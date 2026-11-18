namespace Cilica.Analysis;

/// <summary> Tracks writes to a span's reference and length, independently of writes to its elements. </summary>
internal sealed class SpanStorageAnalysis(IMethodAnalysisManager manager) : IMethodAnalysis
{
    MemoryAccessAnalysis? _memory;
    MemoryAccessAnalysis Memory => _memory ??= manager.GetAnalysis<MemoryAccessAnalysis>();
    readonly DominatorTree _dominators = manager.GetAnalysis<DominatorTree>();
    readonly Dictionary<LocalSlot, bool> _confined = new();

    static IMethodAnalysis IMethodAnalysis.Create(IMethodAnalysisManager manager) => new SpanStorageAnalysis(manager);

    public bool CanHoistLength(CallInst length, CountingLoopInfo loop)
    {
        var receiver = length.Args[0];
        bool Clobbers(Instruction inst) => MayWrite(receiver, inst);

        if (length.Block == loop.Header) {
            // Taking a possibly null byref to the preheader must preserve the original fault order and handler.
            if (receiver is not LocalSlot && (loop.Header.HasGuards ||
                loop.Header.NonPhis().TakeWhile(inst => inst != length).Any(inst => inst.HasSideEffects))) return false;
        } else if (!Memory.IsUnchangedBetween(length, loop.PreHeader, null, Clobbers)) {
            return false;
        }
        return loop.Blocks.GetEnumerator().All(block => !Memory.HasWrite(block, Clobbers));
    }

    public bool DescribeSameView(CallInst pointer, CallInst length)
        => pointer.Args[0] == length.Args[0] && pointer.Method.DeclaringType == length.Method.DeclaringType &&
           Memory.IsUnchangedBetween(pointer, length, _dominators, write => MayWrite(pointer.Args[0], write));

    private bool MayWrite(Value receiver, Instruction inst)
    {
        if (inst is CallInst call && RuntimeLibrary.IsSpanAccessor(call.Method)) return false;
        // A byref parameter or an escaped local can be reassigned through another alias.
        if (receiver is not LocalSlot slot || inst is GuardInst) return true;
        if (!_confined.TryGetValue(slot, out bool confined)) {
            confined = slot.Uses().All(use => use.OperIndex == 0 && use.Parent switch {
                MemoryInst => true,
                CallInst accessor => RuntimeLibrary.IsSpanAccessor(accessor.Method),
                FieldAddrInst field => field.Users().All(user => user is LoadInst),
                _ => false
            });
            _confined.Add(slot, confined);
        }
        return !confined || inst is StoreInst store && store.Address == slot;
    }
}
