namespace Cilica.Passes.Linq;

using Cilica.IR.Utils;
using Cilica.Analysis;

//Represents a sink for a query which is immediately consumed by a loop.
//
//This currently doesn't use the loop analysis but instead relies on a surrounding
//try-finally region to detect the loop.
//
//  RegionEntry:
//    try finally(RegionHandler)
//    goto Header
//  Header:
//    goto enumer.MoveNext() ? Body : Exit  // only two instrs
//  Body:
//    T item = enumer.get_Current()  // first instr
//    ...
//  Latch:
//    goto Header
//  Exit:
//    leave RegionSucc
//  RegionHandler:
//    goto enumer != null ? Dispose : Resume
//  Dispose:
//    callvirt IDisposable::Dispose(this: enumer)
//    goto Resume
//  Resume:
//    resume RegionSucc
//
//If the loop header is the only successor of a guard block, all other blocks
//must be dominated by it. Then we can safely (mostly) isolate and rewire the body
//to the newly generated expanded loop.
internal class LoopSink : LinqSink
{
    BasicBlock _regionEntry, _header, _exit, _body;
    CallInst _getCurrent, _moveNext;
    CallInst? _dispose;
    CompareInst? _nullCheck;
    BasicBlock[] _earlyExits = [];

#pragma warning disable CS8618 // uninitialized non-nullable members
    private LoopSink(CallInst getEnumerCall)
        : base(getEnumerCall) { }
#pragma warning restore CS8618

    public override void EmitBody(IRBuilder builder, Value currItem, BodyLoopData loopData)
    {
        builder.SetBranch(_body);
        _getCurrent.ReplaceWith(currItem);
        
        // Rewire old loop
        GetOrCreateLatch().RedirectSucc(_header, loopData.SkipBlock);
        _regionEntry.RedirectSucc(_header, loopData.SourceLoop.EntryBlock);

        loopData.Exit.SetBranch(_exit);

        // A break can share the normal exit block. Keep those branches inside
        // the new loop's protected region and leave it through its own exit.
        foreach (var pred in _exit.Preds.AsEnumerable().ToArray()) {
            if (pred != _header && pred != loopData.Exit.Block) {
                pred.RedirectSucc(_exit, loopData.GetExitTarget(loopData.Exit.Block));
            }
        }

        if (loopData.Enumeration is { } enumeration) {
            // The original body now sits inside the generated disposal regions.
            // Its break/return leaves still need to exit the original foreach
            // region, after disposing every newly introduced enumerator.
            foreach (var exit in _earlyExits) {
                var target = exit.Succs.First();
                var bridge = exit.Method.CreateBlock(insertAfter: _exit);
                bridge.SetBranch(new LeaveInst(target));
                target.RedirectPhis(exit, bridge);
                exit.SetBranch(enumeration.GetExitTarget(bridge, exitRoot: true));
            }
        }

        // Old header should now be unreachable and can be removed
        Debug.Assert(_header.NumPreds == 0);
        _header.Remove(); 
    }
    protected override void DeleteSubject()
    {
        if (_nullCheck != null) _nullCheck.ReplaceWith(ConstInt.CreateI(_nullCheck.Op == CompareOp.Ne ? 1 : 0));
        _dispose?.Remove();
        base.DeleteSubject();
    }

    public static LoopSink? TryCreate(CallInst getEnumerCall, MethodTransformContext context)
    {
        var sink = new LoopSink(getEnumerCall);
        if (!sink.MatchLoop()) return null;
        var region = context.GetAnalysis<ProtectedRegionAnalysis>().GetBlockRegion(sink._regionEntry);
        sink._earlyExits = region.GetExitBlocks().Where(block => block != sink._exit).ToArray();
        return sink;
    }

    private bool MatchLoop()
    {
        // Uses: itr.MoveNext(), itr.get_Current(), [itr != null; itr.Dispose()]
        if (SubjectCall.IsStatic || SubjectCall.NumArgs != 1 || SubjectCall.NumUses != 4) {
            return false;
        }
        var calls = new Dictionary<string, CallInst>(4);

        // Match uses for GetEnumerator() call
        foreach (var user in SubjectCall.Users()) {
            if (user is CallInst { IsStatic: false, NumArgs: 1 } call) {
                if (!calls.TryAdd(call.Method.Name, call)) {
                    return false;
                }
            } else if (user is CompareInst { Op: CompareOp.Eq or CompareOp.Ne, Right: ConstNull } cmp && _nullCheck == null) {
                _nullCheck = cmp;
            } else {
                return false;
            }
        }

        return
            calls.TryGetValue("MoveNext", out _moveNext!) &&
            calls.TryGetValue("get_Current", out _getCurrent!) &&
            calls.TryGetValue("Dispose", out _dispose) && _dispose.Method.DeclaringType.IsCorelibType(typeof(IDisposable)) &&
            MatchLoopBlocks();
    }
    private bool MatchLoopBlocks()
    {
        _header = _moveNext.Block;
        foreach (var pred in _header.Preds) {
            if (pred.First is GuardInst { Kind: GuardKind.Finally }) {
                if (_regionEntry != null) {
                    return false;
                }
                _regionEntry = pred;
            }
        }
        return _header.NumPreds >= 2 && _regionEntry?.First is GuardInst guard && MatchesCleanup(guard.HandlerBlock) &&
               // Match `Header: goto enumer.MoveNext() ? Body : Exit`
               _header.First == _moveNext && _moveNext.Next == _header.Last && _moveNext.NumUses == 1 &&
               _header.Last is BranchInst br && br.Cond == _moveNext &&
               // Match `Body: T curr = enumer.get_Current()`
               (_body = br.Then).First == _getCurrent &&
               // Match `Exit: leave RegionSucc`
               (_exit = br.Else!).First is LeaveInst;
    }

    private bool MatchesCleanup(BasicBlock handler)
    {
        if (_nullCheck == null || handler.First != _nullCheck ||
            _nullCheck.Next is not BranchInst { IsConditional: true } branch || branch.Cond != _nullCheck) return false;
        var (skip, dispose) = _nullCheck.Op == CompareOp.Eq ? (branch.Then, branch.Else!) : (branch.Else!, branch.Then);
        // The generated enumeration owns disposal. Any other handler code has
        // observable ordering, including when Dispose itself throws.
        return skip.First is ResumeInst && dispose.First == _dispose &&
            (_dispose!.Next is ResumeInst || _dispose.Next is BranchInst { IsJump: true } jump && jump.Then == skip);
    }

    private BasicBlock GetOrCreateLatch()
    {
        var backedges = _header.Preds.AsEnumerable().Where(pred => pred != _regionEntry).ToArray();
        if (backedges is [var only] && only.Last is BranchInst { IsJump: true }) return only;
        // Route conditional or multiple backedges through a common latch. An empty jump block can serve as that latch.
        var latch = backedges.FirstOrDefault(pred => pred.First is BranchInst { IsJump: true });
        if (latch == null) {
            latch = _header.Method.CreateBlock(insertAfter: backedges[0]);
            latch.SetBranch(_header);
        }
        foreach (var pred in backedges) {
            if (pred != latch) pred.RedirectSucc(_header, latch);
        }
        return latch;
    }
}
