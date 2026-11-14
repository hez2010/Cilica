namespace Cilica.Tests.AsmIO;

using Cilica.AsmIO;

[Collection("ModuleResolver")]
public class CustomAttribTests
{
    readonly ModuleResolver _modResolver;

    public CustomAttribTests(ModuleResolverFixture mrf)
    {
        _modResolver = mrf.Resolver;
    }

    [Fact]
    public void DecodeCase1()
    {
        var sys = _modResolver.SysTypes;

        var type = _modResolver.Resolve("TestAsm").FindType(null, "CustomAttribs")!;
        var attrib = type.Methods.First(m => m.Name == "DecodeCase1").GetCustomAttribs().First();

        Assert.Equal(type, ((TypeDef)attrib.Type).DeclaringType);
        Assert.Equal(5, attrib.Args.Length);
        Assert.Equal(9, attrib.Properties.Length);

        Assert.Equal(45, attrib.Args[0]);
        Assert.Equal("CtorStr", attrib.Args[1]);
        Assert.Equal(sys.String, attrib.Args[2]);
        Assert.Equal(new int[] { 1, 2, 3 }, (int[]?)attrib.Args[3]);
        Assert.Equal(150, attrib.Args[4]);

        var listEnumeratorArray = _modResolver.Import(typeof(List<string>.Enumerator)).CreateArray();

        CheckNamed("F_Type", sys.Type, sys.Int32);
        CheckNamed("F_Int", PrimType.Int32, 550);
        CheckNamed("F_Str", PrimType.String, "lorem");
        CheckNamed("F_Enum", _modResolver.Import(typeof(DayOfWeek))!, (int)DayOfWeek.Friday);
        CheckNamed("F_ByteArr", PrimType.Byte.CreateArray(), new byte[] { 1, 2, 3, 255 });
        CheckNamed("F_StrArr", PrimType.String.CreateArray(), new string[] { "ipsum", "dolor" });
        CheckNamed("F_TypeArr", sys.Type.CreateArray(), new TypeDesc[] { sys.Int32, sys.String, listEnumeratorArray });
        CheckNamed("F_Boxed", PrimType.Object, 54.0);
        CheckNamed("P_Long", PrimType.Int64, 0xCAFE_1234L);

        void CheckNamed(string name, TypeDesc type, object? value)
        {
            var prop = attrib!.GetProperty(name)!;
            Assert.Equal(name, prop.Name);
            Assert.Equal(type, prop.Type);
            Assert.Equal(value, prop.Value);
        }
    }

    [Fact]
    public void SerializeTypeEmitsNestedAndGenericNames()
    {
        var module = _modResolver.Resolve("TestAsm");

        var listEnumerator = _modResolver.Import(typeof(List<string>.Enumerator))!;
        Assert.Equal(
            "System.Collections.Generic.List`1+Enumerator[[System.String, " + _modResolver.SysTypes.String.Module.AsmName.FullName + "]], " +
                listEnumerator.Module.AsmName.FullName,
            CustomAttrib.SerializeType(listEnumerator, module));

        var listOfInt = _modResolver.Import(typeof(List<int>))!;
        Assert.Equal(
            "System.Collections.Generic.List`1[[System.Int32, " + _modResolver.SysTypes.Int32.Module.AsmName.FullName + "]], " +
                listOfInt.Module.AsmName.FullName,
            CustomAttrib.SerializeType(listOfInt, module));
    }

    [Fact]
    public void SerializeTypeOmitsQualifierForOwnModule()
    {
        var module = _modResolver.Resolve("TestAsm");
        var ownType = module.FindType(null, "CustomAttribs")!;

        Assert.Equal("CustomAttribs", CustomAttrib.SerializeType(ownType, module));
        Assert.Equal("CustomAttribs", CustomAttrib.SerializeType(ownType));
    }

    [Fact]
    public void SerializeTypeEmitsCompoundTypes()
    {
        var module = _modResolver.Resolve("TestAsm");
        var int32 = _modResolver.SysTypes.Int32;
        var corelib = ", " + int32.Module.AsmName.FullName;

        // The assembly qualifier is appended for the top-level type, so compound types inherit it from their element type.
        Assert.Equal("System.Int32[]" + corelib, CustomAttrib.SerializeType(int32.CreateArray(), module));
        Assert.Equal("System.Int32[][]" + corelib, CustomAttrib.SerializeType(int32.CreateArray().CreateArray(), module));
        Assert.Equal("System.Int32&" + corelib, CustomAttrib.SerializeType(int32.CreateByref(), module));
        Assert.Equal("System.Int32*" + corelib, CustomAttrib.SerializeType(int32.CreatePointer(), module));
        Assert.Equal("System.Int32[,]" + corelib, CustomAttrib.SerializeType(new MDArrayType(int32, 2, default, default), module));

        // Primitives that are not bound to a TypeDef still serialize to their corelib name.
        Assert.Equal("System.Int32" + corelib, CustomAttrib.SerializeType(PrimType.Int32, module));
    }

    [Fact]
    public void SerializeParseRoundtrip()
    {
        var module = _modResolver.Resolve("TestAsm");
        var int32 = _modResolver.SysTypes.Int32;

        TypeDesc[] types = [
            _modResolver.Import(typeof(List<string>.Enumerator))!,
            _modResolver.Import(typeof(List<int>))!,
            _modResolver.Import(typeof(Dictionary<string, List<byte>>))!,
            int32.CreateArray(),
            int32.CreateArray().CreateArray(),
            int32.CreateByref(),
            int32.CreatePointer(),
            int32,
        ];

        foreach (var type in types) {
            string name = CustomAttrib.SerializeType(type, module);
            var parsed = CustomAttrib.ParseSerializedType(module, name);
            Assert.Equal(type, parsed);
        }
    }

    [Fact]
    public void SerializeTypeRejectsUnrepresentableTypes()
    {
        var module = _modResolver.Resolve("TestAsm");

        Assert.Throws<NotSupportedException>(() => CustomAttrib.SerializeType(VectorType.Create(PrimType.Int32, 4), module));
        Assert.Throws<NotSupportedException>(() => CustomAttrib.SerializeType(GenericParamType.GetUnboundT(0), module));
    }
}