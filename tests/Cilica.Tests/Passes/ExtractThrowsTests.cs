namespace Cilica.Tests.Passes;

using Cilica.AsmIO;
using Cilica.IR;
using Cilica.IR.Utils;
using Cilica.Passes;

[Collection("ModuleResolver")]
public class ExtractThrowsTests(ModuleResolverFixture fixture) : PassTestBase(fixture)
{
    [Fact]
    public void SharedThrowHelpersPreserveMessageContentsAcrossPoolPages()
    {
        string[] messages = [
            "ordinary message", "message", "\u00ad", "\0", "", new string('x', 256),
            .. Enumerable.Range(0, 20).Select(i => new string((char)('A' + i), 255))
        ];
        var body = Utils.CreateTestMethod(_modResolver, PrimType.String, PrimType.Int32, PrimType.Bool);
        var ctor = _modResolver.Import(typeof(InvalidOperationException)).FindMethod(".ctor",
            new MethodSig(PrimType.Void, [PrimType.String], true));
        var builder = new IRBuilder(body.CreateBlock());
        for (int i = 0; i < messages.Length; i++) {
            var selected = body.CreateBlock();
            builder.ForkIf(builder.CreateEq(body.Args[0], ConstInt.CreateI(i)), selected);
            var path = new IRBuilder(selected);
            var fail = body.CreateBlock();
            path.ForkIf(body.Args[1], fail);
            var literal = ConstString.Create(messages[i]);
            path.CreateReturn(literal);
            var throwing = new IRBuilder(fail);
            throwing.Emit(new ThrowInst(throwing.CreateNewObj(ctor, literal)));
        }
        builder.CreateReturn(ConstNull.Create());
        Run(body, new ExtractThrows());
        var calls = body.Instructions().OfType<CallInst>().ToArray();
        Assert.Contains(calls, call => call.Args is [ConstInt]);
        Assert.Contains(calls, call => call.Args is [ConstString { Value.Length: 256 }]);
        var run = Utils.Compile<Func<int, bool, string>>(body);
        for (int i = 0; i < messages.Length; i++) {
            var original = run(i, false);
            Assert.Equal(messages[i], original);
            Assert.Equal(original, Assert.Throws<InvalidOperationException>(() => run(i, true)).Message);
        }
    }
}
