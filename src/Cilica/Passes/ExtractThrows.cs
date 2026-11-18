namespace Cilica.Passes;

using System.Reflection;
using Cilica.Analysis;
using Cilica.CodeGen.Cil;

/// <summary> Extract blocks ending with throw to helper methods. </summary>
public class ExtractThrows : IMethodPass
{
    // Appending to an ldstr copies the pool. Start another helper page before those copies become expensive.
    private const int MaxPoolChars = 4096;
    readonly Dictionary<(TypeDef Owner, MethodDesc Constructor, bool Packed), MethodDef> _helpers = new();

    public MethodPassResult Run(MethodTransformContext ctx)
    {
        bool hasReturn = false;
        List<ThrowInst>? candidates = null;
        foreach (var block in ctx.Method) {
            if (block.Last is ReturnInst) hasReturn = true;
            // Match a newobj immediately followed by a throw of that same value.
            //   ex = newobj InvalidOperationException::.ctor(string: "bad item") -> InvalidOperationException
            //   throw ex
            // ->
            //   call <>_Cilica_Aux::TH_InvalidOperationException(uint: messageId)
            //   throw null
            if (block.Last is ThrowInst { Exception: NewObjInst allocation } site && site.Prev == allocation &&
                allocation.Args is [] or [ConstString] && !allocation.Constructor.DeclaringType.IsUnboundGeneric) {
                (candidates ??= new()).Add(site);
            }
        }
        if (!hasReturn || candidates == null) return MethodInvalidations.None;

        var owner = ctx.Compilation.GetAuxType();
        var access = new InliningAccess(owner);
        bool changed = false;
        foreach (var site in candidates) {
            var allocation = (NewObjInst)site.Exception!;
            if (!access.CanUseMember(allocation.Constructor)) continue;
            var call = CreateThrowCall(owner, allocation);
            call.InsertBefore(site);
            // The helper never returns. The caller still needs a terminator, and RyuJIT
            // can eliminate the unreachable throw when it recognizes the non-returning call.
            site.Exception = ConstNull.Create();
            allocation.Remove();
            changed = true;
            ctx.Logger.Trace($"Extracted throw of '{allocation.ResultType}' in '{site.Block}' into helper '{call.Method}'.");
        }
        return changed ? MethodInvalidations.DataFlow : MethodInvalidations.None;
    }

    private CallInst CreateThrowCall(TypeDef owner, NewObjInst allocation)
    {
        var ctor = allocation.Constructor;
        string? text = allocation.Args is [ConstString { Value.Length: <= byte.MaxValue } message] ? message.Value : null;
        var key = (owner, ctor, text != null);
        int id = 0;
        if (!_helpers.TryGetValue(key, out var helper) || !owner.Methods.Contains(helper) ||
            text != null && !TryPackMessage(helper, text, out id)) {
            helper = _helpers[key] = CreateHelper(owner, ctor, text);
            id = text?.Length ?? 0;
        }
        return new CallInst(helper, text != null ? [ConstInt.CreateI(id)] : allocation.Args.ToArray());
    }

    private static bool TryPackMessage(MethodDef helper, string text, out int id)
    {
        id = 0;
        // An imported helper can already be inlined with its current pool.
        if (helper.Body != null) return false;
        ref var literal = ref helper.ILBody!.Instructions.AsSpan()[0];
        var pool = (string)literal.Operand!;
        int offset = pool.IndexOf(text, StringComparison.Ordinal);
        if (offset < 0) {
            if (pool.Length + text.Length > MaxPoolChars) return false;
            offset = pool.Length;
            literal.Operand = pool + text;
        }
        // A call site passes the pool offset in the upper 24 bits and the length in the low 8.
        // The helper extracts the substring on the throw path.
        id = (offset << 8) | text.Length;
        return true;
    }

    private static MethodDef CreateHelper(TypeDef owner, MethodDesc ctor, string? pool)
    {
        string prefix = "TH_" + ctor.DeclaringType.Name;
        string name = prefix;
        for (int suffix = 1; owner.Methods.Any(method => method.Name == name); suffix++) name = prefix + "_" + suffix;
        ImmutableArray<ParamDef> parameters = pool != null ? [new ParamDef(PrimType.UInt32, "messageId")] :
            ctor.ParamSig.Count == 2 ? [new ParamDef(PrimType.String, "message")] : [];
        var helper = owner.CreateMethod(name, PrimType.Void, parameters, MethodAttributes.Assembly | MethodAttributes.Static);

        var il = new ILAssembler();
        if (pool != null) {
            var substring = owner.Module.Resolver.Import(typeof(string)).FindMethod("Substring",
                new MethodSig(PrimType.String, [PrimType.Int32, PrimType.Int32], true));
            // pool.Substring(id >>> 8, id & 255)
            il.Emit(ILCode.Ldstr, pool);
            il.Emit(ILCode.Ldarg_0);
            il.Emit(ILCode.Ldc_I4_8);
            il.Emit(ILCode.Shr_Un);                    // [pool, offset]
            il.Emit(ILCode.Ldarg_0);
            il.Emit(ILCode.Ldc_I4, (int)byte.MaxValue);
            il.Emit(ILCode.And);                       // [pool, offset, length]
            il.Emit(ILCode.Call, substring);           // [message]
        } else if (parameters.Length == 1) {
            il.Emit(ILCode.Ldarg_0);
        }
        il.Emit(ILCode.Newobj, ctor);
        il.Emit(ILCode.Throw);
        helper.ILBody = il.Assemble(null);
        return helper;
    }
}
