namespace Cilica.Tests.AsmIO;

using System.Text;
using Cilica.AsmIO;

[Collection("ModuleResolver")]
public class CustomAttribEncodingTests(ModuleResolverFixture fixture)
{
    /// <summary> The <c>ObsoleteAttribute()</c> constructor, which has no fixed arguments. </summary>
    MethodDesc ParameterlessCtor
        => fixture.Resolver.Import(typeof(ObsoleteAttribute))!
            .Methods.Single(method => method.Name == ".ctor" && method.ParamSig.Count == 1);

    /// <summary> The <c>ObsoleteAttribute(string)</c> constructor, which has a single fixed argument. </summary>
    MethodDesc MessageCtor
        => fixture.Resolver.Import(typeof(ObsoleteAttribute))!
            .Methods.Single(method => method.Name == ".ctor" && method.ParamSig.Count == 2);

    [Fact]
    public void NamedArgsAreEncodedAfterFixedArgs()
    {
        var attrib = new CustomAttrib(MessageCtor,
            ["why"],
            [new CustomAttribProp { Type = PrimType.String, Name = "DiagnosticId", IsField = false, Value = "ID" }]);

        Assert.Equal<byte>([
            0x01, 0x00,                                     // prolog
            0x03, (byte)'w', (byte)'h', (byte)'y',          // fixed arg: "why"
            0x01, 0x00,                                     // named arg count
            0x54,                                           // FIELD_OR_PROP: property
            0x0E,                                           // ELEMENT_TYPE_STRING
            0x0C, .. "DiagnosticId"u8,                      // name
            0x02, (byte)'I', (byte)'D',                     // value
        ], attrib.GetEncodedBlob());
    }

    [Fact]
    public void NamedArgElementTypesMatchTheSpec()
    {
        // Scalars: the element type code is the only prefix.
        Check(PrimType.Bool, true, [0x02], [0x01]);
        Check(PrimType.Char, 'A', [0x03], [0x41, 0x00]);
        Check(PrimType.SByte, (sbyte)-1, [0x04], [0xFF]);
        Check(PrimType.Byte, (byte)200, [0x05], [0xC8]);
        Check(PrimType.Int16, (short)-2, [0x06], [0xFE, 0xFF]);
        Check(PrimType.UInt16, (ushort)65535, [0x07], [0xFF, 0xFF]);
        Check(PrimType.Int32, 550, [0x08], [0x26, 0x02, 0x00, 0x00]);
        Check(PrimType.UInt32, 1u, [0x09], [0x01, 0x00, 0x00, 0x00]);
        Check(PrimType.Int64, 1L, [0x0A], [0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00]);
        Check(PrimType.UInt64, 1ul, [0x0B], [0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00]);
        Check(PrimType.Single, 1.0f, [0x0C], [0x00, 0x00, 0x80, 0x3F]);
        Check(PrimType.Double, 1.0, [0x0D], [0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xF0, 0x3F]);
        Check(PrimType.String, "hi", [0x0E], [0x02, (byte)'h', (byte)'i']);
        Check(PrimType.String, null, [0x0E], [0xFF]);

        // Enums are written as their name, resolved against the attribute's module (CoreLib here).
        Check(fixture.Resolver.Import(typeof(DayOfWeek))!, DayOfWeek.Friday,
            [0x55, 0x10, .. "System.DayOfWeek"u8], [0x05, 0x00, 0x00, 0x00]);

        // Arrays are written as SZARRAY followed by the element type, then the element count.
        Check(PrimType.Byte.CreateArray(), new byte[] { 1, 2 },
            [0x1D, 0x05], [0x02, 0x00, 0x00, 0x00, 0x01, 0x02]);

        // A null array is encoded as a count of -1.
        Check(PrimType.Int32.CreateArray(), null, [0x1D, 0x08], [0xFF, 0xFF, 0xFF, 0xFF]);

        // Boxed values are prefixed with the element type of the value they actually hold.
        Check(PrimType.Object, 54.0, [0x51, 0x0D], [0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x4B, 0x40]);
        Check(PrimType.Object, 550, [0x51, 0x08], [0x26, 0x02, 0x00, 0x00]);
        Check(PrimType.Object, "hi", [0x51, 0x0E], [0x02, (byte)'h', (byte)'i']);
        Check(PrimType.Object, SysTypes.Int32, [0x51, 0x50], [0x0C, .. "System.Int32"u8]);
        Check(PrimType.Object, new byte[] { 1, 2 }, [0x51, 0x1D, 0x05], [0x02, 0x00, 0x00, 0x00, 0x01, 0x02]);

        void Check(TypeDesc type, object? value, byte[] elementType, byte[] valueBytes)
        {
            var attrib = new CustomAttrib(ParameterlessCtor,
                default,
                [new CustomAttribProp { Type = type, Name = "X", IsField = true, Value = value }]);

            Assert.Equal<byte>([
                0x01, 0x00,         // prolog
                0x01, 0x00,         // named arg count
                0x53,               // FIELD_OR_PROP: field
                .. elementType,     // element type
                0x01, (byte)'X',    // name
                .. valueBytes,      // value
            ], attrib.GetEncodedBlob());
        }
    }

