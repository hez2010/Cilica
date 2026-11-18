namespace Cilica.Analysis;

/// <summary> Finds natural loops in the CFG. </summary>
public class LoopAnalysis : IMethodAnalysis
{
    public bool IsInvalidatedBy(bool controlFlowChanged) => controlFlowChanged;

    /// <summary> All natural loops, in header order. </summary>
    public List<LoopInfo> Loops { get; } = new();
    // Reducible graphs need only their natural loop headers.
    readonly HashSet<BasicBlock>? _irreducibleRetreatTargets;
    Dictionary<BasicBlock, LoopInfo>? _innermost;

    public IEnumerable<LoopInfo> RootLoops => Loops.Where(loop => loop.Parent == null);

    public LoopAnalysis(MethodBody method, DominatorTree domTree)
    {
        // Based on https://pages.cs.wisc.edu/~fischer/cs701.f14/finding.loops.html
        var worklist = new ArrayStack<BasicBlock>();
        var finish = new Dictionary<BasicBlock, int>();
        method.TraverseDepthFirst(postVisit: block => finish.Add(block, finish.Count));

        foreach (var header in method) {
            if (!finish.TryGetValue(header, out int headerOrder)) {
                continue;
            }

            LoopInfo? loop = null;

            foreach (var latch in header.Preds) {
                // Check if `latch -> header` is actually a back-edge
                if (!finish.TryGetValue(latch, out int latchOrder) || latchOrder > headerOrder) continue;
                if (!domTree.Dominates(header, latch)) {
                    (_irreducibleRetreatTargets ??= new()).Add(header);
                    continue;
                }

                // The loop body includes the header, latch, and all
                // predecessors from the latch up to the header.
                if (loop == null) {
                    loop = new LoopInfo() { Header = header, Blocks = new() };
                    loop.Blocks.Add(header);
                    Loops.Add(loop);
                }

                var body = loop.Blocks;
                worklist.Push(latch);

                while (worklist.TryPop(out var block)) {
                    if (!body.Add(block)) continue;

                    foreach (var pred in block.Preds) {
                        if (finish.ContainsKey(pred)) {
                            worklist.Push(pred);
                        }
                    }
                }
            }
        }

        foreach (var loop in Loops) {
            _irreducibleRetreatTargets?.Add(loop.Header);
            loop.Parent = Loops.Where(parent => parent != loop && parent.NumBlocks > loop.NumBlocks &&
                loop.Blocks.GetEnumerator().AsEnumerable().All(parent.Contains)).MinBy(parent => parent.NumBlocks);

            if (loop.Parent is { } parent) {
                loop._nextSibling = parent._firstChild;
                parent._firstChild = loop;
            }
        }
    }

    // Every cycle has a DFS retreating edge. In a reducible graph each such
    // edge is a natural backedge to the header of its own loop.
    internal bool BodyIsAcyclic(LoopInfo loop)
        => !loop.HasChildren && (_irreducibleRetreatTargets == null ||
            !loop.Blocks.GetEnumerator().Any(b => b != loop.Header && _irreducibleRetreatTargets.Contains(b)));

    /// <summary> Whether a dominated block can execute twice between visits to its dominator. </summary>
    internal bool MayRepeatWithout(BasicBlock block, BasicBlock dominator)
    {
        if (block == dominator) return false;
        if (_irreducibleRetreatTargets != null) return ControlFlowFacts.CanRepeatWithout(block, dominator);
        if (_innermost == null) {
            _innermost = new();
            foreach (var loop in Loops) {
                foreach (var member in loop.Blocks) {
                    if (!_innermost.TryGetValue(member, out var inner) || loop.NumBlocks < inner.NumBlocks) {
                        _innermost[member] = loop;
                    }
                }
            }
        }
        // Re-entering a block in a reducible CFG revisits its innermost header.
        // A dominator inside that loop must execute again on the way to the block.
        return _innermost.TryGetValue(block, out var containing) && !containing.Contains(dominator);
    }

    public IEnumerable<ShapedLoopInfo> GetShapedLoops(bool innermostOnly = false)
        => (innermostOnly ? GetInnermostLoops() : Loops).Select(ShapedLoopInfo.Parse).Where(s => s != null)!;

