namespace Cilica.PracticeTests;

using System.Runtime.CompilerServices;

using Cilica.Attributes;

[Optimize, CheckCodeGenAfterRun]
public class PresizeListsTests
{
    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(5)]
    public void ArrayBuildersWriteEmptyAndNonemptyResultsDirectly(int count)
    {
        var list = new List<int>();
        for (int i = 0; i < count; i++) list.Add(i);
        var result = list.ToArray();
        Assert.Equal(Math.Max(0, count), result.Length);
        for (int i = 0; i < result.Length; i++) Assert.Equal(i, result[i]);
        // CHECK: CIL::NewArray
        // CHECK-NOT: ::ToArray(
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void DerivedListKeepsItsConstructor(int count)
    {
        var list = new SeededList<int>();
        for (int i = 0; i < count; i++) list.Add(i + 1);
        Assert.Equal(Enumerable.Range(0, count + 1), list.ToArray());
        // CHECK: SeededList`1[int]::.ctor()
    }

    sealed class SeededList<T> : List<T>
    {
        public SeededList() => Add(default!);
    }

    [Theory]
    [InlineData((ushort)0)]
    [InlineData((ushort)35)]
    public void SmallUnsignedCountsBuildArraysDirectly(ushort count)
    {
        var list = new List<int>();
        for (uint i = 0; i < count; i++) list.Add((int)i);
        Assert.Equal(Enumerable.Range(0, count), list.ToArray());
        // CHECK: CIL::NewArray
        // CHECK-NOT: ::ToArray(
    }

    [Theory]
    [InlineData(uint.MaxValue)]
    public void LargeUnsignedCountsReachTheFirstAppend(uint count)
    {
        bool reachedAppend = false;
        try {
            var list = new List<int>();
            for (uint i = 0; i < count; i++) list.Add(Produce());
            GC.KeepAlive(list.ToArray());
        } catch (InvalidOperationException) {
            reachedAppend = true;
        }
        Assert.True(reachedAppend);
        // CHECK: newobj List`1[int]::.ctor(int:

        [MethodImpl(MethodImplOptions.NoInlining)]
        static int Produce() => throw new InvalidOperationException();
    }

    [Fact]
    public void NestedLoopsKeepTheirListAndEachSnapshotOwnsItsElements()
    {
        var items = new List<int>();
        int total = 0;
        for (int j = 0; j < 3; j++) {
            int count = 2 + j;
            for (int i = 0; i < count; i++) items.Add(i);
            total += items.Count;
        }
        Assert.Equal(16, total);

        var source = new List<int>();
        for (int i = 0; i < 4; i++) source.Add(7);
        for (int j = 0; j < 3; j++) {
            var snapshot = source.ToArray();
            Assert.Equal(7, snapshot[0]);
            snapshot[0] = 0;
        }
        // CHECK: ToArray(
    }

    [Fact]
    public void EnumeratorFactoriesCanReturnAPrefixOfAnArray()
    {
        var list = new List<int>();
        var iterator = GetEnumerator(new[] { 7, 8, 9 });
        while (iterator.MoveNext()) list.Add(iterator.Current);
        Assert.Equal(new[] { 7 }, list.ToArray());
        // CHECK: ToArray(
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static IEnumerator<int> GetEnumerator(int[] values) => values.Take(1).GetEnumerator();

    [Fact]
    public void EnumerationDoesNotReadUserCountGetters()
    {
        IReadOnlyCollection<int> source = new ThrowingCountCollection();
        var list = new List<int>();

        foreach (int value in source) {
            list.Add(value);
        }

        Assert.Equal(new[] { 1, 2, 3 }, list.ToArray());
        // CHECK-NOT: get_Count
        // CHECK: Add(
        // CHECK: ToArray(
    }

    [Fact]
    public void ObservableCapacityRetainsIncrementalGrowth()
    {
        var list = new List<int>();
        var capacities = new List<int>();

        for (int i = 0; i < 6; i++) {
            capacities.Add(list.Capacity);
            list.Add(i);
        }

        Assert.Equal(new[] { 0, 4, 4, 4, 4, 8 }, capacities);
        Assert.Equal(8, list.Capacity);
        // CHECK: List`1[int]::.ctor()
        // CHECK: Add(
    }

    [DoNotOptimize]
    sealed class ThrowingCountCollection : IReadOnlyCollection<int>
    {
        public int Count => throw new InvalidOperationException();

        public IEnumerator<int> GetEnumerator() => ((IEnumerable<int>)new[] { 1, 2, 3 }).GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(3)]
    public void BuildArray_MultipleAppendsPreserveOrder(int count)
    {
        var list = new List<int>();
        for (int i = 0; i < count; i++) { list.Add(i); list.Add(i + 100); }
        var actual = list.ToArray();
        Assert.Equal(count > 0 ? new[] { 0, 100, 1, 101, 2, 102 } : Array.Empty<int>(), actual);
        // CHECK: select
        // CHECK: Add(
        // CHECK: Add(
        // CHECK: ToArray(
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void AppendExceptionsExposeOnlyCompletedItems(int failAt)
    {
        var list = new List<int>();
        try {
            for (int i = 0; i < 3; i++) list.Add(Produce(i, failAt));
        } catch (InvalidOperationException) { }
        Assert.Equal(failAt, list.Count);
        for (int i = 0; i < list.Count; i++) Assert.Equal(i, list[i]);
        // CHECK: newobj List`1[int]::.ctor()
        // CHECK: try catch
        // CHECK: Add(
    }

    [Fact]
    public void AppendsInvalidateExistingEnumerators()
    {
        var list = new List<int>();
        var iterator = list.GetEnumerator();
        for (int i = 0; i < 3; i++) list.Add(i);
        bool threw = false;
        try { iterator.MoveNext(); }
        catch (InvalidOperationException) { threw = true; }
        Assert.True(threw);
        // CHECK: Add(
    }

    [DoNotOptimize, MethodImpl(MethodImplOptions.NoInlining)]
    private static int Produce(int value, int failAt)
        => value == failAt ? throw new InvalidOperationException() : value;

    [Fact]
    public void BuildArray_ForLoop()
    {
        var list = new List<string>();

        for (int i = 0; i < 32; i++) {
            int j = i + 1;
            string s = (j % 3, j % 5) switch {
                (0, 0) => "FizzBuzz",
                (0, _) => "Fizz",
                (_, 0) => "Buzz",
                _ => string.Format("{0}", j) // box to prevent i from being addr taken
            };
            list.Add(s);
        }
        var array = list.ToArray(); // last use

        Assert.Equal(32, array.Length);
        Assert.Equal("1", array[0]);
        Assert.Equal("2", array[1]);
        Assert.Equal("Fizz", array[2]);
        Assert.Equal("4", array[3]);
        Assert.Equal("Buzz", array[4]);
        Assert.Equal("FizzBuzz", array[14]);

        // CHECK: CIL::NewArray
        // CHECK: GetArrayDataReference
        // CHECK-NOT: Add(
        // CHECK-NOT: ToArray(
    }

    [Fact]
    public void BuildArray_EnumerableLoop()
    {
        var list = new List<string>();
        var source = CreateList().AsReadOnly();
        int index = 0;

        foreach (string str in source) {
            list.Add(index + ": " + str);
            index++;
        }
        var array = list.ToArray(); // last use

        Assert.Equal(source.Count, array.Length);
        Assert.Equal("0: lorem", array[0]);
        Assert.Equal("4: amet", array[4]);

        // CHECK: Add(
        // CHECK: ToArray(
    }

    [Fact]
    public void BuildArray_EnumerableLoop_CondAdd()
    {
        var list = new List<string>();
        var source = CreateList().AsReadOnly();

        foreach (string str in source) {
            if (!string.IsNullOrWhiteSpace(str)) {
                list.Add(str);
            }
        }
        var array = list.ToArray(); // last use

        Assert.Equal(source.Count, array.Length);
        Assert.Equal("lorem", array[0]);
        Assert.Equal("amet", array[4]);

        // CHECK: List`1[string]::.ctor()
        // CHECK-NOT: EnsureCapacity(
        // CHECK: Add(
    }

    [Fact]
    public void BuildArray_EnumerableLoop_ClobberAdd()
    {
        var list = new List<string>();

        list.Add("First");
        var source = CreateList().AsReadOnly();

        foreach (string str in source) {
            list.Add(str);
        }
        var array = list.ToArray(); // last use

        Assert.Equal(source.Count, array.Length - 1);
        Assert.Equal("First", array[0]);
        Assert.Equal("lorem", array[1]);
        Assert.Equal("amet", array[5]);

        // CHECK: Add(this: {{\w+}}, string: "First")
        // CHECK: get_Count(
        // CHECK-NOT: EnsureCapacity(
    }

    [Fact]
    public void AppendTo_ExternalList_ForLoop()
    {
        var list = CreateList();
        var prevItems = list.ToArray();
        int prevCount = list.Count;

        for (int i = 0; i < 16; i++) {
            string str = string.Format("{0}", i);
            list.Add(str);
        }

        Assert.Equal(16, list.Count - prevCount);
        Assert.Equal(prevItems, list.Take(prevCount));
        Assert.Equal("0", list[prevCount]);
        Assert.Equal("15", list[^1]);

        // CHECK: Add(
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static List<string> CreateList() => ["lorem", "ipsum", "dolor", "sit", "amet"];
}
