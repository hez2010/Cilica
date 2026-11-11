namespace Cilica.Tests.Passes;

using System.Reflection;
using System.Runtime.Loader;
using System.Runtime.CompilerServices;
using Cilica.AsmIO;
using Cilica.CodeGen.Cil;
using Cilica.IR;
using Cilica.IR.Utils;
using Cilica.Passes;
using MethodBody = Cilica.IR.MethodBody;

partial class FoldReflectionTests
{
    [Theory]
    [InlineData("Field")]
    [InlineData("Property")]
    [InlineData("Invoke")]
    [InlineData("StaticDelegate")]
    [InlineData("InstanceDelegate")]
    [InlineData("OpenInstanceDelegate")]
    [InlineData("StaticFactory")]
    [InlineData("FunctionPointer")]
    [InlineData("PrivateDelegateType")]
    [InlineData("GenericType")]
    public void NonPublicMembersFoldAndExecute(string name)
    {
        foreach (bool defaultPipeline in new[] { false, true }) {
            using var test = new Case(name, "ReflectionNonPublicCases");
            Assert.Equal(42, test.Original.Invoke(null, null));
            test.Optimize(defaultPipeline: defaultPipeline);
            Assert.DoesNotContain(test.Method.ILBody!.Instructions,
                instruction => IsReflectionAccess(instruction.Operand) || IsReflectionFactory(instruction.Operand));
            Assert.Equal(42, test.Emit().Invoke(null, null));
        }
    }

    [Fact]
    public void InlinedLookupsCanExposePrivateDeclaringTypes()
    {
        using var test = new Case("HiddenType", "ReflectionNonPublicCases");
        test.Optimize(defaultPipeline: true);
        Assert.DoesNotContain(test.Method.ILBody!.Instructions, instruction => IsReflectionFactory(instruction.Operand));
        Assert.Equal(42, test.Emit().Invoke(null, null));
    }

