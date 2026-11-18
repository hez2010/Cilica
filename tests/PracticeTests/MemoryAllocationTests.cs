namespace Cilica.PracticeTests;

using Cilica.Attributes;
using System.Runtime.CompilerServices;

[Optimize]
public class MemoryAllocationTests
{
    [Fact]
    public void StackAllocatedBuffersCanFollowOtherCallArguments()
    {
        Assert.Equal(42, ConsumeStackBuffer(34, stackalloc byte[8]));
    }

    [DoNotOptimize]
    [MethodImpl(MethodImplOptions.NoInlining)]
    static int ConsumeStackBuffer(int prefix, Span<byte> buffer)
    {
        buffer.Fill(1);
        return prefix + buffer.Length;
    }

    [Fact]
    public void InlinedInitializedLocalsStartFreshAtEveryCall()
    {
        int sum = 0;
        for (int i = 0; i < 4; i++) sum += FreshLocal();
        Assert.Equal(0, sum);
    }

    static int FreshLocal()
    {
        Unsafe.SkipInit(out int value);
        return Increment(ref value);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static int Increment(ref int value) => value++;

    [Fact]
    public void MultidimensionalArrayTypesHaveRuntimeIdentity()
    {
        Assert.True(typeof(int[,]) == typeof(int).MakeArrayType(2));
    }

    [Fact]
    public void MultidimensionalArrayConstructorsRemainIntact()
    {
        var values = new double[2, 3];
        values[1, 2] = 42;
        Assert.Equal(42, values[1, 2]);
        Assert.Equal(6, values.Length);
    }
}
