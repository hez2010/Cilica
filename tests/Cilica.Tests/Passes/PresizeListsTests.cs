namespace Cilica.Tests.Passes;

using Cilica.AsmIO;
using Cilica.IR;
using Cilica.IR.Utils;
using Cilica.Passes;

[Collection("ModuleResolver")]
public class PresizeListsTests(ModuleResolverFixture fixture) : PassTestBase(fixture)
{
    [Fact]
    public void ABodyWithTwoCycleEntriesCanAppendTwicePerIteration()
    {
        var method = Utils.CreateTestMethod(_modResolver, PrimType.Int32.CreateArray(), PrimType.Bool);
        var entry = new IRBuilder(method.CreateBlock());
        var type = _modResolver.Import(typeof(List<int>));
        var list = entry.CreateNewObj(type.FindMethod(".ctor", new MethodSig(PrimType.Void, [], true)));
        var again = method.CreateVar(PrimType.Bool);
        var loop = new LoopBuilder(entry.Block);
        entry.SetBranch(loop.EntryBlock);
        var index = loop.CreateInductor();
        loop.Build(header => header.CreateSlt(index, ConstInt.CreateI(2)), body => {
            var append = new IRBuilder(method.CreateBlock());
            var relay = method.CreateBlock();
            body.CreateStore(again, ConstInt.CreateI(1));
            body.SetBranch(method.Args[0], append.Block, relay);
            relay.SetBranch(append.Block);
            append.CreateCallVirt("Add", list, index);
            var first = append.CreateLoad(again);
            append.CreateStore(again, ConstInt.CreateI(0));
            append.SetBranch(first, relay, loop.Latch.Block);
        });
        loop.Exit.CreateReturn(loop.Exit.CreateCallVirt("ToArray", list));
        Run(method, new PresizeLists());
        var run = Utils.Compile<Func<bool, int[]>>(method);
        Assert.Equal(new[] { 0, 0, 1, 1 }, run(false));
        Assert.Equal(new[] { 0, 0, 1, 1 }, run(true));
    }
}
