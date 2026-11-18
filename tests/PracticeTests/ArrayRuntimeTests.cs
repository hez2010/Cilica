namespace Cilica.PracticeTests;

using Cilica.Attributes;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

[Optimize]
public class ArrayRuntimeTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(17)]
    public void StoredInductionValuesRemainSequential(int count)
    {
        Assert.Equal(Enumerable.Range(1, count), CreateSequence(count));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static int[] CreateSequence(int count)
    {
        var values = new int[count];
        for (int i = 0; i < values.Length; i++) values[i] = i + 1;
        return values;
    }

    interface IOptional { }

    sealed class DynamicReceiver : IDynamicInterfaceCastable
    {
        public int Tests;
        public bool IsInterfaceImplemented(RuntimeTypeHandle type, bool throwIfNotImplemented) => ++Tests == 1;
        public RuntimeTypeHandle GetInterfaceImplementation(RuntimeTypeHandle type) => throw new NotSupportedException();
    }

    [Fact]
    public void DynamicInterfaceTestsRunEachTime()
    {
        var value = new DynamicReceiver();
        Assert.Equal(10, TestTwice(value));
        Assert.Equal(2, value.Tests);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static int TestTwice(object value)
    {
        int first = value is IOptional ? 10 : 0;
        return first + (value is IOptional ? 1 : 0);
    }

    [Fact]
    public void VectorLanesRetainScalarSignednessAndBooleanValues()
    {
        var signed = Enumerable.Repeat((sbyte)-1, 33).ToArray();
        var unsigned = Enumerable.Repeat((byte)255, 33).ToArray();
        Assert.Equal(0, CountEqual(signed, unsigned));

        var floats = new float[33];
        FillUnsigned(floats);
        Assert.Equal((float)uint.MaxValue, floats[0]);
        Assert.Equal(31f, floats[^1]);

        var values = Enumerable.Repeat(-1, 33).ToArray();
        FillConditional(values);
        Assert.All(values, x => Assert.Equal(1, x));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static int CountEqual(sbyte[] a, byte[] b)
    {
        int count = 0;
        for (int i = 0; i < a.Length; i++) count += a[i] == b[i] ? 1 : 0;
        return count;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void FillUnsigned(float[] array)
    {
        for (int i = 0; i < array.Length; i++) array[i] = (uint)(i - 1);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void FillConditional(int[] array)
    {
        for (int i = 0; i < array.Length; i++) {
            int x = array[i];
            array[i] = x < 0 ? (x == -1 ? 1 : 0) : 42;
        }
    }

    [Fact]
    public void GenericArrayStoresAreVisibleThroughConcreteAliases()
    {
        int[] values = [10];
        Assert.Equal(7, DifferenceAfterStore(values, values, 17));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static int DifferenceAfterStore<T>(int[] before, T[] after, T value)
    {
        int first = before[0];
        after[0] = value;
        return before[0] - first;
    }

    [Fact]
    public void ReassignedSpanIsReadOnTheNextIteration()
    {
        Span<int> span = new int[] { 1, 2, 3 };
        Assert.Equal(12, SumReplacingSpan(ref span, ref span, new int[] { 4, 5, 6 }));
    }

    [Theory, CheckCodeGenAfterRun]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(35)]
    public void SlicedSpansVectorizeAfterAnEarlyReturn(int start)
    {
        if (start < 0) return;
        int[] values = Enumerable.Range(1, 35).ToArray();
        Span<int> slice = values.AsSpan(start);
        int sum = 0;
        for (int i = 0; i < slice.Length; i++) sum += slice[i];
        Assert.Equal((35 * 36 - start * (start + 1)) / 2, sum);
        // CHECK: Vector256::Sum
    }

    [Fact]
    public void SavedSpanLengthSurvivesReassignmentBeforeTheLoop()
    {
        Span<int> first = new[] { 10, 20 };
        Assert.Equal(3, SumPrefix(first, new[] { 1, 2, 3, 4 }));
        Assert.Throws<IndexOutOfRangeException>(() => SumPrefix(new[] { 1, 2, 3, 4 }, new[] { 1, 2 }));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static int SumPrefix(Span<int> view, Span<int> replacement)
    {
        int length = view.Length;
        view = replacement;
        int sum = 0;
        for (int i = 0; i < length; i++) sum += view[i];
        return sum;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static int SumReplacingSpan(ref Span<int> span, ref Span<int> alias, Span<int> replacement)
    {
        int sum = 0;
        for (int i = 0; i < span.Length; i++) {
            sum += span[i];
            alias = replacement;
        }
        return sum;
    }

    [Fact]
    public void WritableArrayReferencesCheckTheRuntimeElementType()
    {
        object[] values = new string[] { "first" };
        values[0] = "second";
        Assert.Equal("second", values[0]);
        Assert.Throws<ArrayTypeMismatchException>(() => ReadWritable(values));
        Assert.Throws<ArrayTypeMismatchException>(() => WriteWritable(values));
        int calls = 0;
        Assert.Throws<ArrayTypeMismatchException>(() => WriteAfterEvaluation(values, () => { calls++; return "third"; }));
        Assert.Equal(1, calls);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void WriteAfterEvaluation(object[] values, Func<object> getValue)
    {
        object value = getValue();
        ref object element = ref values[0];
        element = value;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static object ReadWritable(object[] values)
    {
        ref object element = ref values[0];
        return element;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void WriteWritable(object[] values)
    {
        ref object element = ref values[0];
        element = "third";
    }

    [Fact]
    public void CovariantArrayReadsRemainValidAcrossMutation()
    {
        object[] values = new string[] { "first" };
        var result = ReadAroundMutation(values, () => values[0] = "second");

        Assert.Equal("first", result.Before);
        Assert.Equal("second", result.After);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static (object Before, object After) ReadAroundMutation(object[] values, Action mutate)
    {
        var before = values[0];
        mutate();
        return (before, values[0]);
    }
}
