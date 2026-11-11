namespace Cilica.Tests.Passes;

using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Runtime.Loader;
using Cilica.AsmIO;
using Cilica.IR;
using Cilica.IR.Utils;
using Cilica.Passes;

partial class FoldReflectionTests
{
    [Theory]
    [InlineData("Value", true)]
    [InlineData("Missing", false)]
    public void LookupsPreserveMembersWithCustomModifiedGenericArguments(string name, bool exists)
    {
        using var directory = new TemporaryDirectory();
        string path = Path.Combine(directory.Path, "ModifiedArguments_" + Guid.NewGuid().ToString("N") + ".dll");
        Utils.WriteMetadataModule(path, metadata => {
            var identity = typeof(object).Assembly.GetName();
            var core = metadata.AddAssemblyReference(metadata.GetOrAddString(identity.Name!), identity.Version!,
                default, metadata.GetOrAddBlob(identity.GetPublicKeyToken()!), 0, default);
            var objectType = metadata.AddTypeReference(core, metadata.GetOrAddString("System"), metadata.GetOrAddString("Object"));
            var list = metadata.AddTypeReference(core, metadata.GetOrAddString("System.Collections.Generic"), metadata.GetOrAddString("List`1"));
            var modifier = metadata.AddTypeReference(core, metadata.GetOrAddString("System.Runtime.CompilerServices"), metadata.GetOrAddString("IsConst"));
            metadata.AddTypeDefinition(TypeAttributes.Public, default, metadata.GetOrAddString("Target"), objectType,
                MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
            var signature = new BlobBuilder();
            var argument = new BlobEncoder(signature).FieldSignature().GenericInstantiation(list, 1, false).AddArgument();
            argument.CustomModifiers().AddModifier(modifier, true);
            argument.Int32();
            metadata.AddFieldDefinition(FieldAttributes.Public | FieldAttributes.Static,
                metadata.GetOrAddString("Value"), metadata.GetOrAddBlob(signature));
        });

        using var image = new MemoryStream(File.ReadAllBytes(path));
        var original = AssemblyLoadContext.Default.LoadFromStream(image).GetType("Target")!;
        Assert.Equal(exists, original.GetField(name) != null);

        using var resolver = new ModuleResolver();
        resolver.AddTrustedSearchPaths();
        var target = resolver.Load(path).FindType(null, "Target")!;
        var body = Utils.CreateTestMethod(resolver, PrimType.Bool);
        var typeType = resolver.Import(typeof(Type));
        var getType = typeType.FindMethod("GetTypeFromHandle", new MethodSig(typeType, [resolver.Import(typeof(RuntimeTypeHandle))], false));
        var getField = typeType.FindMethod("GetField", new MethodSig(resolver.Import(typeof(FieldInfo)), [PrimType.String], true));
        var builder = new IRBuilder(body.CreateBlock());
        var reflected = builder.CreateCall(getType, builder.Emit(new CilIntrinsic.LoadHandle(resolver, target)));
        var lookup = builder.CreateCallVirt(getField, reflected, ConstString.Create(name));
        builder.CreateReturn(builder.Emit(new CompareInst(CompareOp.Ne, lookup, ConstNull.Create())));

        var compilation = new Compilation(body.Definition.Module, new VoidLogger(), new() { VerifyEachPass = true });
        PassRunner.Run(new FoldReflection(), new MethodTransformContext(compilation, body));
        Assert.Equal(exists, Utils.Compile<Func<bool>>(body)());
    }
}
