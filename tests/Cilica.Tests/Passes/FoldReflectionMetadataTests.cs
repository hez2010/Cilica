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
    [InlineData("InlinedDefinitionMetadata")]
    [InlineData("InlinedGenericDefinitionMetadata")]
    public void InlinedMemberMetadataPreservesDefinitionAndConstructedContexts(string name)
    {
        using var test = new Case(name);
        var expected = (MemberInfo[])test.Original.Invoke(null, null)!;
        var compilation = new Compilation(test.Method.Module, new VoidLogger(), new() { VerifyEachPass = true });
        var methods = test.Method.DeclaringType.Methods.Where(method => method == test.Method || method.Name == "DefinitionMetadata").ToArray();
        Assert.True(CompilationPipeline.CreateDefault(compilation).Run(methods).Succeeded);
        AssertMetadata(expected, (MemberInfo[])test.Emit().Invoke(null, null)!);
    }

    [Theory]
    [InlineData("MethodMetadata")]
    [InlineData("FieldMetadata")]
    [InlineData("ConstructorMetadata")]
    [InlineData("AccessorMetadata")]
    public void MemberConstructionUsesHandlesAndPreservesMetadataIdentity(string name)
    {
        foreach (bool defaultPipeline in new[] { false, true }) {
            using var test = new Case(name);
            var expected = (MemberInfo[])test.Original.Invoke(null, null)!;
            test.Optimize(defaultPipeline: defaultPipeline, nativeAotCompatibility: true);
            Assert.False(test.Method.ILBody!.Instructions.Any(instruction => IsMemberDiscovery(instruction.Operand)),
                string.Join("\n", test.Method.ILBody.Instructions));
            AssertMetadata(expected, (MemberInfo[])test.Emit().Invoke(null, null)!);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InheritedMethodConstructionPreservesTheReflectedType(bool nativeAotCompatibility)
    {
        foreach (bool defaultPipeline in new[] { false, true }) {
            using var test = new Case("InheritedMethodMetadata");
            var expected = (MemberInfo[])test.Original.Invoke(null, null)!;
            test.Optimize(defaultPipeline: defaultPipeline, nativeAotCompatibility: nativeAotCompatibility);
            Assert.Equal(nativeAotCompatibility, test.Method.ILBody!.Instructions.Any(instruction => IsMemberDiscovery(instruction.Operand)));
            AssertMetadata(expected, (MemberInfo[])test.Emit().Invoke(null, null)!);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MetadataWithoutAnEquivalentHandleRetainsDiscovery(bool defaultPipeline)
    {
        using var test = new Case("MetadataWithoutHandles");
        var expected = (MemberInfo[])test.Original.Invoke(null, null)!;
        test.Optimize(defaultPipeline: defaultPipeline);
        Assert.Contains(test.Method.ILBody!.Instructions, instruction => IsMemberDiscovery(instruction.Operand));
        AssertMetadata(expected, (MemberInfo[])test.Emit().Invoke(null, null)!);
    }

    [Theory]
    [InlineData("MethodMetadata")]
    [InlineData("FieldMetadata")]
    [InlineData("ConstructorMetadata")]
    [InlineData("AccessorMetadata")]
    public void MemberConstructionRetainsRequestedTrimmingDependencies(string name)
    {
        using var test = new Case(name);
        var expected = (MemberInfo[])test.Original.Invoke(null, null)!;
        test.Optimize(preserveTrimmingDependencies: true);
        Assert.Contains(test.Method.ILBody!.Instructions, instruction => IsMemberDiscovery(instruction.Operand));
        Assert.DoesNotContain(test.Method.ILBody.Instructions, instruction => instruction.Operand is MethodDesc { Name: "GetMethodFromHandle" or "GetFieldFromHandle" });
        AssertMetadata(expected, (MemberInfo[])test.Emit().Invoke(null, null)!);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MaterializedMethodsRetainRuntimeArgumentValidation(bool defaultPipeline)
    {
        using var test = new Case("InvokeWithRuntimeArguments");
        test.Optimize(defaultPipeline: defaultPipeline);
        Assert.DoesNotContain(test.Method.ILBody!.Instructions, instruction => IsMemberDiscovery(instruction.Operand));
        var run = test.Emit().CreateDelegate<Func<object?[], object?>>();
        Assert.Equal(42, run([42]));
        Assert.Equal(0, run([null]));
        Assert.Throws<TargetParameterCountException>(() => run([]));
        Assert.Throws<ArgumentException>(() => run(["invalid"]));
    }

    [Fact]
    public void MemberConstructionPreservesGetTypeReceiverValidation()
    {
        using var test = new Case("MetadataThroughNullableReceiver");
        test.Optimize();
        Assert.DoesNotContain(test.Method.ILBody!.Instructions, instruction => IsMemberDiscovery(instruction.Operand));
        var run = test.Emit();
        Assert.IsType<NullReferenceException>(Assert.Throws<TargetInvocationException>(() => run.Invoke(null, [null])).InnerException);
        var targetType = run.GetParameters()[0].ParameterType;
        Assert.Same(targetType.GetField("Value"), run.Invoke(null, [Activator.CreateInstance(targetType)]));
    }

    private static bool IsMemberDiscovery(object? operand)
        => operand is MethodDesc method &&
            (method.DeclaringType.IsCorelibType(typeof(Type)) && method.Name is "GetMethod" or "GetField" or "GetProperty" or "GetConstructor" ||
             method.DeclaringType.IsCorelibType(typeof(MethodInfo)) && method.Name == "MakeGenericMethod" ||
             method.DeclaringType.IsCorelibType(typeof(PropertyInfo)) && method.Name is "get_GetMethod" or "get_SetMethod" or "GetGetMethod" or "GetSetMethod");

    private static void AssertMetadata(MemberInfo[] expected, MemberInfo[] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; i++) {
            var member = actual[i];
            Assert.Equal(expected[i].Name, member.Name);
            Assert.Equal(expected[i].MemberType, member.MemberType);
            Assert.Equal(expected[i].DeclaringType!.ToString(), member.DeclaringType!.ToString());
            Assert.Equal(expected[i].ReflectedType!.ToString(), member.ReflectedType!.ToString());
            Assert.Equal(expected[i].ToString(), member.ToString());

            var canonical = member.ReflectedType.GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.FlattenHierarchy)
                .Single(candidate => candidate.Module == member.Module && candidate.MetadataToken == member.MetadataToken);
            if (member is MethodInfo { IsConstructedGenericMethod: true } constructed) {
                canonical = ((MethodInfo)canonical).MakeGenericMethod(constructed.GetGenericArguments());
            }
            Assert.Same(canonical, member);
        }
    }

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

    [Theory]
    [InlineData("ParameterModifier", true)]
    [InlineData("ReturnModifier", true)]
    [InlineData("NestedPointer", true)]
    [InlineData("Array", true)]
    [InlineData("UnmanagedConventions", true)]
    [InlineData("ManagedAndUnmanaged", false)]
    [InlineData("DistinctParameters", false)]
    [InlineData("DistinctResults", false)]
    public void SignatureLookupsUseRuntimeTypeIdentity(string scenario, bool ambiguous)
    {
        foreach (bool defaultPipeline in new[] { false, true }) {
            using var resolver = new ModuleResolver();
            resolver.AddTrustedSearchPaths();
            var modifier = new TypeModifier(resolver.Import(typeof(System.Runtime.InteropServices.InAttribute)), true);
            var byref = PrimType.Int32.CreateByref();
            TypeDesc first = new FuncPtrType(new MethodSig(PrimType.Void, [byref], false));
            TypeDesc second = new FuncPtrType(new MethodSig(PrimType.Void, [new TypeSig(byref, [modifier])], false));
            switch (scenario) {
                case "ReturnModifier":
                    first = new FuncPtrType(new MethodSig(byref, [], false));
                    second = new FuncPtrType(new MethodSig(new TypeSig(byref, [modifier]), [], false));
                    break;
                case "NestedPointer":
                    first = new FuncPtrType(new MethodSig(PrimType.Void, [first], false));
                    second = new FuncPtrType(new MethodSig(PrimType.Void, [second], false));
                    break;
                case "Array":
                    first = first.CreateArray();
                    second = second.CreateArray();
                    break;
                case "UnmanagedConventions":
                    first = new FuncPtrType(new MethodSig(PrimType.Void, [byref], new SignatureHeader(SignatureKind.Method, SignatureCallingConvention.CDecl, 0)));
                    second = new FuncPtrType(new MethodSig(PrimType.Void, [byref], new SignatureHeader(SignatureKind.Method, SignatureCallingConvention.StdCall, 0)));
                    break;
                case "ManagedAndUnmanaged":
                    second = new FuncPtrType(new MethodSig(PrimType.Void, [byref], new SignatureHeader(SignatureKind.Method, SignatureCallingConvention.Unmanaged, 0)));
                    break;
                case "DistinctParameters":
                    second = new FuncPtrType(new MethodSig(PrimType.Void, [PrimType.Int64.CreateByref()], false));
                    break;
                case "DistinctResults":
                    second = new FuncPtrType(new MethodSig(PrimType.Int32, [byref], false));
                    break;
            }

            var methodInfo = resolver.Import(typeof(MethodInfo));
            var body = Utils.CreateTestMethod(resolver, methodInfo);
            var owner = body.Definition.DeclaringType;
            foreach (var parameter in new[] { first, second }) {
                var overload = owner.CreateMethod("Select", PrimType.Void, [new ParamDef(parameter, "value")], MethodAttributes.Public | MethodAttributes.Static);
                overload.Body = new Cilica.IR.MethodBody(overload);
                new IRBuilder(overload.Body.CreateBlock()).CreateReturn();
            }
            var builder = new IRBuilder(body.CreateBlock());
            var typeType = resolver.Import(typeof(Type));
            var parameters = builder.CreateNewArray(typeType, ConstInt.CreateI(1));
            builder.CreateArrayStore(parameters, ConstInt.CreateI(0), ReflectType(builder, second));
            var lookup = typeType.FindMethod("GetMethod", new MethodSig(methodInfo, [PrimType.String, typeType.CreateArray()], true));
            builder.CreateReturn(builder.CreateCallVirt(lookup, ReflectType(builder, owner), ConstString.Create("Select"), parameters));

            var original = Utils.Compile<Func<MethodInfo?>>(body);
            int? expectedToken = null;
            if (ambiguous) Assert.Throws<AmbiguousMatchException>(() => original());
            else expectedToken = original()!.MetadataToken;

            var compilation = new Compilation(owner.Module, new VoidLogger(), new() { VerifyEachPass = true });
            if (defaultPipeline) {
                Assert.True(CompilationPipeline.CreateDefault(compilation).Run([body.Definition]).Succeeded);
            } else {
                PassRunner.Run(new FoldReflection(), new MethodTransformContext(compilation, body));
            }
            var optimized = Utils.Compile<Func<MethodInfo?>>(body.Definition.Body!);
            if (ambiguous) Assert.Throws<AmbiguousMatchException>(() => optimized());
            else Assert.Equal(expectedToken, optimized()!.MetadataToken);
        }
    }
}
