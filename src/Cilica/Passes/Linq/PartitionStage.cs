namespace Cilica.Passes.Linq;

using Cilica.IR.Utils;

internal abstract class PartitionStage(CallInst call, LinqStageNode drain) : LinqStageNode(call, drain)
{
    protected bool IsSourceRange { get; private set; }
    public override bool IsFiltering => !IsSourceRange;

    // Array selectors compose with slicing. A filter must evaluate its predicate to find each element.
    private enum SkipBehavior { ArraySlice, Slice, Enumerate, Library }

    public static LinqSourceNode? Plan(LinqSourceNode source, LinqSink sink)
    {
        var behavior = source switch {
            MemorySource when source.PhysicalSource.Operand.ResultType is ArrayType => SkipBehavior.ArraySlice,
            MemorySource or IntRangeSource => SkipBehavior.Slice,
            EnumeratorSource when source.PhysicalSource.Operand is CallInst { Method.Name: "Where" } filter &&
                RuntimeLibrary.IsEnumerableMethod(filter.Method) => SkipBehavior.Enumerate,
            _ => SkipBehavior.Library
        };
        for (var stage = source.Drain; stage != sink; stage = stage.Drain) {
            switch (stage) {
                case PartitionStage range when behavior is SkipBehavior.ArraySlice or SkipBehavior.Slice:
                    range.IsSourceRange = true;
                    break;
                case SelectStage { IsIndexed: false } when behavior == SkipBehavior.ArraySlice:
                    break;
                case WhereStage:
                    behavior = SkipBehavior.Enumerate;
                    break;
                case SkipStage when behavior == SkipBehavior.Library:
                    source = new EnumeratorSource(stage.Drain.SubjectCall.GetOperandRef(0), stage.Drain);
                    break;
                case SkipStage:
                    break;
                default:
                    behavior = SkipBehavior.Library;
                    break;
            }
        }
        // Asking a retained partition for its first element can bypass projections on skipped items.
        return sink is FindSink { HasPredicate: false } && source is EnumeratorSource &&
            source.PhysicalSource.Operand is CallInst { Method.Name: "Skip" or "Take" } partition &&
            RuntimeLibrary.IsEnumerableMethod(partition.Method) ? null : source;
    }

    // For an array or string, Skip(s).Take(t) computes this range in the preheader.
    //   offset = clamp(s, 0, length)
    //   startPtr += offset
    //   length = clamp(t, 0, length - offset)
    // Clamping Skip on both ends avoids GC tracking holes from advancing the managed pointer past the end of the source.
    public static Value? ApplySourceRange(IRBuilder builder, LinqStageNode stage, ref Value length)
    {
        Value? offset = null;
        for (var node = stage; node != null; node = node.Drain) {
            if (node is not PartitionStage { IsSourceRange: true }) continue;
            length = builder.CreateConvert(length, PrimType.Int32);
            var count = builder.CreateMin(length, builder.CreateMax(node.SubjectCall.Args[1], ConstInt.CreateI(0)));
            if (node is SkipStage) {
                offset = offset == null ? count : builder.CreateAdd(offset, count);
                length = builder.CreateSub(length, count);
            } else {
                // A non-positive Take count produces an empty sequence.
                length = count;
            }
        }
        return offset;
    }

    public sealed override void EmitBody(IRBuilder builder, Value currItem, BodyLoopData loopData)
    {
        if (IsSourceRange) Drain.EmitBody(builder, currItem, loopData);
        else EmitStreamingBody(builder, currItem, loopData);
    }
    protected abstract void EmitStreamingBody(IRBuilder builder, Value currItem, BodyLoopData loopData);
}
