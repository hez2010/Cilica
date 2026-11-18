namespace Cilica.Analysis;

internal static class ControlFlowFacts
{
    public static bool CanRepeatWithout(BasicBlock block, BasicBlock preceding)
    {
        if (block == preceding) return false;
        var pending = new DiscreteStack<BasicBlock>(block);
        while (pending.TryPop(out var current)) {
            foreach (var pred in current.Preds) {
                if (pred == block) return true;
                if (pred != preceding) pending.Push(pred);
            }
        }
        return false;
    }

    public static bool HasCycle(MethodBody method)
    {
        var ancestors = new HashSet<BasicBlock>();
        bool cyclic = false;
        method.TraverseDepthFirst(preVisit: block => {
            ancestors.Add(block);
            cyclic |= block.Succs.Any(ancestors.Contains);
        }, postVisit: block => ancestors.Remove(block));

        return cyclic;
    }
}
