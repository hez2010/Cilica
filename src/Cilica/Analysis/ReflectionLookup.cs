namespace Cilica.Analysis;

using System.Reflection;

/// <summary> Resolves unambiguous, constant reflection lookups against assembly metadata. </summary>
internal sealed partial class ReflectionLookup(ReflectionTypeFacts types, InstructionOrder order, bool nativeAotCompatibility)
{
    const BindingFlags SupportedFlags = BindingFlags.Public | BindingFlags.NonPublic |
        BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly | BindingFlags.FlattenHierarchy | BindingFlags.IgnoreCase | BindingFlags.ExactBinding;

    // A null Result is unknown; a null Member is a proven absence.
    public sealed record Result(MemberDesc? Member, TypeDefOrSpec ReflectedType, TypeDefOrSpec? DeclaringType = null)
    {
        public TypeDefOrSpec Owner => DeclaringType ?? ReflectedType;
    }

    readonly Dictionary<Value, Result?> _resolvedMembers = new();
    readonly Dictionary<CallInst, TypeDesc?> _resolvedTypes = new();
    readonly Dictionary<CallInst, KnownArray> _lookupArrays = new();
    readonly List<(CallInst Call, bool NonNull)> _lookupFacts = new();
    VirtualDispatch.Cache? _dispatch;

    public MethodDesc? ResolveVirtualMethod(MethodDefOrSpec method, TypeDefOrSpec actualType)
        => (_dispatch ??= new()).Resolve(method, actualType);

    public Result? Resolve(Value value)
    {
        if (_resolvedMembers.TryGetValue(value, out var result)) return result;
        if (value is CilIntrinsic.RefCast or CilIntrinsic.CastClass) {
            var cast = (IntrinsicInst)value;
            var member = Resolve(cast.Args[0]);
            var metadataType = member?.Member switch {
                MethodDesc method => types.Resolver.Import(method.IsConstructor || method.IsStaticConstructor ? typeof(ConstructorInfo) : typeof(MethodInfo)),
                FieldDesc => types.Resolver.Import(typeof(FieldInfo)),
                PropertyDef => types.Resolver.Import(typeof(PropertyInfo)),
                _ => null
            };
            result = metadataType?.IsAssignableTo(cast.ResultType) == true ? member : null;
            _resolvedMembers.Add(value, result);
            return result;
        }
        if (value is not CallInst call) return null;
        try {
            result = ResolveMember(call);
            // A constructed base of an open definition carries that definition's
            // variables. Member descriptors consumed by the pass use the caller's
            // generic context, so these lookups must remain runtime operations.
            if (result is { ReflectedType: TypeDef { IsGeneric: true }, Owner: TypeSpec { IsUnboundGeneric: true } }) result = null;
        } catch (NotSupportedException) {
            // Unsupported metadata signatures make this optional lookup unknown.
            result = null;
        }
        _resolvedMembers.Add(call, result);
        if (result != null) _lookupFacts.Add((call, result.Member != null));
        return result;
    }

    public TypeDesc? ResolveType(Value value)
    {
        if (value is not CallInst call) return null;
        if (_resolvedTypes.TryGetValue(call, out var type)) return type;
        type = TypeUtils.GetReflectedType(call) ?? ResolveTypeConstruction(call);
        if (call.Method.Name == "GetType" && type is TypeDefOrSpec nominal && !types.CanInspect(nominal)) type = null;
        _resolvedTypes.Add(call, type);
        if (type != null) _lookupFacts.Add((call, true));
        return type;
    }

    private Result? ResolveMember(CallInst lookup)
        => lookup.Method.Name switch {
            "get_GetMethod" or "get_SetMethod" or "GetGetMethod" or "GetSetMethod"
                when lookup.Method.DeclaringType.IsCorelibType(typeof(PropertyInfo)) => ResolveAccessor(lookup),
            "MakeGenericMethod"
                when lookup.Method.DeclaringType.IsCorelibType(typeof(MethodInfo)) => ResolveGenericMethod(lookup),
            "GetField" or "GetMethod" or "GetProperty" or "GetConstructor"
                when lookup.Method.DeclaringType.IsCorelibType(typeof(Type)) => ResolveNamedMember(lookup),
            "GetMethodFromHandle" or "GetFieldFromHandle" when IsHandleLookup(lookup) => ResolveHandle(lookup),
            _ => null
        };

