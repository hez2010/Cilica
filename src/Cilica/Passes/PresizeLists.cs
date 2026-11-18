namespace Cilica.Passes;

using Cilica.Analysis;
using Cilica.AsmIO;
using Cilica.IR.Utils;

public class PresizeLists : IMethodPass
{
    public MethodPassResult Run(MethodTransformContext ctx)
    {
        var loopAnalysis = ctx.GetAnalysis<LoopAnalysis>();
        var domTree = ctx.GetAnalysis<DominatorTree>();
        var regions = ctx.GetAnalysis<ProtectedRegionAnalysis>();

        var candidateLists = new Dictionary<NewObjInst, (bool HasConditionalAdd, int AddCallCount)>();
        var useDominators = new Dictionary<NewObjInst, BasicBlock?>();
        int numChanges = 0;

        foreach (var loop in loopAnalysis.GetShapedLoops(innermostOnly: true)) {
            // Must be able to calculate loop trip count before entering it
            if (!loop.HasKnownTripCount(domTree, loop.PreHeader.Last)) continue;
            bool? bodyIsAcyclic = null;

            // Find Add() calls inside loop
            foreach (var block in loop.Blocks) {
                foreach (var inst in block) {
                    if (!(inst is CallInst call && IsListAdd(call.Method))) continue;

                    // The list must be allocated outside the loop it is being presized for.
                    if (call.Args is not [NewObjInst { NumArgs: 0 } list, ..] || !loop.IsInvariant(list)) continue;

                    ref var info = ref candidateLists.GetOrAddRef(list);

                    // Each completed iteration executes a dominating Add at least once.
                    // Estimating conditional appends would require a frequency for that path.
                    if (block != loop.Header && domTree.Dominates(block, loop.Latch)) {
                        info.AddCallCount++;
                    } else {
                        info.HasConditionalAdd = true;
                    }
                }
            }

            // Pre-size lists
            foreach (var (listAlloc, info) in candidateLists) {
                if (info.AddCallCount == 0 || !IsListMethod(listAlloc.Constructor)) continue;
                if (!useDominators.TryGetValue(listAlloc, out var uses)) {
                    uses = FindConfinedUseDominator(listAlloc, domTree);
                    useDominators.Add(listAlloc, uses);
                }
                // Every use must follow the normal edge from the preheader into the loop.
                if (uses == null || !domTree.Dominates(loop.Header, uses) ||
                    !CanSinkAlloc(listAlloc, loop.PreHeader, domTree, loopAnalysis, regions)) {
                    continue;
                }

                var builder = new IRBuilder(loop.PreHeader);
                var (capacity, exact) = GetCapacity(loop, info.AddCallCount, builder);
                var ctorWithCap = listAlloc.ResultType.FindMethod(".ctor", new MethodSig(PrimType.Void, [PrimType.Int32]));
                var newList = builder.CreateNewObj(ctorWithCap, capacity);
                ctx.Logger.Trace($"Presized '{listAlloc}' in '{listAlloc.Block}' to capacity '{capacity}' for loop '{loop.Header}'.");
                listAlloc.ReplaceWith(newList);

                // Direct array writes require exactly one append per counted call on every iteration.
                if (!info.HasConditionalAdd && exact && (bodyIsAcyclic ??= loopAnalysis.BodyIsAcyclic(loop.Loop))) {
                    TryReplaceArrayBuilder(loop, builder, newList, domTree, loopAnalysis, ctx.Logger);
                }

                numChanges++;
            }

            candidateLists.Clear();
        }

        return numChanges > 0 ? MethodInvalidations.DataFlow : MethodInvalidations.None;
    }

    private static (Value Capacity, bool Exact) GetCapacity(ShapedLoopInfo loop, int addsPerIteration, IRBuilder builder)
    {
        int limit = int.MaxValue / addsPerIteration;
        var trips = builder.CreateConvert(loop.GetTripCount(builder)!, PrimType.Int32);
        var capacity = builder.CreateMul(trips, ConstInt.CreateI(addsPerIteration));
        if (loop.MaxTripCount <= limit) return (capacity, true);
        // A count beyond the allocation limit leaves growth to List.Add.
        return (builder.CreateSelect(builder.CreateUle(trips, ConstInt.CreateI(limit)), capacity, ConstInt.CreateI(0)), false);
    }

