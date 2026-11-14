namespace Cilica.AsmIO;

using System.Reflection.Metadata;

partial class CustomAttrib
{
    private byte[] EncodeBlob()
    {
        var writer = new BlobBuilder();
        writer.WriteUInt16(0x0001); // prolog

        EncodeFixedArgs(writer);
        EncodeNamedArgs(writer);
        return writer.ToArray();
    }

    private void EncodeFixedArgs(BlobBuilder writer)
    {
        for (int i = 1; i < Constructor.ParamSig.Count; i++) {
            EncodeElement(writer, Constructor.ParamSig[i].Type, _fixedArgs[i - 1]);
        }
    }

    private void EncodeNamedArgs(BlobBuilder writer)
    {
        writer.WriteUInt16(checked((ushort)_namedArgs.Length));

        foreach (var arg in _namedArgs) {
            writer.WriteByte(arg.IsField ? kField : kProp);
            EncodeElementType(writer, arg.Type);
            writer.WriteSerializedString(arg.Name);
            EncodeElement(writer, arg.Type, arg.Value);
        }
    }

    /// <summary> Writes the element type of a named argument. See ECMA-335 II.23.3. </summary>
    private void EncodeElementType(BlobBuilder writer, TypeDesc type)
    {
        var sys = ParentModule.Resolver.SysTypes;

        if (type is ArrayType) {
            writer.WriteByte((byte)SerializationTypeCode.SZArray);
            EncodeElementType(writer, type.ElemType!);
            return;
        }
        if (type == sys.Type) {
            writer.WriteByte((byte)SerializationTypeCode.Type);
            return;
        }
        if (type == PrimType.Object) {
            writer.WriteByte((byte)SerializationTypeCode.TaggedObject);
            return;
        }
        
        // Enums have to be handled before the type kind, because an enum's kind is its underlying type.
        if (type.IsEnum) {
            writer.WriteByte((byte)SerializationTypeCode.Enum);
            writer.WriteSerializedString(SerializeType(type, ParentModule));
            return;
        }

        // Boolean..String are the only primitives that are valid attribute element types,
        // and their serialization codes match their signature codes.
        if (type.Kind is >= TypeKind.Bool and <= TypeKind.String) {
            writer.WriteByte((byte)type.Kind.ToSrmTypeCode());
            return;
        }

        throw new NotSupportedException($"Cannot encode '{type}' as a custom attribute element type.");
    }

    private void EncodeElement(BlobBuilder writer, TypeDesc type, object? value)
    {
        if (type is ArrayType) {
            if (value == null) {
                writer.WriteInt32(-1);
                return;
            }
            var array = (Array)value!;

            writer.WriteInt32(array.Length);
            for (int i = 0; i < array.Length; i++) {
                EncodeElement(writer, type.ElemType!, array.GetValue(i));
            }
            return;
        }
        else if (IsSystemType(type)) {
            // 'System.Type' arguments are stored as the name of the type they refer to.
            var typeName = value switch {
                null => null,
                TypeDesc typeValue => SerializeType(typeValue, ParentModule),
                _ => throw new ArgumentException(
                    $"Expected a '{nameof(TypeDesc)}' value for a 'System.Type' argument but got '{value.GetType().Name}'.", nameof(value))
            };
            writer.WriteSerializedString(typeName);
            return;
        }
#pragma warning disable format
        else switch (type.Kind) {
            case TypeKind.Bool:   writer.WriteBoolean((bool)value!); break;
            case TypeKind.Char:   writer.WriteUInt16((char)value!); break;
            case TypeKind.SByte:  writer.WriteSByte((sbyte)value!); break;
            case TypeKind.Byte:   writer.WriteByte((byte)value!); break;
            case TypeKind.Int16:  writer.WriteInt16((short)value!); break;
            case TypeKind.UInt16: writer.WriteUInt16((ushort)value!); break;
            case TypeKind.Int32:  writer.WriteInt32((int)value!); break;
            case TypeKind.UInt32: writer.WriteUInt32((uint)value!); break;
            case TypeKind.Int64:  writer.WriteInt64((long)value!); break;
            case TypeKind.UInt64: writer.WriteUInt64((ulong)value!); break;
            case TypeKind.Single: writer.WriteSingle((float)value!); break;
            case TypeKind.Double: writer.WriteDouble((double)value!); break;
            case TypeKind.String: writer.WriteSerializedString((string?)value); break;
            // Boxed value
            case var _ when type == PrimType.Object:
                // TODO: Encode boxed values, which are prefixed with their own element type.
                throw new NotImplementedException();

            default: throw new NotSupportedException();
        }
    }

    private static bool IsSystemType(TypeDesc type)
    {
        return type is TypeDef def && def == def.Module.Resolver.SysTypes.Type;
    }
}