    [Fact]
    public void UnsupportedElementTypesThrow()
    {
        // A plain class is not a valid custom attribute argument type.
        var attrib = new CustomAttrib(ParameterlessCtor,
            default,
            [new CustomAttribProp { Type = fixture.Resolver.Import(typeof(Exception))!, Name = "X", IsField = true, Value = null }]);

        Assert.Throws<NotSupportedException>(() => attrib.GetEncodedBlob());
    }

    [Fact]
    public void BoxedNullValuesThrow()
    {
        // A boxed value has to carry its own element type, so there is nothing to write for null.
        Assert.Throws<ArgumentException>(() => EncodeNamedArg(PrimType.Object, null));
    }

    [Fact]
    public void ReencodingADecodedBlobReproducesIt()
    {
        var type = fixture.Resolver.Resolve("TestAsm")!.FindType(null, "CustomAttribs")!;
        var original = type.Methods.First(method => method.Name == "DecodeCase1").GetCustomAttribs().First();

        // The original blob covers fixed args, boxed values, 'System.Type', enums and arrays,
        // so re-encoding it exercises every element type the encoder supports.
        var reencoded = new CustomAttrib(original.Constructor, original.Args, original.Properties);

        Assert.Equal(original.GetEncodedBlob(), reencoded.GetEncodedBlob());
    }

    [Fact]
    public void SystemTypeNamedArgEncodesTheTypeName()
    {
        Assert.Equal<byte>([
            0x01, 0x00,                 // prolog
            0x01, 0x00,                 // named arg count
            0x53,                       // FIELD_OR_PROP: field
            0x50,                       // ELEMENT_TYPE_TYPE
            0x01, (byte)'X',            // name
            0x0C, .. "System.Int32"u8,  // value: the type name
        ], EncodeNamedArg(SysTypes.Type, SysTypes.Int32));
    }

    [Fact]
    public void SystemTypeArrayNamedArgEncodesEveryTypeName()
    {
        Assert.Equal<byte>([
            0x01, 0x00,                 // prolog
            0x01, 0x00,                 // named arg count
            0x53,                       // FIELD_OR_PROP: field
            0x1D, 0x50,                 // SZARRAY of ELEMENT_TYPE_TYPE
            0x01, (byte)'X',            // name
            0x02, 0x00, 0x00, 0x00,     // element count
            0x0C, .. "System.Int32"u8,
            0x0D, .. "System.String"u8,
        ], EncodeNamedArg(SysTypes.Type.CreateArray(), new TypeDesc[] { SysTypes.Int32, SysTypes.String }));
    }

    [Fact]
    public void NullSystemTypeNamedArgEncodesAsANullString()
    {
        Assert.Equal<byte>([
            0x01, 0x00,         // prolog
            0x01, 0x00,         // named arg count
            0x53,               // FIELD_OR_PROP: field
            0x50,               // ELEMENT_TYPE_TYPE
            0x01, (byte)'X',    // name
            0xFF,               // value: null
        ], EncodeNamedArg(SysTypes.Type, null));
    }

    [Fact]
    public void SystemTypeNamedArgRejectsNonTypeValues()
    {
        Assert.Throws<ArgumentException>(() => EncodeNamedArg(SysTypes.Type, 42));
    }

    [Fact]
    public void TypeNamesFromOtherAssembliesAreAssemblyQualified()
    {
        var blob = EncodeNamedArg(SysTypes.Type, fixture.Resolver.Import(typeof(Outer.Inner))!);

        // The value is the last thing in the blob, so the type name ends it.
        Assert.EndsWith($"{typeof(Outer.Inner).FullName}, {typeof(Outer).Assembly.GetName().FullName}",
            Encoding.UTF8.GetString(blob));
    }

    byte[] EncodeNamedArg(TypeDesc type, object? value)
        => new CustomAttrib(ParameterlessCtor,
            default,
            [new CustomAttribProp { Type = type, Name = "X", IsField = true, Value = value }]).GetEncodedBlob();

    SystemTypes SysTypes => fixture.Resolver.SysTypes;

    public class Outer
    {
        public class Inner { }
    }
}