    private Result? ResolveHandle(CallInst call)
    {
        if (!call.IsStatic || call.Args[0] is not CilIntrinsic.LoadHandle handle) return null;
        var member = handle.Entity switch {
            MethodDefOrSpec method when call.Method.DeclaringType.IsCorelibType(typeof(MethodBase)) => (MemberDesc)method,
            FieldDefOrSpec field when call.Method.DeclaringType.IsCorelibType(typeof(FieldInfo)) && !field.Attribs.HasFlag(FieldAttributes.Literal) => field,
            _ => null
        };
        if (member?.DeclaringType is not TypeDefOrSpec owner || !types.CanInspect(owner)) return null;

        TypeDefOrSpec reflected;
        if (call.NumArgs == 1) {
            if (owner.IsGeneric) return null;
            reflected = owner;
        } else if (call.NumArgs == 2 && call.Args[1] is CilIntrinsic.LoadHandle { Entity: TypeDefOrSpec type }) {
            // Other type/handle combinations can rebind generic contexts or
            // throw. Only an exact context is needed to recognize emitted IL.
            if (type != owner && (nativeAotCompatibility || member is not MethodDefOrSpec ||
                !types.CanInspectHierarchy(type) || !Hierarchy(type).Contains(owner))) return null;
            reflected = type;
        } else return null;
        return new(member, reflected, owner);
    }

    private Result? ResolveGenericMethod(CallInst lookup)
    {
        if (!lookup.IsVirtual || lookup.NumArgs != 2) return null;
        if (Resolve(lookup.Args[0]) is not { Member: MethodDefOrSpec { IsGeneric: true } method } original) return null;
        if (!types.CanInspect(method.DeclaringType)) return null;
        if (method is MethodSpec { IsBoundGeneric: true }) return null;
        if (!TryReadTypeArguments(lookup, method.Definition.GenericParams, out var arguments, out var array)) return null;

        var context = new GenericContext(method.DeclaringType.GenericParams, arguments);
        if (!types.SatisfiesConstraints(method.Definition.GenericParams, arguments, context)) return null;
        _lookupArrays.Add(lookup, array);
        return new(method.GetSpec(arguments), original.ReflectedType, original.Owner);
    }

    private Result? ResolveAccessor(CallInst lookup)
    {
        var methodInfoType = types.Resolver.Import(typeof(MethodInfo));
        bool? nonPublic;
        if (new MethodSig(methodInfoType, [], true).Matches(lookup.Method)) {
            nonPublic = lookup.Method.Name is "get_GetMethod" or "get_SetMethod";
        } else if (lookup.Method.Name is "GetGetMethod" or "GetSetMethod" &&
            new MethodSig(methodInfoType, [PrimType.Bool], true).Matches(lookup.Method)) {
            nonPublic = lookup.Args[1] is ConstInt flag ? flag.Value != 0 : null;
        } else return null;

        if (Resolve(lookup.Args[0]) is not { Member: PropertyDef } property) return null;
        var result = ResolvePropertyAccessor(property, lookup.Method.Name is "get_SetMethod" or "GetSetMethod");
        if (result?.Member is not MethodDefOrSpec accessor) return result;
        if (accessor.IsPublic || nonPublic == true) return result;
        return nonPublic == false ? new(null, property.ReflectedType) : null;
    }

    public Result? ResolvePropertyAccessor(Result result, bool setter)
    {
        var property = (PropertyDef)result.Member!;
        var definition = setter ? property.Setter : property.Getter;
        if (definition == null || result.Owner != result.ReflectedType &&
            (definition.Attribs & MethodAttributes.MemberAccessMask) == MethodAttributes.Private) return new(null, result.ReflectedType);
        var method = result.Owner is TypeSpec ? definition.GetSpec(new GenericContext(result.Owner)) : definition;
        if (result.Owner != result.ReflectedType && method.Attribs.HasFlag(MethodAttributes.Virtual)) {
            if (ResolveVirtualMethod(method, result.ReflectedType) is not MethodDefOrSpec target) return null;
            method = target;
        }
        return new(method, result.ReflectedType, method.DeclaringType);
    }

