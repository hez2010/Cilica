namespace Cilica.Analysis;

using System.Reflection;

partial class ReflectionLookup
{
    // A default parameter array means no signature filter; an empty array asks
    // specifically for a member with no parameters.
    readonly record struct MemberQuery(MemberTypes Kind, string Name, BindingFlags Flags,
        ImmutableArray<TypeDesc> ParameterTypes, TypeDesc? PropertyType, int? GenericArity)
    {
        public bool HasParameterTypes => !ParameterTypes.IsDefault;
        public bool HasSignature => HasParameterTypes || PropertyType != null;

        public bool Equals(MemberQuery other)
            => Kind == other.Kind && Name == other.Name && Flags == other.Flags &&
                PropertyType == other.PropertyType && GenericArity == other.GenericArity &&
                HasParameterTypes == other.HasParameterTypes &&
                (!HasParameterTypes || ParameterTypes.SequenceEqual(other.ParameterTypes));

        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.Add(Kind);
            hash.Add(Name);
            hash.Add(Flags);
            hash.Add(PropertyType);
            hash.Add(GenericArity);
            hash.Add(HasParameterTypes);
            if (HasParameterTypes) foreach (var parameter in ParameterTypes) hash.Add(parameter);
            return hash.ToHashCode();
        }
    }

    sealed class MemberCandidate(MemberDesc member, TypeDefOrSpec owner)
    {
        public MemberDesc Member => member;
        public TypeDefOrSpec Owner => owner;
        TypeDesc[]? _parameters;

        public int ParameterCount => member switch {
            MethodDesc method => method.ParamSig.Count - (method.IsInstance ? 1 : 0),
            PropertyDef property => property.Sig.ParamTypes.Count,
            _ => 0
        };
        public TypeDesc[] ParameterTypes => _parameters ??= member switch {
            MethodDesc method => method.ParamSig.Skip(method.IsInstance ? 1 : 0).Select(parameter => parameter.Type).ToArray(),
            PropertyDef property => property.Sig.ParamTypes.Select(parameter => parameter.Type.GetSpec(new GenericContext(owner))).ToArray(),
            _ => []
        };
        public TypeDesc PropertyType => ((PropertyDef)member).Sig.ReturnType.Type.GetSpec(new GenericContext(owner));
        public bool HasOpenParameters => ParameterTypes.Any(type => type.IsUnboundGeneric);
        public bool HasMethodVariables => member is MethodDesc { IsGeneric: true } && ParameterTypes.Any(ContainsMethodVariable);
        public Result AsResult(TypeDefOrSpec reflectedType) => new(member, reflectedType, owner);
    }

    sealed class MemberIndex
    {
        public readonly Dictionary<(MemberTypes Kind, bool IgnoreCase), ILookup<string, MemberDesc>> Names = new();
        public readonly Dictionary<MemberQuery, Result?> Selections = new();
        public TypeDefOrSpec[]? FieldOwners;
    }

    readonly Dictionary<TypeDefOrSpec, MemberIndex> _indexes = new(ReferenceEqualityComparer.Instance);

    private MemberIndex Index(TypeDefOrSpec type) => _indexes.GetOrAddRef(type) ??= new();

    private Result? ResolveNamedMember(CallInst lookup)
    {
        if (lookup.IsStatic || lookup.Method.Name != "GetConstructor" && lookup.Args[1] is not ConstString) return null;
        if (ResolveType(lookup.Args[0]) is not TypeDefOrSpec type || !types.CanInspect(type) || type.Attribs.HasFlag(TypeAttributes.Import)) return null;
        if (!TryReadMemberQuery(lookup, out var query, out var array)) return null;
        if (query.Kind != MemberTypes.Constructor && !query.Flags.HasFlag(BindingFlags.DeclaredOnly) && !types.CanInspectHierarchy(type)) return null;
        if (type is TypeDef { IsGeneric: true } && query.HasSignature &&
            !(query.HasParameterTypes && query.ParameterTypes.IsEmpty && query.PropertyType == null)) return null;

        var cache = Index(type).Selections;
        if (!cache.TryGetValue(query, out var result)) {
            result = SelectMember(type, query);
            cache.Add(query, result);
        }
        if (result != null && array != null) _lookupArrays.Add(lookup, array);
        return result;
    }

    private bool TryReadMemberQuery(CallInst lookup, out MemberQuery query, out KnownArray? array)
    {
        query = default;
        array = null;
        var kind = lookup.Method.Name switch {
            "GetField" => MemberTypes.Field,
            "GetMethod" => MemberTypes.Method,
            "GetProperty" => MemberTypes.Property,
            "GetConstructor" => MemberTypes.Constructor,
            _ => throw new UnreachableException()
        };
        var flags = BindingFlags.Public | BindingFlags.Instance;
        if (kind != MemberTypes.Constructor) flags |= BindingFlags.Static;
        TypeDesc? propertyType = null;
        ImmutableArray<TypeDesc> parameters = default;
        int? genericArity = null;

        for (int i = kind == MemberTypes.Constructor ? 1 : 2; i < lookup.NumArgs; i++) {
            var parameter = lookup.Method.ParamSig[i].Type;
            var value = lookup.Args[i];
            if (parameter.IsCorelibType(typeof(BindingFlags))) {
                if (value is not ConstInt bits) return false;
                flags = (BindingFlags)bits.Value;
                if ((flags & ~SupportedFlags) != 0) return false;
            } else if (parameter == PrimType.Int32 && kind == MemberTypes.Method) {
                if (value is not ConstInt { Value: >= 0 and <= int.MaxValue } arity) return false;
                genericArity = (int)arity.Value;
            } else if (parameter is ArrayType { ElemType: var element } && element.IsCorelibType(typeof(Type))) {
                if (!TryReadParameterTypes(value, lookup, out parameters, out array)) return false;
            } else if (parameter.IsCorelibType(typeof(Type)) && kind == MemberTypes.Property) {
                if (value is ConstNull) continue;
                propertyType = ResolveType(value);
                if (propertyType == null) return false;
            } else if (parameter.IsCorelibType(typeof(CallingConventions))) {
                if (value is not ConstInt convention || convention.Value is not ((int)CallingConventions.Any or (int)CallingConventions.Standard)) return false;
            } else if (parameter.IsCorelibType(typeof(Binder)) || parameter is ArrayType { ElemType: var modifier } && modifier.IsCorelibType(typeof(ParameterModifier))) {
                if (value is not ConstNull) return false;
            } else return false;
        }

        query = new(kind, kind == MemberTypes.Constructor ? ".ctor" : ((ConstString)lookup.Args[1]).Value, flags, parameters, propertyType, genericArity);
        return true;
    }

    private bool TryReadParameterTypes(Value value, CallInst consumer, out ImmutableArray<TypeDesc> parameters, out KnownArray array)
    {
        parameters = default;
        array = null!;
        // CLI parameter sequences cannot describe a method with more parameters.
        // Retain validation of larger runtime arrays without materializing them.
        int count = value is CilIntrinsic.NewArray { Args: [ConstInt { Value: >= 0 and <= ushort.MaxValue } length] } ? (int)length.Value : 0;
        if (!KnownArray.TryRead(value, consumer, count, order, out array, types.Resolver.Import(typeof(Type)), allowNull: false)) return false;
        return TryResolveTypes(array.Values, out parameters);
    }

    private Result? SelectMember(TypeDefOrSpec type, MemberQuery query)
    {
        var candidates = CollectCandidates(type, query);
        if (candidates == null) return null;
        if (candidates.Count == 0) return new(null, type);
        if (query.Kind == MemberTypes.Field) return SelectField(type, candidates);
        if (query.HasSignature) {
            candidates = SelectSignature(query, candidates);
            if (candidates == null) return null;
        }
        return SelectMostDerived(type, query.Kind, candidates);
    }

    private List<MemberCandidate>? CollectCandidates(TypeDefOrSpec type, MemberQuery query)
    {
        var candidates = new List<MemberCandidate>();
        var visibleProperties = new List<PropertyDef>();
        foreach (var owner in CandidateOwners(type, query)) {
            if (owner != type && !types.CanInspect(owner) && !owner.IsCorelibType()) return null;
            bool inherited = owner != type;
            foreach (var member in NamedMembers(owner, query)) {
                if (member is MethodDefOrSpec method) {
                    if (inherited && method.Attribs.HasFlag(MethodAttributes.Virtual)) {
                        if (TypeUtils.HasClassMethodImpls(method) || ResolveVirtualMethod(method, type) is not MethodDefOrSpec target) return null;
                        if (!VirtualDispatch.SameMethod(target, method)) continue;
                    }
                    if (query.GenericArity is { } arity && method.GenericParams.Count != arity) continue;
                }
                if (member is PropertyDef property) {
                    var visible = IsVisibleProperty(property, owner, type, visibleProperties);
                    if (visible == null) return null;
                    if (!visible.Value) continue;
                    visibleProperties.Add(property);
                }
                if (!MatchesBindingFlags(member, query.Flags, inherited)) continue;
                var candidate = new MemberCandidate(member, owner);
                if (query.HasParameterTypes && candidate.ParameterCount != query.ParameterTypes.Length) continue;
                candidates.Add(candidate);
            }
        }
        return candidates;
    }

    private bool? IsVisibleProperty(PropertyDef property, TypeDefOrSpec owner, TypeDefOrSpec reflectedType, List<PropertyDef> visible)
    {
        bool inherited = owner != reflectedType;
        var accessors = PropertyAccessors(property, inherited).ToArray();
        if (accessors.Length == 0) return false;
        // Runtime property hiding compares metadata signatures before generic
        // substitution and before public/nonpublic binding flags are applied.
        if (visible.Any(prior => prior.Name == property.Name && prior.Sig.Equals(property.Sig))) return false;
        var representative = accessors.FirstOrDefault(accessor => accessor.IsPublic);
        if (inherited && representative?.Attribs.HasFlag(MethodAttributes.Virtual) == true) {
            var bound = owner is TypeSpec ? representative.GetSpec(new GenericContext(owner)) : representative;
            if (TypeUtils.HasClassMethodImpls(bound) || ResolveVirtualMethod(bound, reflectedType) is not MethodDefOrSpec target) return null;
            if (visible.Any(prior => prior.Getter == target.Definition || prior.Setter == target.Definition)) return false;
        }
        return true;
    }

    private static Result? SelectField(TypeDefOrSpec type, List<MemberCandidate> candidates)
    {
        // Class fields take precedence over interface fields. Within a class
        // hierarchy the first candidate is the nearest declaring type.
        bool hasClassField = candidates.Any(candidate => !candidate.Owner.IsInterface);
        var eligible = hasClassField ? candidates.Where(candidate => !candidate.Owner.IsInterface) : candidates;
        var selected = eligible.First();
        if (eligible.Skip(1).Any(candidate => candidate.Owner == selected.Owner || selected.Owner.IsInterface)) return null;
        return selected.AsResult(type);
    }

    private List<MemberCandidate>? SelectSignature(MemberQuery query, List<MemberCandidate> candidates)
    {
        // Different signature variables can become equal at runtime. An
        // overload set involving them needs the runtime binder.
        if (candidates.Count > 1 && (query.HasParameterTypes && query.ParameterTypes.Any(type => type.IsUnboundGeneric) ||
            candidates.Any(candidate => candidate.HasOpenParameters))) return null;
        var exact = candidates.Where(candidate => MatchesExactSignature(query, candidate)).ToList();
        if (exact.Count != 0) return exact;

        // A unique compatible method needs no overload ranking. Larger
        // conversion-based overload sets remain the runtime binder's job.
        return candidates.Count == 1 && IsCompatibleMethod(query, candidates[0]) ? candidates : null;
    }

    private static bool MatchesExactSignature(MemberQuery query, MemberCandidate candidate)
        => !candidate.HasMethodVariables &&
            (!query.HasParameterTypes || candidate.ParameterTypes.SequenceEqual(query.ParameterTypes)) &&
            (query.PropertyType == null || candidate.PropertyType == query.PropertyType);

    private bool IsCompatibleMethod(MemberQuery query, MemberCandidate candidate)
    {
        if (query.Kind is not (MemberTypes.Method or MemberTypes.Constructor) || query.Flags.HasFlag(BindingFlags.ExactBinding) || candidate.HasMethodVariables) return false;
        for (int i = 0; i < candidate.ParameterCount; i++) {
            var source = query.ParameterTypes[i];
            var target = candidate.ParameterTypes[i];
            if (!types.IsAssignable(source, target) && (target.IsEnum || !ReflectionTypeFacts.CanWidenPrimitive(source, target))) return false;
        }
        return true;
    }

    private static Result? SelectMostDerived(TypeDefOrSpec type, MemberTypes kind, List<MemberCandidate> candidates)
    {
        var selected = candidates[0];
        if (candidates.Count > 1) {
            if (kind != MemberTypes.Method || selected.HasOpenParameters) return null;
            if (candidates.Skip(1).Any(candidate => candidate.Owner == selected.Owner ||
                !candidate.ParameterTypes.SequenceEqual(selected.ParameterTypes))) return null;
        }
        return selected.AsResult(type);
    }

    private IEnumerable<TypeDefOrSpec> CandidateOwners(TypeDefOrSpec type, MemberQuery query)
    {
        if (query.Kind == MemberTypes.Constructor || query.Flags.HasFlag(BindingFlags.DeclaredOnly)) return [type];
        if (query.Kind != MemberTypes.Field || !query.Flags.HasFlag(BindingFlags.FlattenHierarchy)) return Hierarchy(type);
        var index = Index(type);
        if (index.FieldOwners == null) {
            var owners = Hierarchy(type).ToList();
            var seen = new HashSet<TypeDefOrSpec>(owners);
            for (int i = 0; i < owners.Count; i++) {
                foreach (var itf in owners[i].Interfaces.OfType<TypeDefOrSpec>()) if (seen.Add(itf)) owners.Add(itf);
            }
            index.FieldOwners = owners.ToArray();
        }
        return index.FieldOwners;
    }

    private static IEnumerable<TypeDefOrSpec> Hierarchy(TypeDefOrSpec type)
    {
        for (var owner = type; owner != null; owner = owner.BaseType) yield return owner;
    }

    private IEnumerable<MemberDesc> NamedMembers(TypeDefOrSpec type, MemberQuery query)
    {
        var cache = Index(type).Names;
        bool ignoreCase = query.Flags.HasFlag(BindingFlags.IgnoreCase);
        var key = (query.Kind, ignoreCase);
        if (!cache.TryGetValue(key, out var names)) {
            IEnumerable<MemberDesc> members = query.Kind switch {
                MemberTypes.Field => type.Fields,
                MemberTypes.Method => type.Methods.Where(method => !method.IsConstructor && !method.IsStaticConstructor),
                MemberTypes.Constructor => type.Methods.Where(method => method.IsConstructor || method.IsStaticConstructor),
                MemberTypes.Property => type.Definition.Properties,
                _ => throw new UnreachableException()
            };
            names = members.ToLookup(member => query.Kind == MemberTypes.Constructor ? ".ctor" : member.Name,
                ignoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            cache.Add(key, names);
        }
        return names[query.Name];
    }

    private static bool ContainsMethodVariable(TypeDesc type) => type switch {
        GenericParamType parameter => parameter.IsMethodParam,
        CompoundType compound => ContainsMethodVariable(compound.ElemType),
        FuncPtrType pointer => ContainsMethodVariable(pointer.Signature.ReturnType.Type) || pointer.Signature.ParamTypes.Any(parameter => ContainsMethodVariable(parameter.Type)),
        _ => type.GenericParams.Any(ContainsMethodVariable)
    };

    private static IEnumerable<MethodDef> PropertyAccessors(PropertyDef property, bool inherited)
        => new[] { property.Getter, property.Setter }.OfType<MethodDef>().Concat(property.OtherAccessors)
            .Where(method => !inherited || (method.Attribs & MethodAttributes.MemberAccessMask) != MethodAttributes.Private);

    private static bool MatchesBindingFlags(MemberDesc member, BindingFlags flags, bool inherited)
    {
        bool isPublic, isStatic, isPrivate;
        switch (member) {
            case FieldDesc field:
                isPublic = (field.Attribs & FieldAttributes.FieldAccessMask) == FieldAttributes.Public;
                isPrivate = (field.Attribs & FieldAttributes.FieldAccessMask) == FieldAttributes.Private;
                isStatic = field.IsStatic;
                break;
            case MethodDesc method:
                isPublic = method.IsPublic;
                isPrivate = (method.Attribs & MethodAttributes.MemberAccessMask) == MethodAttributes.Private && !method.Attribs.HasFlag(MethodAttributes.Virtual);
                isStatic = method.IsStatic;
                break;
            case PropertyDef property:
                var accessors = PropertyAccessors(property, inherited).ToArray();
                if (accessors.Length == 0) return false;
                isPublic = accessors.Any(accessor => accessor.IsPublic);
                isPrivate = false;
                isStatic = accessors.Any(accessor => accessor.IsStatic);
                break;
            default: throw new UnreachableException();
        }
        return !(inherited && (isPrivate || isStatic && !flags.HasFlag(BindingFlags.FlattenHierarchy))) &&
            flags.HasFlag(isPublic ? BindingFlags.Public : BindingFlags.NonPublic) &&
            flags.HasFlag(isStatic ? BindingFlags.Static : BindingFlags.Instance);
    }
}
