namespace Cilica.Passes.Linq;

using Cilica.IR.Utils;

internal static class IRBuilderExt
{
    public static Value CreateLambdaInvoke(this IRBuilder ib, CallInst call, int lambdaIndex, params Value[] args)
    {
        var invoker = call.Method.ParamSig[lambdaIndex].Type.FindMethod("Invoke");
        return ib.CreateCallVirt(invoker, args.Prepend(call.Args[lambdaIndex]).ToArray());
    }
    public static Value CreateLambdaInvoke_ItemAndIndex(this IRBuilder ib, CallInst call, int lambdaIndex, Value currItem, LoopAccumVarFactory createAccum)
    {
        var invoker = call.Method.ParamSig[lambdaIndex].Type.FindMethod("Invoke");
        var lambda = call.Args[lambdaIndex];

        if (invoker.ParamSig.Count == 3) {
            CallInst invocation = null!;
            createAccum(ConstInt.CreateI(-1), previousIndex => {
                var index = ib.CreateBin(BinaryOp.AddOvf, previousIndex, ConstInt.CreateI(1));
                invocation = ib.CreateCallVirt(invoker, lambda, currItem, index);
                return index;
            });
            return invocation;
        }
        return ib.CreateCallVirt(invoker, lambda, currItem);
    }

    public static void ThrowEmptySequence(this IRBuilder ib, bool noMatch, Value? cond = null)
    {
        var helpers = ib.Resolver.Resolve("System.Linq").FindType("System.Linq", "ThrowHelper", throwIfNotFound: true);
        var throwHelper = ib.Method.CreateBlock().SetName("LQ_ThrowHelper");
        new IRBuilder(throwHelper).CreateCall(helpers.FindMethod(noMatch ? "ThrowNoMatchException" : "ThrowNoElementsException"));
        throwHelper.InsertLast(new ThrowInst(ConstNull.Create()));

        if (cond == null) {
            ib.SetBranch(throwHelper);
        } else {
            ib.ForkIf(ib.CreateNe(cond, Const.CreateZero(cond.ResultType)), throwHelper);
        }
    }
}