    public static class ExternalPrivateTarget
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int AddOne(int value) => value + 1;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PrivateDelegatesAcrossAssembliesRemainCallable(bool defaultPipeline)
    {
        using var resolver = new ModuleResolver();
        resolver.AddTrustedSearchPaths();
        var body = CreatePrivateDelegateCaller(resolver.Create("ReflectionAccess_" + Guid.NewGuid().ToString("N")),
            resolver.Import(typeof(ExternalPrivateTarget)));
        Assert.Equal(42, Utils.Compile<Func<int, int>>(body)(41));
        var compilation = new Compilation(body.Definition.Module, new VoidLogger(), new() { VerifyEachPass = true });
        var manager = defaultPipeline ? CompilationPipeline.CreateDefault(compilation) : new PassManager { Compilation = compilation };
        if (!defaultPipeline) manager.AddPasses().Apply<FoldReflection>();
        Assert.True(manager.Run([body.Definition]).Succeeded);
        Assert.DoesNotContain(body.Definition.ILBody!.Instructions, instruction => IsReflectionFactory(instruction.Operand));
        Assert.Equal(42, Utils.Compile<Func<int, int>>(body.Definition.Body!)(41));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CoreLibKeepsReferencesThatWouldRequireAccessSuppression(bool corelib)
    {
        using var resolver = new ModuleResolver();
        resolver.AddTrustedSearchPaths();
        var module = corelib ? resolver.CoreLib : resolver.Create("ReflectionAccess_" + Guid.NewGuid().ToString("N"));
        var target = module.CreateType(null, "PrivateReflectionTarget", TypeAttributes.Public);
        var add = target.CreateMethod("AddOne", PrimType.Int32, [new ParamDef(PrimType.Int32, "value")],
            MethodAttributes.Private | MethodAttributes.Static);
        add.Body = new MethodBody(add);
        var builder = new IRBuilder(add.Body.CreateBlock());
        builder.CreateReturn(builder.CreateAdd(add.Body.Args[0], ConstInt.CreateI(1)));
        var body = CreatePrivateDelegateCaller(module, target);
        var manager = new PassManager { Compilation = new Compilation(module, new VoidLogger(), new() { VerifyEachPass = true }) };
        manager.AddPasses().Apply<FoldReflection>();
        Assert.True(manager.ApplyPasses([body.Definition]).Succeeded);
        Assert.Equal(corelib, body.Definition.Body!.Instructions().OfType<CallInst>().Any(call => IsReflectionFactory(call.Method)));
        Assert.DoesNotContain(module.GetCustomAttribs(forAssembly: true), attribute => attribute.Type.Name == "IgnoresAccessChecksToAttribute");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DisabledCrossAssemblyOptimizationKeepsDependenciesReplaceable(bool changeArrayElementBase)
    {
        using var resolver = new ModuleResolver();
        resolver.AddTrustedSearchPaths();
        var module = resolver.Create("Caller_" + Guid.NewGuid().ToString("N"));
        var library = resolver.Create("Library_" + Guid.NewGuid().ToString("N"));
        var target = library.CreateType(null, "Target");
        var baseType = module.CreateType(null, "ElementBase");
        if (changeArrayElementBase) target.SetBaseType(baseType);
        var method = module.CreateType(null, "Caller").CreateMethod("Run", PrimType.Object,
            changeArrayElementBase ? [new ParamDef(target.CreateArray(), "value")] : [], MethodAttributes.Public | MethodAttributes.Static);
        var original = method.Body = new MethodBody(method);
        var builder = new IRBuilder(original.CreateBlock());
        var typeType = resolver.Import(typeof(Type));
        var fieldInfo = resolver.Import(typeof(FieldInfo));
        var getField = typeType.FindMethod("GetField", new MethodSig(fieldInfo, [PrimType.String], true));
        if (changeArrayElementBase) {
            var holder = module.CreateType(null, "Holder");
            holder.CreateField("Value", baseType.CreateArray());
            var constructor = holder.CreateMethod(".ctor", PrimType.Void, [new ParamDef(holder, "this")], MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName);
            constructor.Body = new MethodBody(constructor);
            var initialize = new IRBuilder(constructor.Body.CreateBlock());
            initialize.CreateCall(resolver.Import(typeof(object)).FindMethod(".ctor"), constructor.Body.Args[0]);
            initialize.CreateReturn();
            constructor.ILBody = ILGenerator.GenerateCode(constructor.Body);
            var receiver = builder.CreateNewObj(constructor);
            var reflected = builder.CreateCall(typeType.FindMethod("GetTypeFromHandle"), builder.Emit(new CilIntrinsic.LoadHandle(resolver, holder)));
            var field = builder.CreateCallVirt(getField, reflected, ConstString.Create("Value"));
            builder.CreateCallVirt(fieldInfo.FindMethod("SetValue", new MethodSig(PrimType.Void, [PrimType.Object, PrimType.Object], true)), field, receiver, original.Args[0]);
            builder.CreateReturn(ConstString.Create("stored"));
        } else {
            var reflected = builder.CreateCall(typeType.FindMethod("GetTypeFromHandle"), builder.Emit(new CilIntrinsic.LoadHandle(resolver, target)));
            builder.CreateReturn(builder.CreateCallVirt(getField, reflected, ConstString.Create("Added")));
        }
        var manager = new PassManager { Compilation = new Compilation(module, new VoidLogger(), new() { AllowCrossAssemblyIPO = false, VerifyEachPass = true }) };
        manager.AddPasses().Apply<FoldReflection>();
        Assert.True(manager.Run([method]).Succeeded);
        var optimized = method.Body!;
        if (changeArrayElementBase) target.SetBaseType(resolver.Import(typeof(object)));
        else target.CreateField("Added", PrimType.Int32, FieldAttributes.Static | FieldAttributes.Public);
        foreach (var body in new[] { original, optimized }) {
            method.ILBody = ILGenerator.GenerateCode(body);
            var images = new[] { module, library }.ToDictionary(item => item.AsmName.Name!, item => {
                using var stream = new MemoryStream();
                item.Save(stream, null, item.ModName);
                return stream.ToArray();
            });
            var context = new AssemblyLoadContext(null, isCollectible: true);
            context.Resolving += (_, name) => images.TryGetValue(name.Name!, out var image) ? context.LoadFromStream(new MemoryStream(image)) : null;
            try {
                var assembly = context.LoadFromStream(new MemoryStream(images[module.AsmName.Name!]));
                var run = assembly.GetType("Caller")!.GetMethod("Run")!;
                if (changeArrayElementBase) {
                    var element = context.LoadFromAssemblyName(new AssemblyName(library.AsmName.Name!)).GetType("Target")!;
                    var error = Assert.Throws<TargetInvocationException>(() => run.Invoke(null, [Array.CreateInstance(element, 0)]));
                    Assert.IsType<ArgumentException>(error.InnerException);
                } else {
                    var field = (FieldInfo)run.Invoke(null, null)!;
                    Assert.Equal("Added", field.Name);
                }
            } finally { context.Unload(); }
        }
    }

    private static MethodBody CreatePrivateDelegateCaller(ModuleDef module, TypeDefOrSpec target)
    {
        var resolver = module.Resolver;
        var caller = module.CreateType(null, "ReflectionCaller", TypeAttributes.Public)
            .CreateMethod("Run", PrimType.Int32, [new ParamDef(PrimType.Int32, "value")], MethodAttributes.Public | MethodAttributes.Static);
        var body = caller.Body = new MethodBody(caller);
        var builder = new IRBuilder(body.CreateBlock());
        var typeType = resolver.Import(typeof(Type));
        var methodInfo = resolver.Import(typeof(MethodInfo));
        var delegateType = resolver.Import(typeof(Func<int, int>));
        var flagsType = resolver.Import(typeof(BindingFlags));
        var type = builder.CreateCall(typeType.FindMethod("GetTypeFromHandle"), builder.Emit(new CilIntrinsic.LoadHandle(resolver, target)));
        var method = builder.CreateCallVirt(typeType.FindMethod("GetMethod", new MethodSig(methodInfo, [PrimType.String, flagsType], true)),
            type, ConstString.Create("AddOne"), ConstInt.Create(flagsType, (int)(BindingFlags.NonPublic | BindingFlags.Static)));
        var create = methodInfo.Methods.Single(method => method.Name == "CreateDelegate" && method.IsGeneric && method.ParamSig.Count == 1)
            .GetSpec([delegateType]);
        var callback = builder.CreateCallVirt(create, method);
        builder.CreateReturn(builder.CreateCallVirt(delegateType.FindMethod("Invoke"), callback, body.Args[0]));
        return body;
    }
}
