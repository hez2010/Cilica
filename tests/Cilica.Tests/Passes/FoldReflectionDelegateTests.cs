namespace Cilica.Tests.Passes;

using System.Reflection;
using Cilica.AsmIO;
using Cilica.IR;
using Cilica.IR.Utils;
using Cilica.Passes;

partial class FoldReflectionTests
{
    [Theory]
    [InlineData("ConstructorDelegate", false)]
    [InlineData("ConstructorDelegate", true)]
    [InlineData("ConstructedReceiverDelegate", false)]
    [InlineData("ConstructedReceiverDelegate", true)]
    public void ReflectedConstructorsComposeWithDelegateFolding(string name, bool nativeAotCompatibility)
    {
        using var test = new Case(name, "ReflectionDelegateCases");
        Assert.Equal(42, test.Original.Invoke(null, null));
        test.Optimize(defaultPipeline: true, nativeAotCompatibility: nativeAotCompatibility);
        Assert.DoesNotContain(test.Method.ILBody!.Instructions, instruction => IsMemberDiscovery(instruction.Operand));
        bool retainsInvocation = nativeAotCompatibility && name == "ConstructorDelegate";
        Assert.Equal(retainsInvocation, test.Method.ILBody.Instructions.Any(instruction =>
            IsReflectionAccess(instruction.Operand) || IsReflectionFactory(instruction.Operand) || IsDelegateInvoke(instruction.Operand)));
        Assert.Equal(42, test.Emit().Invoke(null, null));
    }

