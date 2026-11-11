namespace Cilica.Analysis;

using System.Reflection;

/// <summary> Resolves unambiguous, constant reflection lookups against assembly metadata. </summary>
internal sealed partial class ReflectionLookup(ReflectionTypeFacts types, InstructionOrder order)
{
    const BindingFlags SupportedFlags = BindingFlags.Public | BindingFlags.NonPublic |
        BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly | BindingFlags.FlattenHierarchy | BindingFlags.IgnoreCase | BindingFlags.ExactBinding;

    // A null Result is unknown; a null Member is a proven absence.
    public sealed record Result(MemberDesc? Member, TypeDefOrSpec ReflectedType, TypeDefOrSpec? DeclaringType = null)
    {
        public TypeDefOrSpec Owner => DeclaringType ?? ReflectedType;
    }

    readonly Dictionary<CallInst, Result?> _resolvedMembers = new();
    readonly Dictionary<CallInst, TypeDesc?> _resolvedTypes = new();
    readonly Dictionary<CallInst, KnownArray> _lookupArrays = new();
    readonly List<(CallInst Call, bool NonNull)> _lookupFacts = new();
    VirtualDispatch.Cache? _dispatch;

    public MethodDesc? ResolveVirtualMethod(MethodDefOrSpec method, TypeDefOrSpec actualType)
        => (_dispatch ??= new()).Resolve(method, actualType);

    public Result? Resolve(Value value)
    {
        if (value is not CallInst call) return null;
        if (_resolvedMembers.TryGetValue(call, out var result)) return result;
        try {
            result = ResolveMember(call);
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
        type = TypeUtils.GetReflectedType(call) ?? ResolveGenericType(call);
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
            "GetField" or "GetMethod" or "GetProperty"
                when lookup.Method.DeclaringType.IsCorelibType(typeof(Type)) => ResolveNamedMember(lookup),
            _ => null
        };

    private TypeDesc? ResolveGenericType(CallInst call)
    {
        if (!call.IsVirtual || call.NumArgs != 2 || call.Method.Name != "MakeGenericType") return null;
        if (!call.Method.DeclaringType.IsCorelibType(typeof(Type))) return null;
        if (ResolveType(call.Args[0]) is not TypeDef { IsGeneric: true } definition) return null;
        if (!types.CanInspect(definition)) return null;
        if (!TryReadTypeArguments(call, definition.GenericParams, out var arguments, out var array)) return null;
        if (!types.SatisfiesConstraints(definition.GenericParams, arguments, new GenericContext(arguments))) return null;

        _lookupArrays.Add(call, array);
        return definition.GetSpec(arguments);
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

    private bool TryReadTypeArguments(CallInst call, GenericParamType[] parameters, out ImmutableArray<TypeDesc> arguments, out KnownArray array)
    {
        arguments = default;
        if (!KnownArray.TryRead(call.Args[1], call, parameters.Length, order, out array, types.Resolver.Import(typeof(Type)), allowNull: false)) return false;
        return TryResolveTypes(array.Values, out arguments);
    }

    private bool TryResolveTypes(Value[] values, out ImmutableArray<TypeDesc> arguments)
    {
        arguments = default;
        var result = ImmutableArray.CreateBuilder<TypeDesc>(values.Length);
        foreach (var value in values) {
            if (ResolveType(value) is not { } type) return false;
            result.Add(type);
        }
        arguments = result.MoveToImmutable();
        return true;
    }

    public static bool IsMemberLookup(CallInst call)
        => call.Method.Name switch {
            "GetField" or "GetMethod" or "GetProperty" => call.Method.DeclaringType.IsCorelibType(typeof(Type)),
            "get_GetMethod" or "get_SetMethod" or "GetGetMethod" or "GetSetMethod" => call.Method.DeclaringType.IsCorelibType(typeof(PropertyInfo)),
            "MakeGenericMethod" => call.Method.DeclaringType.IsCorelibType(typeof(MethodInfo)),
            _ => false
        };

    public static bool IsTypeLookup(CallInst call)
        => call.Method.Name switch {
            "GetTypeFromHandle" => call.Method.DeclaringType.IsCorelibType(typeof(Type)),
            "GetType" => call.Method.DeclaringType.IsCorelibType(typeof(object)),
            _ => false
        };

    public void ApplyFacts(bool preserveTrimmingDependencies, ICompilationLogger logger)
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