    private static BasicBlock? FindConfinedUseDominator(NewObjInst list, DominatorTree dominators)
    {
        BasicBlock? common = null;
        foreach (var (user, operand) in list.Uses()) {
            if (operand != 0 || user is not CallInst call || !IsListMethod(call.Method) ||
                call.Method.Name is not ("Add" or "ToArray" or "get_Count" or "get_Item")) return null;
            common ??= user.Block;
            while (!dominators.Dominates(common, user.Block)) common = dominators.IDom(common);
        }
        return common;
    }

    // A confined list construction can write directly to its result array.
    // Observable lists retain Add's versioning and incremental mutation semantics.
    //   PreHeader: list = newobj List`1[T]::.ctor(int: capacity)
    //   Body:      call List`1[T]::Add(this: list, T: value)
    //   AfterLoop: result = call List`1[T]::ToArray(this: list)
    // Every use inside the loop must be an Add. The single ToArray must execute at most once per allocation.
    private static bool TryReplaceArrayBuilder(ShapedLoopInfo loop, IRBuilder builder, NewObjInst list, DominatorTree domTree,
        LoopAnalysis loops, ICompilationLogger logger)
    {
        var addCalls = new List<CallInst>();
        CallInst? toArrayCall = null;
        foreach (CallInst call in list.Users()) {
            if (loop.Contains(call.Block) && call.Method.Name == "Add") {
                addCalls.Add(call);
            } else if (!loop.Contains(call.Block) && call.Method.Name == "ToArray" && toArrayCall == null) {
                toArrayCall = call;
            } else {
                return false;
            }
        }

        if (toArrayCall == null || !domTree.Dominates(loop.Exit, toArrayCall.Block) ||
            loops.MayRepeatWithout(toArrayCall.Block, builder.Block)) return false;

        var elemType = list.ResultType.GenericParams[0];
        var array = builder.CreateNewArray(elemType, list.Args[0]).SetName("arrbuilder_items");
        logger.Trace($"Replaced '{toArrayCall}' in '{toArrayCall.Block}' with direct array construction '{array}' for loop '{loop.Header}'.");
        toArrayCall.ReplaceWith(array);
        list.Remove();

        // Canonical counting loops use their induction variable as the destination index.
        if (addCalls.Count == 1 && loop is CountingLoopInfo { IsCanonical: true } forLoop) {
            var basePtr = LoopStrengthReduction.CreateGetDataPtrRange(builder, array, getCount: false).BasePtr;

            var call = addCalls[0];
            builder.SetPosition(call, InsertionDir.After);
            var ptr = builder.CreatePtrOffset(basePtr, forLoop.Counter);
            builder.CreateStore(ptr, call.Args[1]);
            call.Remove();
        } else {
            var idxVar = builder.Method.CreateVar(PrimType.Int32, "arrbuilder_idx");
            builder.CreateStore(idxVar, ConstInt.CreateI(0));

            foreach (var call in addCalls) {
                builder.SetPosition(call, InsertionDir.After);

                builder.CreateArrayStore(array, builder.CreateLoad(idxVar), call.Args[1]); // array[idx] = value
                builder.CreateStore(idxVar, builder.CreateAdd(builder.CreateLoad(idxVar), ConstInt.CreateI(1))); // idx++
                call.Remove();
            }
        }
        return true;
    }

    // Sinking the allocation into the preheader must preserve its lifetime across visits to the loop.
    private static bool CanSinkAlloc(NewObjInst alloc, BasicBlock preheader, DominatorTree domTree,
        LoopAnalysis loops, ProtectedRegionAnalysis regions)
    {
        if (!domTree.Dominates(alloc.Block, preheader) || loops.MayRepeatWithout(preheader, alloc.Block)) return false;
        var sourceRegion = regions.GetBlockRegion(alloc.Block);
        for (var region = regions.GetBlockRegion(preheader); region != sourceRegion; region = region.Parent) {
            if (region == null || region.GetHandlerGuard() != null ||
                region.StartBlock.Guards().Any(g => g.Kind != GuardKind.Finally)) return false;
        }
        return true;
    }

    private static bool IsListAdd(MethodDesc method)
    {
        // TODO: support for ImmutableArray builders
        return method.Name == "Add" && IsListMethod(method);
    }
    private static bool IsListMethod(MethodDesc method)
    {
        return method.DeclaringType.IsCorelibType(typeof(List<>));
    }
}
