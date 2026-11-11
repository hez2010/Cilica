namespace Cilica.Analysis;

/// <summary> Instruction positions before a transformation that preserves their execution order. </summary>
internal sealed class InstructionOrder
{
    readonly Dictionary<Instruction, (BasicBlock Block, int Index)> _positions = new();

    public InstructionOrder(MethodBody method)
    {
        int index = 0;
        foreach (var instruction in method.Instructions()) _positions.Add(instruction, (instruction.Block, index++));
    }

    public bool PrecedesInBlock(Instruction first, Instruction second)
    {
        var a = _positions[first];
        var b = _positions[second];
        return a.Block == b.Block && a.Index < b.Index;
    }

    public int Index(Instruction instruction) => _positions[instruction].Index;
}
