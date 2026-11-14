namespace Cilica.Passes;

using Cilica.Analysis;

/// <summary>
/// Marks calls whose result is returned unchanged as tail calls, allowing the callee to reuse the caller's stack frame.
/// </summary>
/// <remarks>
/// Only calls that satisfy the restrictions ECMA-335 (III.2.4) places on the <c>tail.</c> prefix are converted: the call must be
/// the final operation before a return of its own result, it must not sit inside a protected region, and the callee's return
/// type must match the caller's exactly so that no conversion is required between the two frames.
/// Calls with a <c>constrained.</c> prefix, P/Invoke, unmanaged and vararg calls, and methods that allocate on the stack or are
/// synchronized are left alone, because discarding the caller's frame would change their semantics.
/// </remarks>
public class TailCallOptimization : IMethodPass
{
    public MethodPassResult Run(MethodTransformContext ctx)
    {
        var method = ctx.Method;
        if (!IsOptimizableMethod(ctx)) return MethodInvalidations.None;

        var regions = ctx.GetAnalysis<ProtectedRegionAnalysis>();
        long revision = method.Revision;
        bool controlFlowChanged = false;

        foreach (var block in method) {
            // A `tail.` prefix is not permitted inside a try block, a filter, or a handler.
            if (regions.GetBlockRegion(block) != regions.Root) continue;
            controlFlowChanged |= TryOptimizeBlock(ctx, block);
        }

        if (method.Revision == revision) return MethodInvalidations.None;
        if (controlFlowChanged) DeadCodeElim.RemoveUnreachableBlocks(method);
        return controlFlowChanged ? MethodInvalidations.ControlFlow : MethodInvalidations.DataFlow;
    }

    /// <summary> Rejects methods where discarding the current frame would change observable behavior. </summary>
    private static bool IsOptimizableMethod(MethodTransformContext ctx)
    {
        // A synchronized method would release its monitor before the callee finishes.
        if (ctx.Definition.ImplAttribs.HasFlag(System.Reflection.MethodImplAttributes.Synchronized)) return false;
        // A stack allocation lives in exactly the frame a tail call discards.
        return !ctx.Method.Instructions().Any(inst => inst is CilIntrinsic.Alloca);
    }

    /// <summary> Returns true if the block's control flow was restructured. </summary>
    private static bool TryOptimizeBlock(MethodTransformContext ctx, BasicBlock block)
    {
        // The call and its return live in the same block: `call; ret`.
        if (block.Last is ReturnInst ret) {
            if (block.LastNonBranch is not { } call) return false;
            if (!CanBeTailCall(call, ctx.Method) || !ReturnsValue(ret, call)) return false;

            SetTailCall(call);
            ctx.Logger.Trace($"Converted '{call}' in '{block}' to a tail call.");
            return false;
        }

        // `SimplifyCFG` sank the return into a shared exit block, so the call is followed by a jump to it: `call; goto exit`.
        if (block.Last is BranchInst { IsJump: true } jump) {
            if (block.LastNonBranch is not { } call) return false;
            if (!CanBeTailCall(call, ctx.Method)) return false;
            if (!IsReturnOnlyBlock(jump.Then, out var exit)) return false;
            if (!ReturnsThroughExit(exit, jump.Then, block, call)) return false;

            // Hoist the return into this block and drop the edge to the now-redundant exit block.
            SetTailCall(call);
            block.SetBranch(CreateReturn(call));
            jump.Then.RedirectPhis(block, null, removeTrivialPhis: true);
            ctx.Logger.Trace($"Converted '{call}' in '{block}' to a tail call, returning through '{jump.Then}'.");
            return true;
        }

        return false;
    }

    /// <summary> Checks that <paramref name="inst"/> is a call which may carry a <c>tail.</c> prefix. </summary>
    private static bool CanBeTailCall(Instruction inst, MethodBody method)
    {
        if (inst is CallInst call) {
            if (call.IsTailCall) return false;
            // A constrained call resolves the target against a type that need not be the receiver's.
            if (call.Constraint != null) return false;
            if (!IsManagedCall(call.Method)) return false;
            return IsTailCallReturnType(call.Method.ReturnType, method.ReturnType);
        }
        if (inst is CilIntrinsic.CallIndirect calli) {
            if (calli.IsTailCall) return false;
            var sig = calli.FunctionType.Signature;
            if (sig.CallConv != CallConvention.Managed) return false;
            return IsTailCallReturnType(sig.ReturnType.Type, method.ReturnType);
        }
        return false;
    }

    /// <summary> Checks that the callee runs as managed code and accepts the caller's frame. </summary>
    private static bool IsManagedCall(MethodDesc method)
    {
        // A P/Invoke transitions into unmanaged code, which cannot return into a discarded frame.
        if (method is MethodDefOrSpec entity && entity.Definition.ImportInfo != null) return false;
        return method.CallConv == CallConvention.Managed;
    }

    /// <summary> Checks that the callee's return value can be handed to the caller's caller without conversion. </summary>
    private static bool IsTailCallReturnType(TypeDesc calleeReturn, TypeDesc callerReturn)
    {
        if (callerReturn != calleeReturn) return false;
        // A byref or typed reference can point into the frame that the tail call discards.
        return callerReturn.Kind is not (TypeKind.ByRef or TypeKind.TypedRef);
    }

    private static bool ReturnsValue(ReturnInst ret, Instruction call)
        => ret.HasValue ? ret.Value == call : call.ResultType == PrimType.Void;

    /// <summary> Checks that <paramref name="exit"/> returns the value produced by <paramref name="call"/> in <paramref name="block"/>. </summary>
    private static bool ReturnsThroughExit(ReturnInst exit, BasicBlock exitBlock, BasicBlock block, Instruction call)
    {
        if (!exit.HasValue) return call.ResultType == PrimType.Void;

        var value = exit.Value!;
        if (value is PhiInst phi && phi.Block == exitBlock) {
            for (int i = 0; i < phi.NumArgs; i++) {
                if (phi.GetBlock(i) == block) return phi.GetValue(i) == call;
            }
            return false;
        }
        // Without a phi the exit block must be reachable from this block alone for the value to be valid there.
        return value == call && exitBlock.NumPreds == 1;
    }

    /// <summary> Checks that <paramref name="block"/> does nothing but return, possibly through phi nodes. </summary>
    private static bool IsReturnOnlyBlock(BasicBlock block, out ReturnInst exit)
    {
        exit = null!;
        if (block.Last is not ReturnInst ret) return false;

        for (var inst = block.First; inst != null && inst != ret; inst = inst.Next) {
            if (inst is not PhiInst) return false;
        }
        exit = ret;
        return true;
    }

    private static ReturnInst CreateReturn(Instruction call)
        => new(call.ResultType == PrimType.Void ? null : call);

    private static void SetTailCall(Instruction call)
    {
        if (call is CallInst direct) direct.IsTailCall = true;
        else if (call is CilIntrinsic.CallIndirect indirect) indirect.IsTailCall = true;
    }
}
