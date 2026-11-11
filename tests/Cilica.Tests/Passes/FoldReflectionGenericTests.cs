namespace Cilica.Tests.Passes;

using System.Reflection;
using Cilica.AsmIO;
using Cilica.IR;
using Cilica.IR.Utils;
using Cilica.Passes;

partial class FoldReflectionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EscapingMemberMetadataKeepsRuntimeTypeArguments(bool defaultPipeline)
    {
        using var test = new Case("GenericMetadata", "ReflectionGenericCases");
        test.Optimize(defaultPipeline: defaultPipeline);
        Assert.DoesNotContain(test.Method.ILBody!.Instructions, instruction => IsMemberDiscovery(instruction.Operand));
        var method = test.Emit();
        foreach (var type in new[] { typeof(int), typeof(string), typeof(int?) }) {
            AssertMetadata((MemberInfo[])test.Original.MakeGenericMethod(type).Invoke(null, null)!,
                (MemberInfo[])method.MakeGenericMethod(type).Invoke(null, null)!);
        }
    }

    [Theory]
    [InlineData("MethodDelegate", 42)]
    [InlineData("MethodInvoke", 42)]
    [InlineData("MethodPointer", "hello")]
    [InlineData("ConstructedType", 42)]
    [InlineData("ConstructedArrayDelegate", 42)]
    [InlineData("ConstructedPointerDelegate", 42)]
    [InlineData("ArrayConstraint", 42)]
    [InlineData("ConstructedDelegateType", 42)]
    [InlineData("MethodOnGenericOwner", "String:Object:hello")]
    [InlineData("ClosedStaticGenericReturnBuffer", "String:Object:hello:world")]
    [InlineData("OpenGenericStructDelegate", "String:Object:hello:41")]
    [InlineData("DerivedOverride", 42)]
    [InlineData("MixedGenericVirtualDelegates", "Int32:String:Int32:42|String:String:String:hello|Int32:Int32:Int32:7")]
    [InlineData("InterfaceInvoke", 42)]
    [InlineData("ClosedInterfaceDelegate", 42)]
    [InlineData("AbstractInvoke", 42)]
    [InlineData("ClosedAbstractDelegate", 42)]
    [InlineData("BoxedInterfaceInvoke", 42)]
    [InlineData("BoxedInterfaceDelegate", 42)]
    [InlineData("BoxedGenericVirtualInvoke", "String:Object:hello:41")]
    [InlineData("GenericVirtualDelegate", "String:Object:hello")]
    [InlineData("GenericVirtualInvoke", "String:Object:hello")]
    [InlineData("DefaultInterfaceInvoke", 42)]
    [InlineData("DefaultInterfaceDelegate", 42)]
    [InlineData("BoxedDefaultInterfaceIdentity", true)]
    [InlineData("StructConstraint", 42)]
    [InlineData("ReferenceConstraint", "hello")]
    [InlineData("ConstructorConstraint", 42)]
    [InlineData("InterfaceConstraint", 42)]
    [InlineData("GenericArityLookup", 42)]
    public void GenericAndVirtualReflectionPreservesBehavior(string name, object expected)
    {
        foreach (bool defaultPipeline in new[] { false, true }) {
            using var test = new Case(name, "ReflectionGenericCases");
            Assert.Equal(expected, test.Original.Invoke(null, null));
            test.Optimize(defaultPipeline: defaultPipeline);
            AssertNoGenericReflection(test.Method);
            Assert.Equal(expected, test.Emit().Invoke(null, null));
        }
    }

    [Theory]
    [InlineData("GenericBody")]
    [InlineData("GenericOwnerBody")]
    [InlineData("GenericConstructedTypeBody")]
    [InlineData("GenericField")]
    [InlineData("GenericPointer")]
    public void RuntimeTypeParametersKeepTheirGenericContext(string name)
    {
        foreach (bool defaultPipeline in new[] { false, true }) {
            using var test = new Case(name, "ReflectionGenericCases");
            test.Optimize(defaultPipeline: defaultPipeline);
            AssertNoGenericReflection(test.Method);
            var emitted = test.Emit();
            foreach (var (type, value) in new (Type, object?)[] { (typeof(int), 42), (typeof(string), "hello"),
                (typeof(object), new object()), (typeof((int, long)), (7, 12L)), (typeof(int?), null), (typeof(int?), 42) }) {
                var run = emitted.MakeGenericMethod(type);
                Assert.Equal(test.Original.MakeGenericMethod(type).Invoke(null, [value]), run.Invoke(null, [value]));
            }
        }
    }

    [Theory]
    [InlineData("GenericValueInvoke")]
    [InlineData("GenericConstrainedBody")]
    public void RuntimeStructConstraintsAllowGenericBoxing(string name)
    {
        using var test = new Case(name, "ReflectionGenericCases");
        test.Optimize(defaultPipeline: true);
        AssertNoGenericReflection(test.Method);
        var emitted = test.Emit();
        foreach (var (type, value) in new (Type, object)[] { (typeof(int), 42), (typeof(long), 42L), (typeof((int, long)), (7, 12L)) }) {
            Assert.Equal(value, emitted.MakeGenericMethod(type).Invoke(null, [value]));
        }
    }

    [Fact]
    public void BoxedGenericInterfaceReceiversKeepMutations()
    {
        using var test = new Case("GenericBoxedInterface", "ReflectionGenericCases");
        test.Optimize(defaultPipeline: true);
        AssertNoGenericReflection(test.Method);
        var method = test.Emit();
        var counter = method.DeclaringType!.GetNestedType("Counter")!;
        Assert.Equal(42, method.MakeGenericMethod(counter).Invoke(null, [Activator.CreateInstance(counter, [40])]));
    }

    [Theory]
    [InlineData("EscapedBoxedGenericVirtualDelegate")]
    [InlineData("EscapedBoxedGenericMethodDelegate")]
    public void EscapingDelegatesRetainUnboxingAndInstantiationContexts(string name)
    {
        foreach (bool defaultPipeline in new[] { false, true }) {
            using var test = new Case(name, "ReflectionGenericCases");
            var original = (Func<object, string>)test.Original.Invoke(null, null)!;
            test.Optimize(defaultPipeline: defaultPipeline);
            if (name == "EscapedBoxedGenericMethodDelegate") AssertNoGenericReflection(test.Method);
            var callback = (Func<object, string>)test.Emit().Invoke(null, null)!;
            Assert.Equal(original("hello"), callback("hello"));
            Assert.Equal(original("world"), callback("world"));
            Assert.NotNull(callback.Target);
            Assert.Equal(typeof(object), Assert.Single(callback.Method.GetGenericArguments()));
            Assert.Equal(original.Method.DeclaringType!.FullName, callback.Method.DeclaringType!.FullName);
            Assert.Equal(typeof(string), Assert.Single(callback.Target!.GetType().GetGenericArguments()));
        }
    }

    [Theory]
    [InlineData("GenericBoxedVirtualFactory")]
    [InlineData("GenericBoxedMethodFactory")]
    public void GenericEscapingFactoriesRetainBothRuntimeInstantiations(string name)
    {
        foreach (bool defaultPipeline in new[] { false, true }) {
            using var test = new Case(name, "ReflectionGenericCases");
            test.Optimize(defaultPipeline: defaultPipeline);
            if (name == "GenericBoxedMethodFactory") AssertNoGenericReflection(test.Method);
            var emitted = test.Emit();
            foreach (var (owner, argument, value) in new (Type, Type, object)[] {
                (typeof(string), typeof(object), "hello"), (typeof(object), typeof(string), "world"),
                (typeof(string), typeof((int, long)), (1, 2L)), (typeof(int), typeof(long), 42L)
            }) {
                var original = (Delegate)test.Original.MakeGenericMethod(owner, argument).Invoke(null, null)!;
                var callback = (Delegate)emitted.MakeGenericMethod(owner, argument).Invoke(null, null)!;
                Assert.Equal(original.DynamicInvoke(value), callback.DynamicInvoke(value));
                Assert.Equal(original.DynamicInvoke(value), callback.DynamicInvoke(value));
                Assert.Equal(argument, Assert.Single(callback.Method.GetGenericArguments()));
                Assert.Equal(original.Method.DeclaringType!.FullName, callback.Method.DeclaringType!.FullName);
                Assert.Equal(owner, Assert.Single(callback.Target!.GetType().GetGenericArguments()));
            }
        }
    }

    [Fact]
    public void GenericDefaultInterfaceBodiesSeeTheOriginalBox()
    {
        using var test = new Case("GenericDefaultInterfaceIdentity", "ReflectionGenericCases");
        test.Optimize(defaultPipeline: true);
        AssertNoGenericReflection(test.Method);
        var emitted = test.Emit();
        var valueType = emitted.DeclaringType!.GetNestedType("DefaultValue")!;
        Assert.Equal(true, emitted.MakeGenericMethod(valueType).Invoke(null, [Activator.CreateInstance(valueType)]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GenericDefaultInterfaceDelegatesPreserveMethodIdentity(bool defaultPipeline)
    {
        using var test = new Case("EscapedGenericDefaultInterface", "ReflectionGenericCases");
        var original = (Func<object, string>)test.Original.Invoke(null, null)!;
        test.Optimize(defaultPipeline: defaultPipeline);
        AssertNoGenericReflection(test.Method);
        var callback = (Func<object, string>)test.Emit().Invoke(null, null)!;
        Assert.Equal(original("hello"), callback("hello"));
        Assert.Equal(original.Method.DeclaringType!.FullName, callback.Method.DeclaringType!.FullName);
        Assert.Equal(original.Method.GetGenericArguments(), callback.Method.GetGenericArguments());
    }

    private delegate ref int RefGetter();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GenericBoxPayloadReferencesRemainValidAfterReturning(bool defaultPipeline)
    {
        using var test = new Case("EscapingBoxReference", "ReflectionGenericCases");
        test.Optimize(defaultPipeline: defaultPipeline);
        AssertNoGenericReflection(test.Method);
        var emitted = test.Emit();
        foreach (var type in new[] { typeof(string), typeof(int), typeof(long) }) {
            var get = emitted.MakeGenericMethod(type).CreateDelegate<RefGetter>();
            ref int value = ref get();
            Assert.Equal(40 + type.Name.Length, value);
            value = 101;
            GC.Collect();
            GC.WaitForPendingFinalizers();
            Assert.Equal(101, value);
        }
    }

    [Theory]
    [InlineData("InlinedGenericType")]
    [InlineData("InlinedClosedVirtual")]
    public void InliningExposesReflectionArgumentsAndTargets(string name)
    {
        using var test = new Case(name, "ReflectionGenericCases");
        test.Optimize(defaultPipeline: true);
        AssertNoGenericReflection(test.Method);
        Assert.Equal(42, test.Emit().Invoke(null, null));
    }

    [Theory]
    [InlineData("ConstructedTypeResult", false)]
    [InlineData("ConstructedTypeResult", true)]
    [InlineData("InlinedTypeResult", true)]
    [InlineData("ArrayTypeResult", false)]
    [InlineData("ArrayTypeResult", true)]
    [InlineData("RankOneArrayTypeResult", false)]
    [InlineData("MultidimensionalArrayTypeResult", true)]
    [InlineData("MaximumRankArrayTypeResult", false)]
    [InlineData("PointerTypeResult", false)]
    [InlineData("PointerTypeResult", true)]
    [InlineData("ByrefTypeResult", false)]
    [InlineData("ByrefTypeResult", true)]
    [InlineData("VoidPointerTypeResult", false)]
    [InlineData("VoidByrefTypeResult", false)]
    [InlineData("NestedTypeResult", false)]
    [InlineData("NestedTypeResult", true)]
    [InlineData("FunctionPointerArrayTypeResult", true)]
    public void TypeConstructionFoldsWhenTheTypeResultEscapes(string name, bool defaultPipeline)
    {
        using var test = new Case(name, "ReflectionGenericCases");
        var expected = (Type)test.Original.Invoke(null, null)!;
        test.Optimize(defaultPipeline: defaultPipeline);
        AssertNoGenericReflection(test.Method);
        var actual = (Type)test.Emit().Invoke(null, null)!;
        Assert.Equal(expected.ToString(), actual.ToString());
        Assert.Equal(expected.IsSZArray, actual.IsSZArray);
        if (expected.Assembly == typeof(object).Assembly) Assert.Same(expected, actual);
    }

    [Theory]
    [InlineData("ConstructedTypeResult")]
    [InlineData("ArrayTypeResult")]
    [InlineData("PointerTypeResult")]
    [InlineData("ByrefTypeResult")]
    [InlineData("NestedTypeResult")]
    public void TypeConstructionRetainsRequestedTrimmingDependencies(string name)
    {
        using var test = new Case(name, "ReflectionGenericCases");
        var expected = (Type)test.Original.Invoke(null, null)!;
        test.Optimize(preserveTrimmingDependencies: true);
        Assert.Contains(test.Method.ILBody!.Instructions, instruction => IsTypeConstruction(instruction.Operand));
        Assert.Equal(expected.ToString(), test.Emit().Invoke(null, null)!.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompoundTypesKeepTheirRuntimeGenericArguments(bool defaultPipeline)
    {
        using var test = new Case("GenericTypeResult", "ReflectionGenericCases");
        test.Optimize(defaultPipeline: defaultPipeline);
        AssertNoGenericReflection(test.Method);
        var method = test.Emit();
        foreach (var argument in new[] { typeof(int), typeof(string), typeof(int?) }) {
            Assert.Same(argument.MakeArrayType().MakePointerType().MakeByRefType(), method.MakeGenericMethod(argument).Invoke(null, null));
        }
    }

    [Fact]
    public void TypeConstructionPreservesRuntimeFailuresAndUnknownArguments()
    {
        using var invalid = new Case("InvalidTypeConstruction", "ReflectionGenericCases");
        invalid.Optimize(defaultPipeline: true);
        var method = invalid.Emit();
        for (int scenario = 0; scenario < 11; scenario++) {
            var expected = Assert.Throws<TargetInvocationException>(() => invalid.Original.Invoke(null, [scenario])).InnerException!.GetType();
            Assert.Equal(expected, Assert.Throws<TargetInvocationException>(() => method.Invoke(null, [scenario])).InnerException!.GetType());
        }
        Assert.Equal(invalid.Original.Invoke(null, [11])!.ToString(), method.Invoke(null, [11])!.ToString());

        using var ranked = new Case("RuntimeRankArrayType", "ReflectionGenericCases");
        ranked.Optimize(defaultPipeline: true);
        Assert.Contains(ranked.Method.ILBody!.Instructions, instruction => IsTypeConstruction(instruction.Operand));
        var makeArray = ranked.Emit().CreateDelegate<Func<int, Type>>();
        Assert.Same(typeof(int).MakeArrayType(1), makeArray(1));
        Assert.Same(typeof(int[,]), makeArray(2));
        Assert.Throws<IndexOutOfRangeException>(() => makeArray(0));

        using var generic = new Case("MaybeByRefLikeArray", "ReflectionGenericCases");
        generic.Optimize(defaultPipeline: true);
        Assert.Contains(generic.Method.ILBody!.Instructions, instruction => IsTypeConstruction(instruction.Operand));
        var genericArray = generic.Emit();
        Assert.Same(typeof(int[]), genericArray.MakeGenericMethod(typeof(int)).Invoke(null, null));
        Assert.IsType<TypeLoadException>(Assert.Throws<TargetInvocationException>(() => genericArray.MakeGenericMethod(typeof(Span<int>)).Invoke(null, null)).InnerException);
    }

    [FunctionPointerTypeTheory]
    [InlineData("Plain", false, false)]
    [InlineData("Plain", true, false)]
    [InlineData("Plain", false, true)]
    [InlineData("Plain", true, true)]
    [InlineData("NullParameters", false, false)]
    [InlineData("NullParameters", true, false)]
    [InlineData("Byref", false, false)]
    [InlineData("Nested", true, false)]
    [InlineData("NestedResult", false, false)]
    [InlineData("SpecialTypes", false, false)]
    [InlineData("SpecialTypes", true, false)]
    [InlineData("NullParameter", false, false)]
    [InlineData("VoidParameter", false, false)]
    [InlineData("OpenParameter", false, false)]
    [InlineData("OpenResult", false, false)]
    [InlineData("RuntimeFlag", false, false)]
    [InlineData("RuntimeFlag", true, false)]
    [InlineData("Nonvirtual", false, false)]
    public void FunctionPointerTypeConstructionPreservesSignaturesAndValidation(string scenario, bool unmanaged, bool preserveTrimming)
    {
        using var resolver = new ModuleResolver();
        resolver.AddTrustedSearchPaths();
        var typeType = resolver.Import(typeof(Type));
        var body = Utils.CreateTestMethod(resolver, typeType, PrimType.Bool);
        var builder = new IRBuilder(body.CreateBlock());
        TypeDesc resultType = scenario switch {
            "Byref" => PrimType.Int32.CreateByref(),
            "NestedResult" => new FuncPtrType(new MethodSig(PrimType.Int32, [PrimType.String], false)),
            "SpecialTypes" => PrimType.Void.CreateByref(),
            "OpenResult" => resolver.Import(typeof(List<>)),
            "NullParameters" => PrimType.Void,
            _ => PrimType.Int32
        };
        TypeDesc?[] parameterTypes = scenario switch {
            "NullParameters" => [],
            "NullParameter" => [null],
            "VoidParameter" => [PrimType.Void],
            "OpenParameter" => [resolver.Import(typeof(List<>))],
            "Byref" => [PrimType.Int32.CreatePointer(), resolver.Import(typeof(Span<int>))],
            "Nested" => [new FuncPtrType(new MethodSig(PrimType.Void, [PrimType.Int32], false)), new MDArrayType(PrimType.Int32, 2, [], [])],
            "SpecialTypes" => [PrimType.TypedRef, resolver.Import(typeof(ArgIterator)), resolver.Import(typeof(RuntimeArgumentHandle)), PrimType.Void.CreateByref()],
            _ => [PrimType.String, PrimType.Int32.CreateByref()]
        };
        Value parameters = ConstNull.Create();
        if (scenario != "NullParameters") {
            parameters = builder.CreateNewArray(typeType, ConstInt.CreateI(parameterTypes.Length));
            for (int i = 0; i < parameterTypes.Length; i++) {
                builder.CreateArrayStore(parameters, ConstInt.CreateI(i), parameterTypes[i] is { } parameter ? ReflectType(builder, parameter) : ConstNull.Create());
            }
        }
        var factory = typeType.FindMethod("MakeFunctionPointerType", new MethodSig(typeType, [typeType.CreateArray(), PrimType.Bool], true));
        var call = builder.CreateCallVirt(factory, ReflectType(builder, resultType), parameters,
            scenario == "RuntimeFlag" ? body.Args[0] : ConstInt.CreateI(unmanaged ? 1 : 0));
        if (scenario == "Nonvirtual") call.IsVirtual = false;
        builder.CreateReturn(call);

        var original = Utils.Compile<Func<bool, Type>>(body);
        Type? expected = null;
        var error = Record.Exception(() => expected = original(unmanaged));
        var compilation = new Compilation(body.Definition.Module, new VoidLogger(), new() {
            VerifyEachPass = true, PreserveTrimmingDependencies = preserveTrimming
        });
        Assert.True(CompilationPipeline.CreateDefault(compilation).Run([body.Definition]).Succeeded);
        var optimized = Utils.Compile<Func<bool, Type>>(body.Definition.Body!);
        if (error != null) Assert.IsType(error.GetType(), Record.Exception(() => optimized(unmanaged)));
        else Assert.Same(expected, optimized(unmanaged));
        bool retained = error != null || preserveTrimming || scenario == "RuntimeFlag";
        Assert.Equal(retained, body.Definition.Body!.Instructions().OfType<CallInst>().Any(instruction => IsTypeConstruction(instruction.Method)));
    }

    private static Value ReflectType(IRBuilder builder, TypeDesc type)
        => builder.CreateCall(builder.Resolver.Import(typeof(Type)).FindMethod("GetTypeFromHandle"),
            builder.Emit(new CilIntrinsic.LoadHandle(builder.Resolver, type)));

    private sealed class FunctionPointerTypeTheoryAttribute : TheoryAttribute
    {
        public FunctionPointerTypeTheoryAttribute()
        {
            if (typeof(Type).GetMethod("MakeFunctionPointerType") == null) Skip = "Requires Type.MakeFunctionPointerType (.NET 11 or later).";
        }
    }

    [Theory]
    [InlineData("InvalidStructConstraint")]
    [InlineData("InvalidTypeConstraint")]
    [InlineData("InvalidConstructorConstraint")]
    [InlineData("InvalidInterfaceConstraint")]
    [InlineData("MissingTypeArgument")]
    [InlineData("WrongArity")]
    [InlineData("AlreadyConstructed")]
    [InlineData("OpenGenericVirtualDelegate")]
    [InlineData("ChangedTypeArguments")]
    [InlineData("OpenArrayTypeResult")]
    [InlineData("OpenPointerTypeResult")]
    [InlineData("OpenByrefTypeResult")]
    public void UnprovenGenericConstructionsRetainRuntimeBehavior(string name)
    {
        using var test = new Case(name, "ReflectionGenericCases");
        var expected = test.Original.Invoke(null, null);
        test.Optimize();
        Assert.Contains(test.Method.ILBody!.Instructions, instruction => IsGenericReflection(instruction.Operand));
        Assert.Equal(expected, test.Emit().Invoke(null, null));
    }

    [Theory]
    [InlineData("OpaqueGenericInvoke")]
    [InlineData("GenericUnknownConstraint")]
    public void UnknownGenericValuesKeepInvocationValidation(string name)
    {
        using var test = new Case(name, "ReflectionGenericCases");
        test.Optimize(defaultPipeline: true);
        Assert.Contains(test.Method.ILBody!.Instructions, instruction => IsGenericReflection(instruction.Operand));
        var emitted = test.Emit();
        foreach (var (type, value) in new (Type, object)[] { (typeof(int), 42), (typeof(string), "hello") }) {
            Assert.Equal(test.Original.MakeGenericMethod(type).Invoke(null, [value]), emitted.MakeGenericMethod(type).Invoke(null, [value]));
        }
        if (name == "OpaqueGenericInvoke") {
            var original = test.Original.MakeGenericMethod(typeof(object)).CreateDelegate<Func<object, object>>();
            var run = emitted.MakeGenericMethod(typeof(object)).CreateDelegate<Func<object, object>>();
            Assert.Equal(original(Type.Missing), run(Type.Missing));
        }
    }

    [Fact]
    public void IndirectClassConstraintsDoNotExcludeBoxedValueTypes()
    {
        using var test = new Case("IndirectReferenceConstraint", "ReflectionGenericCases");
        Assert.Equal("ArgumentException", test.Original.MakeGenericMethod(typeof(int), typeof(object)).Invoke(null, [42]));
        test.Optimize(defaultPipeline: true);
        Assert.Contains(test.Method.ILBody!.Instructions, instruction => IsGenericReflection(instruction.Operand));
        var emitted = test.Emit();
        Assert.Equal("ArgumentException", emitted.MakeGenericMethod(typeof(int), typeof(object)).Invoke(null, [42]));
        Assert.Equal("hello", emitted.MakeGenericMethod(typeof(string), typeof(object)).Invoke(null, ["hello"]));
    }

    [Theory]
    [InlineData("MethodDelegate", "MakeGenericMethod")]
    [InlineData("ConstructedType", "MakeGenericType")]
    [InlineData("ArrayTypeResult", "MakeArrayType")]
    [InlineData("PointerTypeResult", "MakePointerType")]
    [InlineData("ByrefTypeResult", "MakeByRefType")]
    public void NonvirtualReflectionBaseCallsRetainTheirImplementation(string name, string operation)
    {
        using var test = new Case(name, "ReflectionGenericCases");
        var instructions = test.Method.ILBody!.Instructions;
        for (int i = 0; i < instructions.Count; i++) {
            if (instructions[i].Operand is MethodDesc { Name: var methodName } && methodName == operation) {
                var instruction = instructions[i];
                instruction.OpCode = ILCode.Call;
                instructions[i] = instruction;
            }
        }
        var expected = Assert.Throws<TargetInvocationException>(() => test.Emit().Invoke(null, null)).InnerException!.GetType();
        test.Optimize(defaultPipeline: true);
        Assert.Equal(expected, Assert.Throws<TargetInvocationException>(() => test.Emit().Invoke(null, null)).InnerException!.GetType());
    }

    [Theory]
    [InlineData("InstanceHandle", "Owner`1", false, "Read")]
    [InlineData("OpenOwnerHandle", "Owner`1", true, "Read")]
    [InlineData("AbstractHandle", "Abstract", false, "Read")]
    [InlineData("GenericDefinitionHandle", "Target", false, "Echo")]
    [InlineData("OpenInheritedHandle", "GenericDerived`1", true, "Echo", "ReflectionCases", false)]
    public void MethodHandlesKeepTheirMetadataIdentity(string name, string owner, bool open, string member,
        string caseType = "ReflectionGenericCases", bool folds = true)
    {
        foreach (bool pipeline in new[] { false, true }) {
            using var test = new Case(name, caseType);
            test.Optimize(defaultPipeline: pipeline);
            var run = test.Emit();
            var declaringType = run.DeclaringType!.GetNestedType(owner)!;
            if (declaringType.IsGenericTypeDefinition && !open) declaringType = declaringType.MakeGenericType(typeof(int));
            Assert.Equal(declaringType.GetMethod(member)!.MethodHandle, (RuntimeMethodHandle)run.Invoke(null, null)!);
            Assert.Equal(!folds, test.Method.ILBody!.Instructions.Any(instruction => IsReflectionFactory(instruction.Operand)));
        }
    }

    private static bool IsGenericReflection(object? operand)
        => IsReflectionAccess(operand) || IsReflectionFactory(operand) || IsTypeConstruction(operand) ||
            operand is MethodDesc { Name: "MakeGenericMethod" } method && method.DeclaringType.IsCorelibType(typeof(MethodInfo));

    private static bool IsTypeConstruction(object? operand)
        => operand is MethodDesc { Name: "MakeArrayType" or "MakePointerType" or "MakeByRefType" or "MakeFunctionPointerType" or "MakeGenericType" } method &&
            method.DeclaringType.IsCorelibType(typeof(Type));

    private static void AssertNoGenericReflection(MethodDef method)
        => Assert.DoesNotContain(method.ILBody!.Instructions, instruction => IsGenericReflection(instruction.Operand));
}
