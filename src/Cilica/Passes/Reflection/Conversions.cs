namespace Cilica.Passes;

using Cilica.Analysis;
using Cilica.IR.Utils;

partial class ReflectionRewriter
{
    private bool CanConvert(Value value, TypeDesc type, bool allowWidening = true)
    {
        if (!_typeFacts.IsBoxable(type)) return false;
        if (value is ConstNull) return type is not GenericParamType || _typeFacts.IsReferenceType(type) || _typeFacts.IsNonNullableValueType(type);
        if (type.IsValueType || type is GenericParamType) {
            if (value is not CilIntrinsic.Box box) return false;
            if (box.SourceType == type) return true;
            if (type.IsCorelibType(typeof(Nullable<>))) return box.SourceType == type.GenericParams[0];
            return allowWidening && ReflectionTypeFacts.CanWidenPrimitive(box.SourceType, type);
        }
        return _typeFacts.IsAssignable(value is CilIntrinsic.Box boxed ? boxed.SourceType : value.ResultType, type);
    }

    private Value ConvertArgument(IRBuilder builder, Value value, TypeDesc type)
    {
        if (!type.IsValueType && type is not GenericParamType) {
            return value is ConstNull || value.ResultType.IsAssignableTo(type) ? value : builder.Emit(new CilIntrinsic.RefCast(type, value));
        }
        if (value is ConstNull) {
            // Reflection allocates an uninitialized value for null. Preserve the
            // allocation's type/module initialization when it can be observable.
            if (type is GenericParamType && _typeFacts.IsNonNullableValueType(type) || type.RequiresTypeInitialization(fieldAccess: true)) {
                var resolver = _typeFacts.Resolver;
                var reflected = CreateTypeReference(builder, type);
                var create = resolver.Import(typeof(System.Runtime.CompilerServices.RuntimeHelpers)).FindMethod("GetUninitializedObject");
                return builder.CreateUnboxObj(type, builder.CreateCall(create, reflected));
            }
            return builder.CreateDefaultOf(type);
        }
        var source = ((CilIntrinsic.Box)value).SourceType;
        if (source == type || type.IsCorelibType(typeof(Nullable<>))) return builder.CreateUnboxObj(type, value);
        Value unboxed = builder.CreateUnboxObj(source, value);
        if (source.Kind == TypeKind.UInt32 && type.StackType == StackType.Long) unboxed = builder.CreateConvert(unboxed, PrimType.UInt64);
        return builder.CreateConvert(unboxed, type, srcUnsigned: source.Kind.IsUnsigned() && type.IsFloat());
    }

    private static Value? ReadConstant(TypeDesc type, object? value) => value switch {
        null => ConstNull.Create(),
        string text => ConstString.Create(text),
        float number => ConstFloat.Create(type, number),
        double number => ConstFloat.Create(type, number),
        ulong number => ConstInt.Create(type, unchecked((long)number)),
        bool or char or sbyte or byte or short or ushort or int or uint or long => ConstInt.Create(type, Convert.ToInt64(value)),
        _ => null
    };
}
