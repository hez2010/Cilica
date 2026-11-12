namespace Cilica.Tests.CodeGen;

using System.Reflection;
using Cilica.AsmIO;
using Cilica.CodeGen.Cil;
using Cilica.Frontend;
using Cilica.IR;
using Cilica.IR.Utils;

[Collection("ModuleResolver")]
public class StackReuseTests(ModuleResolverFixture fixture)
{
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(8)]
    public void RepeatedArgumentsUseDupWithoutTemporaries(int count)
    {
        var callback = fixture.Resolver.Import(typeof(Func<int>));
        var body = Utils.CreateTestMethod(fixture.Resolver, PrimType.Int32, callback);
        var sum = body.Definition.DeclaringType.CreateMethod("Sum", PrimType.Int32,
            [.. Enumerable.Range(0, count).Select(i => new ParamDef(PrimType.Int32, "value" + i))],
            MethodAttributes.Public | MethodAttributes.Static);
        sum.Body = new(sum);
        var callee = new IRBuilder(sum.Body.CreateBlock());
        Value result = sum.Body.Args[0];
        for (int i = 1; i < count; i++) result = callee.CreateAdd(result, sum.Body.Args[i]);
        callee.CreateReturn(result);
        var builder = new IRBuilder(body.CreateBlock());
        var value = builder.CreateCallVirt(callback.FindMethod("Invoke"), body.Args[0]);
        builder.CreateReturn(builder.CreateCall(sum, Enumerable.Repeat<Value>(value, count).ToArray()));

        for (int round = 0; round < 2; round++) {
            var il = ILGenerator.GenerateCode(body);
            Assert.Empty(il.Locals);
            Assert.Equal(count - 1, il.Instructions.Count(inst => inst.OpCode == ILCode.Dup));
            Assert.Equal(count + 11, il.Instructions.Sum(inst => inst.GetSize()));
            Assert.Equal(count, il.MaxStack);
            int calls = 0;
            var run = Utils.Compile<Func<Func<int>, int>>(body);
            Assert.Equal(count * 7, run(() => { calls++; return 7; }));
            Assert.Equal(1, calls);
            body = ILImporter.ParseCode(body.Definition);
        }
    }

    [Fact]
    public void ArrayInitializationKeepsTheArrayOnTheStack()
    {
        var body = Utils.CreateTestMethod(fixture.Resolver, PrimType.Int32.CreateArray());
        var builder = new IRBuilder(body.CreateBlock());
        var array = builder.Emit(new CilIntrinsic.NewArray(PrimType.Int32, ConstInt.CreateI(2)));
        builder.CreateArrayStore(array, ConstInt.CreateI(0), ConstInt.CreateI(42));
        builder.CreateArrayStore(array, ConstInt.CreateI(1), ConstInt.CreateI(43));
        builder.CreateReturn(array);

        var il = ILGenerator.GenerateCode(body);
        Assert.Empty(il.Locals);
        Assert.Equal(2, il.Instructions.Count(inst => inst.OpCode == ILCode.Dup));
        Assert.Equal(4, il.MaxStack);
        Assert.Equal(new[] { 42, 43 }, Utils.Compile<Func<int[]>>(body)());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void StackOnlyReferencesDoNotLeaveClearingLocals(int lifetimes)
    {
        var body = Utils.CreateTestMethod(fixture.Resolver, PrimType.Void);
        var builder = new IRBuilder(body.CreateBlock());
        var keepAlive = fixture.Resolver.Import(typeof(GC)).FindMethod("KeepAlive");
        for (int i = 0; i < lifetimes; i++) {
            var value = builder.CreateNewObj(fixture.Resolver.Import(typeof(object)).FindMethod(".ctor"));
            builder.CreateCall(keepAlive, value);
            builder.CreateCall(keepAlive, value);
        }
        builder.CreateReturn();

        var il = ILGenerator.GenerateCode(body);
        Assert.Empty(il.Locals);
        Assert.Equal(lifetimes, il.Instructions.Count(inst => inst.OpCode == ILCode.Dup));
        Assert.DoesNotContain(il.Instructions, inst => inst.OpCode == ILCode.Ldnull);
        Assert.Equal(2, il.MaxStack);
        Utils.Compile<Action>(body)();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ReusingARegisterReleasesThePreviousReference(bool wrapInStruct, bool initLocals)
    {
        var references = new List<WeakReference>();
        TypeDesc valueType = wrapInStruct ? fixture.Resolver.Import(typeof(ValueTuple<object>)) : PrimType.Object;
        var factory = fixture.Resolver.Import(wrapInStruct ? typeof(Func<ValueTuple<object>>) : typeof(Func<object>));
        var probe = fixture.Resolver.Import(typeof(Func<bool>));
        var body = Utils.CreateTestMethod(fixture.Resolver, PrimType.Bool, factory, probe);
        body.InitLocals = initLocals;
        body.Definition.ImplAttribs = MethodImplAttributes.NoOptimization | MethodImplAttributes.NoInlining;
        var entry = body.CreateBlock();
        var work = body.CreateBlock();
        var unused = body.CreateBlock();
        var builder = new IRBuilder(entry);
        var keepAlive = fixture.Resolver.Import(typeof(GC)).FindMethod("KeepAlive");
        var referenceEquals = fixture.Resolver.Import(typeof(object)).FindMethod("ReferenceEquals");
        var a = builder.CreateCallVirt(factory.FindMethod("Invoke"), body.Args[0]);
        builder.CreateCall(keepAlive, GetObject(builder, a));
        builder.SetBranch(builder.CreateCall(referenceEquals, ConstNull.Create(), GetObject(builder, a)), unused, work);
        unused.SetBranch(new ReturnInst(ConstInt.CreateI(0)));
        builder = new IRBuilder(work);
        var b = builder.CreateCallVirt(factory.FindMethod("Invoke"), body.Args[0]);
        builder.CreateCall(keepAlive, GetObject(builder, b));
        var collected = builder.CreateCallVirt(probe.FindMethod("Invoke"), body.Args[1]);
        builder.CreateCall(keepAlive, GetObject(builder, b));
        builder.CreateReturn(collected);
        var document = new DebugSourceDocument { Name = "StackReuse.cs" };
        b.DebugLocation = new(document, 1, 1, 1, 20);
        collected.DebugLocation = new(document, 2, 2, 1, 20);
        var points = new SequencePointBuilder(body.Definition);
        var il = ILGenerator.GenerateCode(body, points);
        Assert.Contains(il.Instructions, inst => inst.OpCode == ILCode.Dup);
        var probePoint = Assert.Single(points.BuildSymbols(il.Instructions)!.SequencePoints.Where(point => point.StartLine == 2));
        Assert.Equal(ILCode.Ldarg_1, il.Instructions.Single(inst => inst.Offset == probePoint.Offset).OpCode);

        bool result = wrapInStruct
            ? Utils.Compile<Func<Func<ValueTuple<object>>, Func<bool>, bool>>(body)(() => new(CreateValue(references)), () => Probe(references))
            : Utils.Compile<Func<Func<object>, Func<bool>, bool>>(body)(() => CreateValue(references), () => Probe(references));
        Assert.True(result);
        Assert.Equal(2, references.Count);

        Value GetObject(IRBuilder target, Value value) => wrapInStruct
            ? target.Emit(new FieldExtractInst(valueType.FindField("Item1"), value)) : value;

        static object CreateValue(List<WeakReference> references)
        {
            var value = new object();
            references.Add(new WeakReference(value));
            return value;
        }

        static bool Probe(List<WeakReference> references)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            return !references[0].IsAlive && references[1].IsAlive;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SharedValuesPreserveBothConditionalPaths(bool select)
    {
        var callback = fixture.Resolver.Import(typeof(Func<int>));
        var body = Utils.CreateTestMethod(fixture.Resolver, PrimType.Int32, callback, PrimType.Bool);
        var builder = new IRBuilder(body.CreateBlock());
        var value = builder.CreateCallVirt(callback.FindMethod("Invoke"), body.Args[0]);
        if (select) {
            builder.CreateReturn(builder.CreateSelect(body.Args[1], builder.CreateAdd(value, value), builder.CreateSub(value, ConstInt.CreateI(1))));
        } else {
            var yes = body.CreateBlock();
            var no = body.CreateBlock();
            builder.SetBranch(body.Args[1], yes, no);
            var yesBuilder = new IRBuilder(yes);
            yesBuilder.CreateReturn(yesBuilder.CreateAdd(value, value));
            var noBuilder = new IRBuilder(no);
            noBuilder.CreateReturn(noBuilder.CreateSub(value, ConstInt.CreateI(1)));
        }

        var il = ILGenerator.GenerateCode(body);
        Assert.Single(il.Locals);
        Assert.Contains(il.Instructions, inst => inst.OpCode == ILCode.Dup);
        var run = Utils.Compile<Func<Func<int>, bool, int>>(body);
        foreach (bool choose in new[] { false, true }) {
            int calls = 0;
            Assert.Equal(choose ? 14 : 6, run(() => { calls++; return 7; }, choose));
            Assert.Equal(1, calls);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OverlappingValuesPreserveOperandAndEffectOrder(bool nested)
    {
        var callback = fixture.Resolver.Import(typeof(Func<int>));
        var body = Utils.CreateTestMethod(fixture.Resolver, PrimType.Int32, callback, callback);
        var builder = new IRBuilder(body.CreateBlock());
        var invoke = callback.FindMethod("Invoke");
        var a = builder.CreateCallVirt(invoke, body.Args[0]);
        var b = builder.CreateCallVirt(invoke, body.Args[1]);
        var first = builder.CreateMul(nested ? b : a, nested ? b : a);
        var second = builder.CreateAdd(nested ? a : b, nested ? a : b);
        builder.CreateReturn(builder.CreateSub(first, second));

        var run = Utils.Compile<Func<Func<int>, Func<int>, int>>(body);
        var trace = new List<string>();
        Assert.Equal(nested ? 43 : -5, run(() => { trace.Add("a"); return 3; }, () => { trace.Add("b"); return 7; }));
        Assert.Equal(new[] { "a", "b" }, trace);
    }

    [Fact]
    public void NestedTemporariesShareTheEvaluationStack()
    {
        var callback = fixture.Resolver.Import(typeof(Func<int>));
        var body = Utils.CreateTestMethod(fixture.Resolver, PrimType.Int32, callback, callback);
        var builder = new IRBuilder(body.CreateBlock());
        var invoke = callback.FindMethod("Invoke");
        var a = builder.CreateCallVirt(invoke, body.Args[0]);
        var b = builder.CreateCallVirt(invoke, body.Args[1]);
        var abs = fixture.Resolver.Import(typeof(Math)).FindMethod("Abs", new MethodSig(PrimType.Int32, [PrimType.Int32]));
        builder.CreateCall(abs, b);
        builder.CreateCall(abs, b);
        builder.CreateReturn(builder.CreateAdd(a, a));

        var il = ILGenerator.GenerateCode(body);
        Assert.Empty(il.Locals);
        Assert.Equal(3, il.MaxStack);
        var trace = new List<string>();
        var run = Utils.Compile<Func<Func<int>, Func<int>, int>>(body);
        Assert.Equal(6, run(() => { trace.Add("a"); return 3; }, () => { trace.Add("b"); return -7; }));
        Assert.Equal(new[] { "a", "b" }, trace);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(32)]
    [InlineData(256)]
    public void ManyNestedTemporariesPreserveConsumptionOrder(int count)
    {
        var callback = fixture.Resolver.Import(typeof(Func<int>));
        var body = Utils.CreateTestMethod(fixture.Resolver, PrimType.Int32, callback);
        var (consume, trace) = CreateRecorder(body);
        var builder = new IRBuilder(body.CreateBlock());
        var invoke = callback.FindMethod("Invoke");
        var outer = builder.CreateCallVirt(invoke, body.Args[0]);
        int expected = 0;
        for (int group = 0; group < 2; group++) {
            var values = new Value[count];
            for (int i = 0; i < count; i++) values[i] = builder.CreateCallVirt(invoke, body.Args[0]);
            for (int i = count - 1; i >= 0; i--) {
                for (int use = 0; use < 2; use++) {
                    builder.CreateCall(consume, values[i]);
                    expected = unchecked(expected * 31 + group * count + i + 2);
                }
            }
            builder.CreateCall(consume, outer);
            expected = unchecked(expected * 31 + 1);
        }
        builder.CreateReturn(builder.CreateFieldLoad(trace));

        var il = ILGenerator.GenerateCode(body);
        Assert.Empty(il.Locals);
        Assert.Equal(count + 2, il.MaxStack);
        int calls = 0;
        var run = Utils.Compile<Func<Func<int>, int>>(body);
        Assert.Equal(expected, run(() => ++calls));
        Assert.Equal(2 * count + 1, calls);
    }

    [Fact]
    public void CrossingTemporariesKeepTheirValues()
    {
        var callback = fixture.Resolver.Import(typeof(Func<int>));
        var body = Utils.CreateTestMethod(fixture.Resolver, PrimType.Int32, callback);
        var (consume, trace) = CreateRecorder(body);
        var builder = new IRBuilder(body.CreateBlock());
        var invoke = callback.FindMethod("Invoke");
        var a = builder.CreateCallVirt(invoke, body.Args[0]);
        var b = builder.CreateCallVirt(invoke, body.Args[0]);
        builder.CreateCall(consume, a);
        builder.CreateCall(consume, b);
        builder.CreateCall(consume, b);
        builder.CreateCall(consume, a);
        builder.CreateReturn(builder.CreateFieldLoad(trace));

        var il = ILGenerator.GenerateCode(body);
        Assert.Single(il.Locals);
        Assert.Single(il.Instructions.Where(inst => inst.OpCode == ILCode.Dup));
        int calls = 0;
        var run = Utils.Compile<Func<Func<int>, int>>(body);
        Assert.Equal(((1 * 31 + 2) * 31 + 2) * 31 + 1, run(() => ++calls));
        Assert.Equal(2, calls);
    }

    private static (MethodDef Consume, FieldDef Trace) CreateRecorder(Cilica.IR.MethodBody body)
    {
        var owner = body.Definition.DeclaringType;
        var trace = owner.CreateField("Trace", PrimType.Int32, FieldAttributes.Public | FieldAttributes.Static);
        var consume = owner.CreateMethod("Consume", PrimType.Void, [new ParamDef(PrimType.Int32, "value")],
            MethodAttributes.Public | MethodAttributes.Static);
        consume.Body = new(consume);
        var builder = new IRBuilder(consume.Body.CreateBlock());
        var previous = builder.CreateMul(builder.CreateFieldLoad(trace), ConstInt.CreateI(31));
        builder.CreateFieldStore(trace, null, builder.CreateAdd(previous, consume.Body.Args[0]));
        builder.CreateReturn();
        return (consume, trace);
    }

    [Fact]
    public void RetainedValuesSurviveIndirectCalls()
    {
        var body = Utils.CreateTestMethod(fixture.Resolver, PrimType.Int32, PrimType.Int32);
        var target = body.Definition.DeclaringType.CreateMethod("Target", PrimType.Int32,
            [new ParamDef(PrimType.Int32, "value")], MethodAttributes.Public | MethodAttributes.Static);
        target.Body = new(target);
        var callee = new IRBuilder(target.Body.CreateBlock());
        callee.CreateReturn(callee.CreateMul(target.Body.Args[0], target.Body.Args[0]));
        var builder = new IRBuilder(body.CreateBlock());
        var value = builder.CreateAdd(body.Args[0], ConstInt.CreateI(1));
        var signature = new FuncPtrType(new MethodSig(PrimType.Int32, [PrimType.Int32], isInstance: false));
        var address = builder.Emit(new FuncAddrInst(target));
        builder.Emit(new CilIntrinsic.CallIndirect(signature, address, [value]));
        builder.CreateReturn(builder.CreateAdd(value, value));

        var il = ILGenerator.GenerateCode(body);
        Assert.Empty(il.Locals);
        Assert.Equal(3, il.MaxStack);
        Assert.Equal(16, Utils.Compile<Func<int, int>>(body)(7));
    }

    [Fact]
    public void RetainedValuesDoNotCrossStackAllocation()
    {
        var callback = fixture.Resolver.Import(typeof(Func<int>));
        var body = Utils.CreateTestMethod(fixture.Resolver, PrimType.Int32, callback);
        var builder = new IRBuilder(body.CreateBlock());
        var value = builder.CreateCallVirt(callback.FindMethod("Invoke"), body.Args[0]);
        builder.CreateCall(fixture.Resolver.Import(typeof(Math)).FindMethod("Abs", new MethodSig(PrimType.Int32, [PrimType.Int32])), value);
        var allocation = builder.Emit(new CilIntrinsic.Alloca(ConstInt.CreateI(4)));
        builder.Emit(new StoreInst(allocation, ConstInt.CreateI(42), PrimType.Int32));
        builder.CreateReturn(builder.CreateAdd(value, value));

        Assert.Contains(ILGenerator.GenerateCode(body).Locals, local => local.Type == PrimType.Int32);
        var run = Utils.Compile<Func<Func<int>, int>>(body);
        Assert.Equal(14, run(() => 7));
    }

    [Fact]
    public void RepeatedStructArgumentsUseStackCopies()
    {
        var pair = fixture.Resolver.Import(typeof(ValueTuple<int, int>));
        var result = fixture.Resolver.Import(typeof(ValueTuple<(int, int), (int, int)>));
        var body = Utils.CreateTestMethod(fixture.Resolver, result, PrimType.Int32);
        var builder = new IRBuilder(body.CreateBlock());
        var value = builder.CreateNewObj(pair.FindMethod(".ctor"), body.Args[0], ConstInt.CreateI(42));
        builder.CreateReturn(builder.CreateNewObj(result.FindMethod(".ctor"), value, value));

        var il = ILGenerator.GenerateCode(body);
        Assert.Empty(il.Locals);
        Assert.Single(il.Instructions.Where(inst => inst.OpCode == ILCode.Dup));
        Assert.Equal(((7, 42), (7, 42)), Utils.Compile<Func<int, ((int, int), (int, int))>>(body)(7));
    }

    [Fact]
    public void NarrowTemporariesKeepTheirTruncatingStore()
    {
        var body = Utils.CreateTestMethod(fixture.Resolver, PrimType.Int32, PrimType.Byte);
        var builder = new IRBuilder(body.CreateBlock());
        var value = builder.Emit(new BinaryInst(BinaryOp.Or, body.Args[0], body.Args[0]));
        value.Right = ConstInt.CreateI(0x100);
        builder.CreateReturn(builder.CreateAdd(value, value));

        var il = ILGenerator.GenerateCode(body);
        Assert.Single(il.Locals);
        Assert.Single(il.Instructions.Where(inst => inst.OpCode == ILCode.Dup));
        Assert.Equal(14, Utils.Compile<Func<byte, int>>(body)(7));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeValuesKeepImplicitTruncationAtASelectResult(bool nested)
    {
        var body = Utils.CreateTestMethod(fixture.Resolver, PrimType.Int64, PrimType.IntPtr, PrimType.Bool);
        var builder = new IRBuilder(body.CreateBlock());
        var negative = builder.Emit(new UnaryInst(UnaryOp.Neg, body.Args[0]));
        var value = builder.Emit(new SelectInst(body.Args[1], Wrap(body.Args[0]), Wrap(negative), PrimType.Int32));
        builder.CreateReturn(builder.CreateConvert(builder.CreateAdd(value, value), PrimType.Int64));

        var run = Utils.Compile<Func<nint, bool, long>>(body);
        var input = unchecked((nint)0x100000007L);
        Assert.Equal(14, run(input, true));
        Assert.Equal(-14, run(input, false));

        Value Wrap(Value operand)
        {
            if (!nested) return operand;
            var wrapped = builder.Emit(new BinaryInst(BinaryOp.Or, ConstInt.CreateI(0), ConstInt.CreateI(0)));
            wrapped.Left = operand;
            return wrapped;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RewrittenBinaryOperandsKeepTheResultStorageType(bool nested)
    {
        var body = Utils.CreateTestMethod(fixture.Resolver, PrimType.Int64, PrimType.IntPtr);
        var builder = new IRBuilder(body.CreateBlock());
        var value = builder.Emit(new BinaryInst(BinaryOp.Or, ConstInt.CreateI(0), ConstInt.CreateI(1)));
        value.Left = body.Args[0];
        if (nested) value = builder.Emit(new BinaryInst(BinaryOp.Or, value, ConstInt.CreateI(1)));
        builder.CreateReturn(builder.CreateConvert(builder.CreateAdd(value, value), PrimType.Int64));

        var run = Utils.Compile<Func<nint, long>>(body);
        Assert.Equal(14, run(unchecked((nint)0x100000007L)));
    }

    [Fact]
    public unsafe void RewrittenPointerBasesKeepManagedReferenceStorage()
    {
        var byref = PrimType.Int32.CreateByref();
        var body = Utils.CreateTestMethod(fixture.Resolver, PrimType.Int32, PrimType.IntPtr);
        var increment = body.Definition.DeclaringType.CreateMethod("Increment", PrimType.Void,
            [new ParamDef(byref, "value")], MethodAttributes.Public | MethodAttributes.Static);
        increment.Body = new(increment);
        var callee = new IRBuilder(increment.Body.CreateBlock());
        callee.CreateStore(increment.Body.Args[0], callee.CreateAdd(callee.CreateLoad(increment.Body.Args[0]), ConstInt.CreateI(1)));
        callee.CreateReturn();
        var builder = new IRBuilder(body.CreateBlock());
        var address = builder.Emit(new PtrOffsetInst(body.CreateVar(PrimType.Int32), ConstInt.CreateI(0), 1));
        address.BasePtr = body.Args[0];
        for (int i = 0; i < 4; i++) builder.CreateCall(increment, address);
        builder.CreateReturn(builder.CreateLoad(address));

        Assert.Equal(byref, Assert.Single(ILGenerator.GenerateCode(body).Locals).Type);
        var run = Utils.Compile<Func<nint, int>>(body);
        int value = 3;
        Assert.Equal(7, run((nint)(&value)));
        Assert.Equal(7, value);
    }

    [Fact]
    public void FloatingPointTemporariesKeepTheirStoragePrecision()
    {
        var body = Utils.CreateTestMethod(fixture.Resolver, PrimType.Single, PrimType.Single, PrimType.Single);
        var builder = new IRBuilder(body.CreateBlock());
        var value = builder.Emit(new BinaryInst(BinaryOp.FAdd, body.Args[0], body.Args[1]));
        builder.CreateReturn(builder.Emit(new BinaryInst(BinaryOp.FAdd, value, value)));

        Assert.Single(ILGenerator.GenerateCode(body).Locals);
        var run = Utils.Compile<Func<float, float, float>>(body);
        Assert.Equal(33554432f, run(16777216f, 1f));
        Assert.True(float.IsNaN(run(float.NaN, 1f)));
    }

    [Fact]
    public void SequencePointsRemainAtEmittedInstructionOffsets()
    {
        var body = Utils.CreateTestMethod(fixture.Resolver, PrimType.Int32, PrimType.Int32);
        var document = new DebugSourceDocument { Name = "StackReuse.cs" };
        var builder = new IRBuilder(body.CreateBlock());
        var value = builder.Emit(new BinaryInst(BinaryOp.Add, body.Args[0], ConstInt.CreateI(3)));
        value.DebugLocation = new(document, 1, 1, 1, 20);
        var result = builder.Emit(new BinaryInst(BinaryOp.Mul, value, value));
        result.DebugLocation = new(document, 2, 2, 1, 20);
        builder.CreateReturn(result).DebugLocation = new(document, 3, 3, 1, 20);
        var points = new SequencePointBuilder(body.Definition);

        var il = ILGenerator.GenerateCode(body, points);
        Assert.Empty(il.Locals);
        var symbols = points.BuildSymbols(il.Instructions)!;
        Assert.Equal(new[] { 1, 3 }, symbols.SequencePoints.Select(point => point.StartLine));
        var offsets = symbols.SequencePoints.Select(point => point.Offset).ToArray();
        Assert.Equal(offsets.Distinct().Order(), offsets);
        Assert.All(offsets, offset => Assert.Contains(il.Instructions, inst => inst.Offset == offset));
        Assert.Equal(100, Utils.Compile<Func<int, int>>(body)(7));
    }
}
