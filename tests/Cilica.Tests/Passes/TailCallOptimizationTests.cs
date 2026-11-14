namespace Cilica.Tests.Passes;

using System.Reflection;
using Cilica.AsmIO;
using Cilica.IR;
using Cilica.IR.Utils;
using Cilica.Passes;
using MethodBody = Cilica.IR.MethodBody;

[Collection("ModuleResolver")]
public class TailCallOptimizationTests(ModuleResolverFixture fixture) : PassTestBase(fixture)
{
    [Fact]
    public void ReturningTheResultOfACallDirectlyBecomesATailCall()
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Int32, PrimType.Int32);
        var callee = CreateIdentityCallee(body);
        var builder = new IRBuilder(body.CreateBlock());
        var call = builder.CreateCall(callee, body.Args[0]);
        builder.CreateReturn(call);

        Run(body, new TailCallOptimization());

        Assert.True(call.IsTailCall);
        Assert.Equal(7, Utils.Compile<Func<int, int>>(body)(7));
    }

    [Fact]
    public void ReturningAVoidCallBecomesATailCall()
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Void);
        var callee = CreateVoidCallee(body);
        var builder = new IRBuilder(body.CreateBlock());
        var call = builder.CreateCall(callee);
        builder.CreateReturn();

        Run(body, new TailCallOptimization());

        Assert.True(call.IsTailCall);
        Utils.Compile<Action>(body)();
    }

    [Fact]
    public void ComputingAValueFromTheCallResultIsNotATailCall()
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Int32, PrimType.Int32);
        var callee = CreateIdentityCallee(body);
        var builder = new IRBuilder(body.CreateBlock());
        var call = builder.CreateCall(callee, body.Args[0]);
        builder.CreateReturn(builder.CreateAdd(call, ConstInt.CreateI(1)));

        Run(body, new TailCallOptimization());

        Assert.False(call.IsTailCall);
        Assert.Equal(8, Utils.Compile<Func<int, int>>(body)(7));
    }

    [Fact]
    public void CallsInsideAProtectedRegionAreNotTailCalls()
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Int32, PrimType.Int32);
        var callee = CreateIdentityCallee(body);
        var entry = body.CreateBlock();
        var guarded = body.CreateBlock();
        var handler = body.CreateBlock();
        var exit = body.CreateBlock();
        entry.SetBranch(guarded);

        guarded.InsertLast(new GuardInst(GuardKind.Catch, handler, _modResolver.Import(typeof(Exception))));
        var guardedCall = new CallInst(callee, [body.Args[0]]);
        guarded.InsertLast(guardedCall);
        guarded.SetBranch(new LeaveInst(exit));

        var handlerCall = new CallInst(callee, [body.Args[0]]);
        handler.InsertLast(handlerCall);
        handler.SetBranch(new LeaveInst(exit));

        var phi = exit.InsertPhi(new PhiInst(PrimType.Int32, (guarded, guardedCall), (handler, handlerCall)));
        exit.SetBranch(new ReturnInst(phi));

        Run(body, new TailCallOptimization());

        Assert.False(guardedCall.IsTailCall);
        Assert.False(handlerCall.IsTailCall);
        Assert.Equal(5, Utils.Compile<Func<int, int>>(body)(5));
    }

    [Fact]
    public void ReturnsOfAWiderReferenceTypeAreNotTailCalls()
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Object);
        var callee = CreateStringCallee(body);
        var builder = new IRBuilder(body.CreateBlock());
        var call = builder.CreateCall(callee);
        builder.CreateReturn(call);

        Run(body, new TailCallOptimization());

        Assert.False(call.IsTailCall);
    }

    [Fact]
    public void MethodsAllocatingOnTheStackAreNotOptimized()
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Int32, PrimType.Int32);
        var callee = CreateIdentityCallee(body);
        var builder = new IRBuilder(body.CreateBlock());
        builder.Emit(new CilIntrinsic.Alloca(ConstInt.CreateI(16)));
        var call = builder.CreateCall(callee, body.Args[0]);
        builder.CreateReturn(call);

        Run(body, new TailCallOptimization());

        Assert.False(call.IsTailCall);
    }

    [Fact]
    public void ReturnsSunkIntoASharedExitBlockAreHoistedIntoTheCallingBlocks()
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Int32, PrimType.Int32);
        var callee = CreateIdentityCallee(body);
        var entry = body.CreateBlock();
        var positive = body.CreateBlock();
        var other = body.CreateBlock();
        var exit = body.CreateBlock();

        var entryBuilder = new IRBuilder(entry);
        entry.SetBranch(new BranchInst(entryBuilder.CreateSgt(body.Args[0], ConstInt.CreateI(0)), positive, other));

        var positiveBuilder = new IRBuilder(positive);
        var positiveCall = positiveBuilder.CreateCall(callee, body.Args[0]);
        positive.SetBranch(exit);

        var otherBuilder = new IRBuilder(other);
        var otherCall = otherBuilder.CreateCall(callee, ConstInt.CreateI(0));
        other.SetBranch(exit);

        var phi = exit.InsertPhi(new PhiInst(PrimType.Int32, (positive, positiveCall), (other, otherCall)));
        new IRBuilder(phi, InsertionDir.After).CreateReturn(phi);

        Run(body, new TailCallOptimization());

        Assert.True(positiveCall.IsTailCall);
        Assert.True(otherCall.IsTailCall);
        Assert.IsType<ReturnInst>(positive.Last);
        Assert.IsType<ReturnInst>(other.Last);
        Assert.Equal(3, body.NumBlocks);
        Assert.Equal(0, exit.NumPreds);

        var run = Utils.Compile<Func<int, int>>(body);
        Assert.Equal(3, run(3));
        Assert.Equal(0, run(-3));
    }

    [Fact]
    public void RunningThePassTwiceLeavesAnExistingTailCallAlone()
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Int32, PrimType.Int32);
        var callee = CreateIdentityCallee(body);
        var builder = new IRBuilder(body.CreateBlock());
        var call = builder.CreateCall(callee, body.Args[0]);
        builder.CreateReturn(call);

        Run(body, new TailCallOptimization(), new TailCallOptimization());

        Assert.True(call.IsTailCall);
        Assert.Single(body.Instructions().OfType<CallInst>());
    }

    /// <summary> Creates a static <c>int Callee(int)</c> method that returns its argument unchanged. </summary>
    private MethodDef CreateIdentityCallee(MethodBody body, string name = "Callee")
    {
        var callee = CreateCallee(body, name, PrimType.Int32, PrimType.Int32);
        var calleeBody = callee.Body!;
        new IRBuilder(calleeBody.CreateBlock()).CreateReturn(calleeBody.Args[0]);
        return callee;
    }

    /// <summary> Creates a static <c>void Callee()</c> method that does nothing. </summary>
    private MethodDef CreateVoidCallee(MethodBody body, string name = "Callee")
    {
        var callee = CreateCallee(body, name, PrimType.Void);
        new IRBuilder(callee.Body!.CreateBlock()).CreateReturn();
        return callee;
    }

    /// <summary> Creates a static <c>string Callee()</c> method that returns a constant. </summary>
    private MethodDef CreateStringCallee(MethodBody body, string name = "Callee")
    {
        var callee = CreateCallee(body, name, PrimType.String);
        new IRBuilder(callee.Body!.CreateBlock()).CreateReturn(ConstString.Create("value"));
        return callee;
    }

    private static MethodDef CreateCallee(MethodBody body, string name, TypeDesc returnType, params TypeDesc[] paramTypes)
    {
        var callee = body.Definition.DeclaringType.CreateMethod(name, returnType,
            [.. paramTypes.Select((type, i) => new ParamDef(type, "arg" + i))],
            MethodAttributes.Public | MethodAttributes.Static);
        callee.Body = new MethodBody(callee);
        return callee;
    }
}
