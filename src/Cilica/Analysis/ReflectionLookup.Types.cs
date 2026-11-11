namespace Cilica.Analysis;

using System.Reflection.Metadata;

partial class ReflectionLookup
{
    private TypeDesc? ResolveTypeConstruction(CallInst call)
    {
        // Nonvirtual calls execute Type's throwing base implementations.
        if (!IsTypeConstruction(call) || !call.IsVirtual) return null;
        if (ResolveType(call.Args[0]) is not { } type || !types.CanInspectArgument(type)) return null;

        return call.Method.Name switch {
            "MakeGenericType" => ResolveGenericType(call, type),
            "MakeArrayType" => ResolveArrayType(call, type),
            "MakePointerType" when type is not ByrefType => type.CreatePointer(),
            "MakeByRefType" when type is not ByrefType => type.CreateByref(),
            "MakeFunctionPointerType" => ResolveFunctionPointerType(call, type),
            _ => null
        };
    }

    private TypeDesc? ResolveGenericType(CallInst call, TypeDesc type)
    {
        if (type is not TypeDef { IsGeneric: true } definition || !types.CanInspect(definition)) return null;
        if (!TryReadTypeArguments(call, definition.GenericParams, out var arguments, out var array)) return null;
        if (!types.SatisfiesConstraints(definition.GenericParams, arguments, new GenericContext(arguments))) return null;

        _lookupArrays.Add(call, array);
        return definition.GetSpec(arguments);
    }

    private TypeDesc? ResolveArrayType(CallInst call, TypeDesc element)
    {
        if (element is ByrefType || element.Kind is TypeKind.Void or TypeKind.TypedRef || types.MayBeByRefLike(element)) return null;
        if (call.NumArgs == 1) return element.CreateArray();
        // The runtime distinguishes T[] from the variable-bound rank-one T[*].
        // Array ranks outside the CLR's supported range retain their exceptions.
        return call.Args[1] is ConstInt { Value: >= 1 and <= 32 } rank
            ? new MDArrayType(element, (int)rank.Value, [], []) : null;
    }

    private TypeDesc? ResolveFunctionPointerType(CallInst call, TypeDesc result)
    {
        if (call.Args[2] is not ConstInt unmanaged || !ReflectionTypeFacts.IsRuntimeType(result)) return null;
        if (!TryReadParameterTypes(call.Args[1], call, out var parameters, out var array, allowNull: true)) return null;
        if (parameters.Any(parameter => parameter.Kind == TypeKind.Void || !ReflectionTypeFacts.IsRuntimeType(parameter) || !types.CanInspectArgument(parameter))) return null;

        var convention = unmanaged.Value != 0 ? SignatureCallingConvention.Unmanaged : SignatureCallingConvention.Default;
        var signature = new MethodSig(result, parameters.Select(parameter => new TypeSig(parameter)).ToArray(),
            new SignatureHeader(SignatureKind.Method, convention, SignatureAttributes.None));
        _lookupArrays.Add(call, array);
        return new FuncPtrType(signature);
    }

    private bool TryReadTypeArguments(CallInst call, GenericParamType[] parameters, out ImmutableArray<TypeDesc> arguments, out KnownArray array)
    {
        arguments = default;
        if (!KnownArray.TryRead(call.Args[1], call, parameters.Length, order, out array, types.Resolver.Import(typeof(Type)), allowNull: false)) return false;
        return TryResolveTypes(array.Values, out arguments);
    }

    private bool TryReadParameterTypes(Value value, CallInst consumer, out ImmutableArray<TypeDesc> parameters, out KnownArray array, bool allowNull = false)
    {
        parameters = default;
        // Bound analysis of runtime signature arrays; larger arrays retain their
        // runtime construction and validation without compiler-side allocation.
        int count = value is CilIntrinsic.NewArray { Args: [ConstInt { Value: >= 0 and <= ushort.MaxValue } length] } ? (int)length.Value : 0;
        if (!KnownArray.TryRead(value, consumer, count, order, out array, types.Resolver.Import(typeof(Type)), allowNull: allowNull)) return false;
        return TryResolveTypes(array.Values, out parameters);
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
}