    public IEnumerable<LoopInfo> GetInnermostLoops()
        => Loops.Where(loop => !loop.HasChildren);

    static IMethodAnalysis IMethodAnalysis.Create(IMethodAnalysisManager mgr)
        => new LoopAnalysis(mgr.Method, mgr.GetAnalysis<DominatorTree>());
}

/// <summary>
/// Represents the structure of a loop in a CFG.
/// See https://llvm.org/docs/LoopTerminology.html
/// </summary>
public class LoopInfo
{
    public required BasicBlock Header { get; init; }
    public required RefSet<BasicBlock> Blocks { get; init; }

    public LoopInfo? Parent { get; set; }
    internal LoopInfo? _firstChild, _nextSibling;

    public bool HasChildren => _firstChild != null;
    public int NumBlocks => Blocks.Count;

    /// <summary> Checks if the specified block is part of this loop. </summary>
    /// <remarks> This includes the header, latch, and any other body block. </remarks>
    public bool Contains(BasicBlock block) => Blocks.Contains(block);

    /// <summary> Checks if the specified value is defined outside the loop. </summary>
    public bool IsInvariant(Value val)
    {
        return !(val is Instruction inst && Contains(inst.Block));
    }

    public BasicBlock? GetPreheader()
    {
        var pred = GetPredecessor();
        return pred?.Last is BranchInst { IsJump: true } ? pred : null;
    }

    /// <summary> Returns the unique block entering the loop (its predecessor). Differently from the pre-header, this block may have multiple successors. </summary>
    public BasicBlock? GetPredecessor() => GetUniquePredAround(Header, inside: false);

    /// <summary> Returns the unique block with a back-edge to the header. </summary>
    public BasicBlock? GetLatch() => GetUniquePredAround(Header, inside: true);

    /// <summary> Returns the unique block that executes after the loop exits. </summary>
    public BasicBlock? GetExit()
    {
        var exit = default(BasicBlock);
        int count = 0;

        foreach (var block in Blocks) {
            foreach (var succ in block.Succs) {
                if (!Contains(succ) && succ != exit) {
                    exit = succ;
                    count++;
                }
            }
        }
        return count == 1 ? exit : null;
    }

    /// <summary> Returns the unique condition controlling the loop exit. </summary>
    public Instruction? GetExitCondition()
    {
        // Header: goto cmp ? Body : Exit
        var exit = GetExit();
        return exit != null && GetUniquePredAround(exit, inside: true) == Header &&
               Header.Last is BranchInst { Cond: Instruction cond } br &&
               br.Else == exit
            ? cond : null;
    }

    public IEnumerable<LoopInfo> GetChildren()
    {
        for (var child = _firstChild; child != null; child = child._nextSibling) {
            yield return child;
        }
    }

    // Returns the unique predecessor of `block` that is either inside or outside the loop.
    private BasicBlock? GetUniquePredAround(BasicBlock block, bool inside)
    {
        var result = default(BasicBlock);
        int count = 0;

        foreach (var pred in block.Preds) {
            if (Contains(pred) == inside) {
                result = pred;
                count++;
            }
        }
        return count == 1 ? result : null;
    }

    public override string ToString()
    {
        // Prehdr^ -> Header[B1 B2 B3 Exiting* Latch↲]
        var sb = new StringBuilder();

        var preheader = GetPreheader();
        var preds = Header.Preds.AsEnumerable().Where(b => !Contains(b));
        sb.AppendSequence(preds, PrintBlock, prefix: "", postfix: " -> ", separator: " ");

        PrintBlock(Header);

        var bodyBlocks = Blocks.GetEnumerator().AsEnumerable().Where(b => b != Header);
        sb.AppendSequence(bodyBlocks, PrintBlock, separator: " ");

        return sb.ToString();

        void PrintBlock(BasicBlock block)
        {
            sb.Append(block);

            if (block == preheader) {
                sb.Append('^');
                return;
            }
            bool isLatch = block.Succs.Contains(Header);
            bool isExiting = block.Succs.Any(b => !Contains(b));

            if (isLatch) sb.Append('↲');
            if (isExiting) sb.Append('*');
        }
    }
}