    public static bool IsMemberLookup(CallInst call)
        => call.Method.Name switch {
            "GetField" or "GetMethod" or "GetProperty" or "GetConstructor" => call.Method.DeclaringType.IsCorelibType(typeof(Type)),
            "get_GetMethod" or "get_SetMethod" or "GetGetMethod" or "GetSetMethod" => call.Method.DeclaringType.IsCorelibType(typeof(PropertyInfo)),
            "MakeGenericMethod" => call.Method.DeclaringType.IsCorelibType(typeof(MethodInfo)),
            "GetMethodFromHandle" or "GetFieldFromHandle" => IsHandleLookup(call),
            _ => false
        };

    public static bool IsHandleLookup(CallInst call)
        => call.Method.Name == "GetMethodFromHandle" && call.Method.DeclaringType.IsCorelibType(typeof(MethodBase)) ||
           call.Method.Name == "GetFieldFromHandle" && call.Method.DeclaringType.IsCorelibType(typeof(FieldInfo));

    public static bool IsTypeLookup(CallInst call)
        => IsTypeConstruction(call) || (call.Method.Name switch {
            "GetTypeFromHandle" => call.Method.DeclaringType.IsCorelibType(typeof(Type)),
            "GetType" => call.Method.DeclaringType.IsCorelibType(typeof(object)),
            _ => false
        });

    public static bool IsTypeConstruction(CallInst call)
        => call.Method.Name is "MakeArrayType" or "MakePointerType" or "MakeByRefType" or "MakeFunctionPointerType" or "MakeGenericType" &&
            call.Method.DeclaringType.IsCorelibType(typeof(Type));

    public void ApplyFacts(bool preserveTrimmingDependencies, ICompilationLogger logger,
        Action<CallInst, Result> materializeMember, Action<CallInst, TypeDesc> materializeType)
    {
        // Resolve records dependencies first. Visit consumers first so their
        // removal can expose unused prerequisite lookups in the same traversal.
        for (int i = _lookupFacts.Count - 1; i >= 0; i--) {
            var (call, nonNull) = _lookupFacts[i];
            LogFact(call, nonNull, logger);
            if (nonNull) call.ResultIsNonNull = true;
            else call.ReplaceUses(ConstNull.Create());
            if (preserveTrimmingDependencies) continue;

            // Earlier inlining/devirtualization may have separated receiver
            // validation from the consuming call. Successful lookups are non-null.
            if (nonNull) {
                foreach (var check in call.Users().AsEnumerable().OfType<CilIntrinsic.NullCheck>().ToArray()) {
                    check.Remove();
                }
                foreach (var cast in call.Users().AsEnumerable().OfType<CilIntrinsic.RefCast>().ToArray()) {
                    if (cast.NumUses == 0) cast.Remove();
                }
            }
            if (call.NumUses != 0 && _resolvedMembers.TryGetValue(call, out var member) && member?.Member != null) {
                materializeMember(call, member);
            }
            if (call.NumUses != 0 && _resolvedTypes.TryGetValue(call, out var type) && type != null) {
                materializeType(call, type);
            }
            if (call.NumUses != 0) continue;
            // Object.GetType still validates its receiver even if its result is known.
            if (call.Method.Name == "GetType" && !types.IsNonNull(call.Args[0])) continue;
            call.Remove();
            if (_lookupArrays.TryGetValue(call, out var array)) array.RemoveIfUnobserved();
        }
    }

    private void LogFact(CallInst call, bool nonNull, ICompilationLogger logger)
    {
        if (!logger.IsEnabled(LogLevel.Trace)) return;
        if (nonNull && !call.ResultIsNonNull) {
            EntityDesc? target = _resolvedTypes.TryGetValue(call, out var type) ? type : _resolvedMembers[call]!.Member;
            logger.Trace($"Proved reflection lookup '{call}' in '{call.Block}' non-null for '{target}'.");
        } else if (!nonNull && call.NumUses != 0) {
            logger.Trace($"Folded reflection lookup '{call}' in '{call.Block}' to null (no matching member in '{_resolvedMembers[call]!.ReflectedType}').");
        }
    }

}
