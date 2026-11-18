namespace Cilica.Analysis;

/// <summary> Indexes potential writes between memory accesses and calls in a snapshot of a method. </summary>
/// <remarks> Removal-only traversals may reuse the snapshot while ignoring detached instructions. </remarks>
public sealed class MemoryAccessAnalysis : IMethodAnalysis
{
    readonly Dictionary<BasicBlock, Instruction[]> _writes = new();
    readonly Dictionary<Instruction, (int Before, int After)> _positions = new();

    public MemoryAccessAnalysis(MethodBody method)
    {
        foreach (var block in method) {
            List<Instruction>? writes = null;

            foreach (var instruction in block) {
                int before = writes?.Count ?? 0;

                // Exceptional edges leave from guards rather than from every instruction
                // in the protected region, including nested handlers that can mutate memory.
                if (instruction is GuardInst || instruction.MayWriteToMemory || instruction.IsMemoryBarrier) {
                    (writes ??= new()).Add(instruction);
                }

                if (instruction is MemoryInst or CallInst) {
                    _positions.Add(instruction, (before, writes?.Count ?? 0));
                }
            }

            _writes.Add(block, writes?.ToArray() ?? []);
        }
    }

    static IMethodAnalysis IMethodAnalysis.Create(IMethodAnalysisManager manager) => new MemoryAccessAnalysis(manager.Method);

    /// <summary> Gets potential writes strictly after and before the given memory accesses or calls. Null boundaries include the whole block. </summary>
    public ReadOnlySpan<Instruction> GetPotentialWrites(BasicBlock block, Instruction? after = null, Instruction? before = null)
    {
        var writes = _writes[block];
        int start = after == null ? 0 : _positions[after].After;
        int end = before == null ? writes.Length : _positions[before].Before;

        return writes.AsSpan(start, end - start);
    }

    internal bool HasWrite(BasicBlock block, Func<Instruction, bool> clobbers, Instruction? after = null, Instruction? before = null)
    {
        foreach (var write in GetPotentialWrites(block, after, before)) {
            if (write.Block != null && clobbers(write)) return true;
        }
        return false;
    }

    // The definition dominates the use. Revisiting the use block through a backedge includes all its writes.
    internal bool IsUnchangedBetween(Instruction def, BasicBlock useBlock, Instruction? use, Func<Instruction, bool> clobbers)
    {
        if (def.Block == useBlock) return !HasWrite(useBlock, clobbers, def, use);
        if (HasWrite(useBlock, clobbers, before: use)) return false;

        var pending = new DiscreteStack<BasicBlock>();
        foreach (var pred in useBlock.Preds) pending.Push(pred);
        while (pending.TryPop(out var block)) {
            if (HasWrite(block, clobbers, after: block == def.Block ? def : null)) return false;
            if (block != def.Block) {
                foreach (var pred in block.Preds) pending.Push(pred);
            }
        }
        return true;
    }

    // Two distinct reads that dominate a common use are ordered along its dominator chain.
    internal bool IsUnchangedBetween(Instruction first, Instruction second, DominatorTree dominators, Func<Instruction, bool> clobbers)
    {
        bool inOrder = first.Block == second.Block
            ? _positions[first].After <= _positions[second].Before
            : dominators.Dominates(first.Block, second.Block);
        if (!inOrder) (first, second) = (second, first);
        return IsUnchangedBetween(first, second.Block, second, clobbers);
    }
}