    [Theory]
    [InlineData("OpenInstance", 42)]
    [InlineData("OpenVirtual", 42)]
    [InlineData("OpenUntyped", 42)]
    [InlineData("OpenStaticFactory", 42)]
    [InlineData("OpenStaticFactoryWithFlagEffects", 142)]
    [InlineData("OpenWithNullTarget", 42)]
    [InlineData("OpenNullNonvirtual", 42)]
    [InlineData("OpenMultipleCalls", 85)]
    [InlineData("OpenReceiverEvaluation", 1242)]
    [InlineData("OpenVirtualNullKeepsArgumentEffects", "2:NullReferenceException")]
    [InlineData("OpenStruct", 42)]
    [InlineData("OpenVirtualStruct", "42")]
    [InlineData("OpenRefStructDelegate", 42)]
    [InlineData("OpenNullableDelegate", true)]
    public void OpenInstanceDelegateInvocationsUseTheirSuppliedReceiver(string name, object expected)
    {
        foreach (bool nativeAot in new[] { false, true }) foreach (bool defaultPipeline in new[] { false, true }) {
            using var test = new Case(name, "ReflectionDelegateCases");
            Assert.Equal(expected, test.Original.Invoke(null, null));
            test.Optimize(defaultPipeline: defaultPipeline, nativeAotCompatibility: nativeAot, simplifyDelegates: true);
            Assert.DoesNotContain(test.Method.ILBody!.Instructions, instruction => IsDelegateInvoke(instruction.Operand));
            Assert.DoesNotContain(test.Method.ILBody.Instructions, instruction => IsReflectionFactory(instruction.Operand));
            if (nativeAot) {
                Assert.DoesNotContain(test.Method.ILBody.Instructions, instruction => instruction.OpCode is ILCode.Ldftn or ILCode.Ldvirtftn);
            }
            Assert.Equal(expected, test.Emit().Invoke(null, null));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AbstractPropertyDelegateFactoriesFoldToTheOverrideResult(bool nativeAot)
    {
        using var test = new Case("OpenAbstractProperty", "ReflectionAccessorCases");
        Assert.Equal(2, test.Original.Invoke(null, null));
        test.Optimize(defaultPipeline: true, nativeAotCompatibility: nativeAot);
        Assert.DoesNotContain(test.Method.ILBody!.Instructions, instruction => instruction.Operand is MethodDesc);
        Assert.Equal(2, test.Emit().Invoke(null, null));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OpenDelegateBindingKeepsShuffleLimitFailures(bool nativeAot)
    {
        using var resolver = new ModuleResolver();
        resolver.AddTrustedSearchPaths();
        var body = Utils.CreateTestMethod(resolver, PrimType.Int32);
        var module = body.Definition.Module;
        var owner = module.CreateType(null, "Receiver");
        var parameters = Enumerable.Range(0, 33000).Select(i => new ParamDef(PrimType.Int32, "arg" + i)).ToArray();
        var read = owner.CreateMethod("Read", PrimType.Int32, [new ParamDef(owner, "this"), .. parameters], MethodAttributes.Public);
        read.Body = new Cilica.IR.MethodBody(read);
        read.Body.CreateBlock().SetBranch(new ReturnInst(ConstInt.CreateI(0)));

        var delegateType = module.CreateType(null, "LargeDelegate", TypeAttributes.Public | TypeAttributes.Sealed,
            baseType: resolver.Import(typeof(MulticastDelegate)));
        var constructor = delegateType.CreateMethod(".ctor", PrimType.Void,
            [new ParamDef(delegateType, "this"), new ParamDef(PrimType.Object, "target"), new ParamDef(PrimType.IntPtr, "method")],
            MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName);
        constructor.ImplAttribs = MethodImplAttributes.Runtime;
        var invoke = delegateType.CreateMethod("Invoke", PrimType.Int32,
            [new ParamDef(delegateType, "this"), new ParamDef(owner, "receiver"), .. parameters],
            MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.NewSlot);
        invoke.ImplAttribs = MethodImplAttributes.Runtime;

        var typeType = resolver.Import(typeof(Type));
        var methodInfo = resolver.Import(typeof(MethodInfo));
        var getType = typeType.FindMethod("GetTypeFromHandle", new MethodSig(typeType, [resolver.Import(typeof(RuntimeTypeHandle))], false));
        var getMethod = typeType.FindMethod("GetMethod", new MethodSig(methodInfo, [PrimType.String], true));
        var createDelegate = methodInfo.FindMethod("CreateDelegate", new MethodSig(resolver.Import(typeof(Delegate)), [typeType], true));
        var entry = body.CreateBlock();
        var guarded = body.CreateBlock();
        var handler = body.CreateBlock();
        var continuation = body.CreateBlock();
        var result = body.CreateVar(PrimType.Int32);
        entry.SetBranch(guarded);
        var builder = new IRBuilder(guarded);
        builder.Emit(new GuardInst(GuardKind.Catch, handler, resolver.Import(typeof(NotSupportedException))));
        var reflectedOwner = builder.CreateCall(getType, builder.Emit(new CilIntrinsic.LoadHandle(resolver, owner)));
        var reflectedDelegate = builder.CreateCall(getType, builder.Emit(new CilIntrinsic.LoadHandle(resolver, delegateType)));
        var target = builder.CreateCallVirt(getMethod, reflectedOwner, ConstString.Create("Read"));
        builder.CreateCallVirt(createDelegate, target, reflectedDelegate);
        builder.CreateStore(result, ConstInt.CreateI(0));
        builder.Emit(new LeaveInst(continuation));
        var caught = new IRBuilder(handler);
        caught.CreateStore(result, ConstInt.CreateI(1));
        caught.Emit(new LeaveInst(continuation));
        var finish = new IRBuilder(continuation);
        finish.CreateReturn(finish.CreateLoad(result));
        Assert.Equal(1, Utils.Compile<Func<int>>(body)());

        var compilation = new Compilation(module, new VoidLogger(), new() {
            VerifyEachPass = true, NativeAotCompatibility = nativeAot
        });
        Assert.True(CompilationPipeline.CreateDefault(compilation).Run([body.Definition]).Succeeded);
        Assert.Equal(1, Utils.Compile<Func<int>>(body.Definition.Body!)());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OpenDelegateAggregateBindingsKeepRuntimeAbiValidation(bool nativeAot)
    {
        using var test = new Case("LargeValueOpenBinding", "ReflectionDelegateCases");
        object? expected = test.Original.Invoke(null, null);
        test.Optimize(defaultPipeline: true, nativeAotCompatibility: nativeAot);
        Assert.Contains(test.Method.ILBody!.Instructions, instruction => IsReflectionFactory(instruction.Operand));
        Assert.Equal(expected, test.Emit().Invoke(null, null));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void UnusedOpenDelegatesPreserveFinalization(bool nativeAot, bool explicitImplementation)
    {
        using var test = new Case("UnusedOpenAbstractDelegate", "ReflectionDelegateCases");
        test.AddDelegateFinalizer(explicitImplementation);
        CheckFinalization(test.Emit());
        test.Optimize(defaultPipeline: true, nativeAotCompatibility: nativeAot);
        CheckFinalization(test.Emit());

        static void CheckFinalization(MethodInfo method)
        {
            method.Invoke(null, null);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            Assert.Equal(1, method.DeclaringType!.GetField("Result")!.GetValue(null));
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void OpenDelegateInvocationPreservesLifetimeDuringArgumentEvaluation(bool nativeAot, bool explicitImplementation)
    {
        using var test = new Case("UsedFinalizableOpenDelegate", "ReflectionDelegateCases");
        test.AddDelegateFinalizer(explicitImplementation);
        Assert.Equal(0, test.Emit().Invoke(null, null));
        test.Optimize(defaultPipeline: true, nativeAotCompatibility: nativeAot);
        Assert.Equal(0, test.Emit().Invoke(null, null));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EliminatedOpenFactoriesKeepRequestedTrimmingDependencies(bool nativeAot)
    {
        using var test = new Case("OpenAbstractProperty", "ReflectionAccessorCases");
        test.Optimize(defaultPipeline: true, nativeAotCompatibility: nativeAot, preserveTrimmingDependencies: true);
        Assert.DoesNotContain(test.Method.ILBody!.Instructions, instruction => IsReflectionFactory(instruction.Operand) ||
            IsDelegateInvoke(instruction.Operand));
        Assert.Contains(test.Method.ILBody.Instructions, instruction => instruction.Operand is MethodDesc { Name: "GetProperty" });
        Assert.Equal(2, test.Emit().Invoke(null, null));
    }

    [Theory]
    [InlineData("OpenObserved", "ReflectionDelegateCases")]
    [InlineData("OpenAbstractObserved", "ReflectionGenericCases")]
    public void ObservedOpenDelegateBindingsPreserveIdentity(string name, string owner)
    {
        foreach (bool nativeAot in new[] { false, true }) {
            using var test = new Case(name, owner);
            Assert.Equal(true, test.Original.Invoke(null, null));
            test.Optimize(defaultPipeline: true, nativeAotCompatibility: nativeAot);
            Assert.DoesNotContain(test.Method.ILBody!.Instructions, instruction => IsDelegateInvoke(instruction.Operand));
            Assert.Equal(true, test.Emit().Invoke(null, null));
        }
    }

    [Theory]
    [InlineData("OpenMulticast", 4)]
    [InlineData("OpenMutableCache", 42)]
    public void OpenDelegateCallsKeepUnknownAndMulticastTargets(string name, object expected)
    {
        foreach (bool nativeAot in new[] { false, true }) {
            using var test = new Case(name, "ReflectionDelegateCases");
            test.Optimize(defaultPipeline: true, nativeAotCompatibility: nativeAot);
            Assert.Contains(test.Method.ILBody!.Instructions, instruction => IsDelegateInvoke(instruction.Operand));
            Assert.Equal(expected, test.Emit().Invoke(null, null));
        }
    }

    [Theory]
    [InlineData("OpenInterfaceDelegate", 42)]
    [InlineData("OpenAbstractDelegate", 42)]
    [InlineData("OpenGenericStructDelegate", "String:Object:hello:41")]
    public void OpenGenericAndAbstractDelegateCallsPreserveDispatch(string name, object expected)
    {
        foreach (bool nativeAot in new[] { false, true }) {
            using var test = new Case(name, "ReflectionGenericCases");
            test.Optimize(nativeAotCompatibility: nativeAot, simplifyDelegates: true);
            Assert.DoesNotContain(test.Method.ILBody!.Instructions, instruction => IsDelegateInvoke(instruction.Operand));
            Assert.DoesNotContain(test.Method.ILBody.Instructions, instruction => IsReflectionFactory(instruction.Operand));
            Assert.Equal(expected, test.Emit().Invoke(null, null));
        }
    }

    private static bool IsDelegateInvoke(object? operand)
        => operand is MethodDesc { Name: "Invoke" } method && method.DeclaringType.BaseType?.IsCorelibType(typeof(MulticastDelegate)) == true;

    [Theory]
    [InlineData("GenericStatic", 42)]
    [InlineData("UntypedStatic", 42)]
    [InlineData("GenericClosedInstance", 42)]
    [InlineData("UntypedClosedInstance", 42)]
    [InlineData("StaticFactoryOpen", 42)]
    [InlineData("StaticFactoryOpenWithFlag", 42)]
    [InlineData("StaticFactoryClosed", 42)]
    [InlineData("StaticFactoryClosedWithFlag", 42)]
    [InlineData("OpenInstance", 42)]
    [InlineData("OpenVirtual", 42)]
    [InlineData("ClosedVirtual", 42)]
    [InlineData("DeclaredVirtualIdentity", true)]
    [InlineData("BoxedVirtual", "42")]
    [InlineData("ClosedStatic", 42)]
    [InlineData("ClosedStaticNull", 42)]
    [InlineData("ClosedStaticFactory", 42)]
    [InlineData("BoundMissingArgument", true)]
    [InlineData("OpenStaticExplicitNull", 42)]
    [InlineData("BoxedStruct", 42)]
    [InlineData("OpenStruct", 42)]
    [InlineData("RefParameter", 42)]
    [InlineData("RefReturn", 42)]
    [InlineData("LargeStructReturn", 42L)]
    [InlineData("DelegateIdentity", true)]
    [InlineData("ClosedGenericType", 42)]
    [InlineData("DelegateInitialization", 42)]
    [InlineData("DelegateException", "failure")]
    [InlineData("DelegateInsideFilter", 42)]
    [InlineData("StaticFunctionPointer", 42)]
    [InlineData("CopiedFunctionPointerHandle", 42)]
    [InlineData("FunctionPointerIdentity", true)]
    [InlineData("GenericTypeFunctionPointer", "hello")]
    [InlineData("UnmanagedFunctionPointer", 42)]
    [InlineData("FunctionPointerInitialization", 42)]
    [InlineData("FunctionPointerMetadataObserved", "Add:42")]
    [InlineData("NativeImportDelegateCreation", true)]
    [InlineData("NativeImportPointerCreation", true)]
    [InlineData("CovariantSignature", "hello")]
    [InlineData("ContravariantSignature", 5)]
    [InlineData("UntypedClosedVirtual", 42)]
    public void KnownDelegateAndPointerFactoriesPreserveBehavior(string name, object expected)
    {
        foreach (bool defaultPipeline in new[] { false, true }) {
            using var test = new Case(name, "ReflectionDelegateCases");
            Assert.Equal(expected, test.Original.Invoke(null, null));
            test.Optimize(defaultPipeline: defaultPipeline);
            Assert.DoesNotContain(test.Method.ILBody!.Instructions, instruction => IsReflectionFactory(instruction.Operand));
            Assert.Equal(expected, test.Emit().Invoke(null, null));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClosedVirtualDelegatesPreserveMethodIdentity(bool defaultPipeline)
    {
        using var test = new Case("ClosedVirtualIdentity", "ReflectionDelegateCases");
        var expected = test.Original.Invoke(null, null);
        test.Optimize(defaultPipeline: defaultPipeline);
        Assert.Contains(test.Method.ILBody!.Instructions, instruction => IsReflectionFactory(instruction.Operand));
        Assert.Equal(expected, test.Emit().Invoke(null, null));
    }

    [Theory]
    [InlineData("ClosedNullInstance")]
    [InlineData("InvalidDelegateType")]
    [InlineData("SignatureMismatch")]
    [InlineData("FailureReturnsNull")]
    [InlineData("OpenWithNonNullTarget")]
    [InlineData("BoundValueArgument")]
    [InlineData("OpenDelegateType")]
    [InlineData("GenericMethodDefinition")]
    [InlineData("NameBasedFactory")]
    [InlineData("DefaultFunctionPointerHandle")]
    [InlineData("MutatedFunctionPointerHandle")]
    [InlineData("OverwrittenFunctionPointerHandle")]
    [InlineData("InstanceFunctionPointer")]
    public void UnsupportedDelegateAndPointerFactoriesRemainReflective(string name)
    {
        using var test = new Case(name, "ReflectionDelegateCases");
        object? expected = test.Original.Invoke(null, null);
        test.Optimize();
        Assert.Contains(test.Method.ILBody!.Instructions, instruction => IsReflectionFactory(instruction.Operand));
        Assert.Equal(expected, test.Emit().Invoke(null, null));
    }

    [Fact]
    public void InliningExposesDelegateFactories()
    {
        using var test = new Case("ThroughHelper", "ReflectionDelegateCases");
        test.Optimize(defaultPipeline: true);
        Assert.DoesNotContain(test.Method.ILBody!.Instructions, instruction => IsReflectionFactory(instruction.Operand));
        Assert.Equal(42, test.Emit().Invoke(null, null));
    }

    [Fact]
    public void RuntimeMethodNamesKeepDelegateLookup()
    {
        using var test = new Case("RuntimeNamedDelegate", "ReflectionDelegateCases");
        test.Optimize(defaultPipeline: true);
        Assert.Contains(test.Method.ILBody!.Instructions, instruction => IsReflectionFactory(instruction.Operand));
        Assert.Equal(42, test.Emit().Invoke(null, ["Add"]));
    }

    [Theory]
    [InlineData("GenericStatic")]
    [InlineData("StaticFunctionPointer")]
    public void FactoryFoldingPreservesRequestedTrimmingDependencies(string name)
    {
        using var test = new Case(name, "ReflectionDelegateCases");
        test.Optimize(preserveTrimmingDependencies: true);
        Assert.DoesNotContain(test.Method.ILBody!.Instructions, instruction => IsReflectionFactory(instruction.Operand));
        Assert.Contains(test.Method.ILBody.Instructions, instruction => instruction.Operand is MethodDesc { Name: "GetMethod" });
        Assert.Equal(42, test.Emit().Invoke(null, null));
    }

    [Theory]
    [InlineData("DelegateTypeInitialization", false, false)]
    [InlineData("DelegateTypeInitialization", false, true)]
    [InlineData("DelegateTypeInitialization", true, false)]
    [InlineData("DelegateTypeInitialization", true, true)]
    [InlineData("ConstructorDelegateTypeInitialization", false, false)]
    [InlineData("ConstructorDelegateTypeInitialization", false, true)]
    [InlineData("ConstructorDelegateTypeInitialization", true, false)]
    [InlineData("ConstructorDelegateTypeInitialization", true, true)]
    public void DelegateTypeInitializersRetainTheirActivationTiming(string name, bool defaultPipeline, bool beforeFieldInit)
    {
        using var test = new Case(name, "ReflectionDelegateCases");
        test.AddDelegateTypeInitializer(beforeFieldInit);
        object? expected = test.Emit().Invoke(null, null);
        test.Optimize(defaultPipeline: defaultPipeline);
        Assert.DoesNotContain(test.Method.ILBody!.Instructions, instruction => IsReflectionFactory(instruction.Operand) || IsReflectionAccess(instruction.Operand));
        Assert.Equal(expected, test.Emit().Invoke(null, null));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void OpenDelegateTypeInitializersRemainAtTheFactory(bool nativeAot, bool beforeFieldInit)
    {
        using var test = new Case("OpenDelegateTypeInitialization", "ReflectionDelegateCases");
        test.AddDelegateTypeInitializer(beforeFieldInit, "OpenAbstractRead");
        object? expected = test.Emit().Invoke(null, null);
        test.Optimize(defaultPipeline: true, nativeAotCompatibility: nativeAot);
        Assert.Contains(test.Method.ILBody!.Instructions, instruction => IsReflectionFactory(instruction.Operand));
        Assert.DoesNotContain(test.Method.ILBody.Instructions, instruction => IsDelegateInvoke(instruction.Operand));
        Assert.Equal(expected, test.Emit().Invoke(null, null));
    }

    private static bool IsReflectionFactory(object? operand)
        => operand is MethodDesc method &&
            (method.Name is "CreateDelegate" or "get_MethodHandle" && method.DeclaringType.Namespace is "System" or "System.Reflection" ||
                method.Name == "GetFunctionPointer" && method.DeclaringType.IsCorelibType(typeof(RuntimeMethodHandle)));
}
