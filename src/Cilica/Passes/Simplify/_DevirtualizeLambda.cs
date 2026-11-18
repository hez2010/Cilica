namespace Cilica.Passes;

using System.Reflection;
using Cilica.IR.Utils;

partial class SimplifyInsts
{
    // Devirtualize a cached lambda when both edges of the cache phi bind the same target.
    //
    // Func = Func`2[int, bool], Data = C+<>c
    // BB_Header:
    //   slot = fldaddr Data::<>9__0_0 -> Func&
    //   cached = load slot -> Func
    //   hit = cmp.ne cached, null -> bool
    //   goto hit ? BB_Result : BB_CacheLoad
    // BB_CacheLoad:
    //   singleton = fldaddr Data::<>9 -> Data&
    //   instance = load singleton -> Data
    //   fn = funcaddr bool Data::<M>b__0_0(Data, int) -> void*
    //   lambda = newobj Func::.ctor(object: instance, nint: fn) -> Func
    //   dest = fldaddr Data::<>9__0_0 -> Func&
    //   store dest, lambda
    //   goto BB_Result
    // BB_Result:
    //   func = phi [BB_Header: cached], [BB_CacheLoad: lambda] -> Func
    //   ...
    //   result = callvirt Func::Invoke(this: func, int: item) -> bool
    // ->
    // BB_Header:
    //   ...
    //   cached = load slot -> Func
    //   receiverAddr = fldaddr Data::<>9 -> Data&
    //   receiver = load receiverAddr -> Data
    //   ...
    // BB_Result:
    //   ...
    //   result = call Data::<M>b__0_0(this: receiver, int: item) -> bool
    private bool DevirtualizeLambda(CallInst call, ICompilationLogger logger)
    {
        var instance = call.Args[0];
        LoadInst? cached = null;
        var allocation = instance is PhiInst phi ? DelegateUtils.GetCachedAllocation(phi, out cached) : instance as NewObjInst;
        if (allocation == null || allocation.Args is not [var target, var pointer] ||
            DelegateUtils.GetMethodAddress(pointer) is not { Constraint: null } address) return false;
        if (!DevirtualizeDelegate(call, target, address, cached, logger)) return false;

        // Open instance binding can fail at construction even when Invoke can be rewritten.
        // A closed binding validates its receiver at construction, before Invoke's arguments.
        bool safeReceiver = address.Method.IsStatic || address.IsVirtual ||
            target is ConstString || ConstFolding.FoldCondition(target) == true;
        if (allocation.NumUses == 0 && safeReceiver && !TypeUtils.HasFinalizer(allocation.ResultType) && !allocation.Constructor.RequiresTypeInitialization() &&
            !allocation.ResultType.RequiresModuleInitialization()) allocation.Remove();
        return true;
    }

    private bool DevirtualizeDelegate(CallInst call, Value target, FuncAddrInst address, LoadInst? cached, ICompilationLogger logger)
    {
        var method = address.Method;

        if (target is ConstNull) {
            if (address.IsVirtual) return false;
            var replacement = DelegateUtils.CreateOpenCall(call, method);
            if (replacement == null) return false;

            logger.Trace($"Replaced delegate invocation '{call}' in '{call.Block}' with a call to '{method}'.");
            OnCallReplaced?.Invoke(call, replacement);
            call.ReplaceWith(replacement, insertIfInst: true);
            return true;
        }

        if (method.IsStatic || !DelegateUtils.MatchesInvocation(call.Method, method, open: false)) return false;
        // A delegate may capture an unchecked or independently resolved
        // virtual target. Only replace it when a direct call has the same receiver.
        if (!method.DeclaringType.IsValueType && !target.ResultType.IsAssignableTo(method.DeclaringType)) return false;
        if (address.IsVirtual && address.Object != target) return false;

        if (cached != null) {
            // Reading the cache has already initialized its declaring type. Load its readonly
            // receiver there so it is available on both incoming edges.
            if (!IRMatcher.StaticFieldLoad(target, out var receiver) || !receiver.Attribs.HasFlag(FieldAttributes.InitOnly) ||
                receiver.DeclaringType != ((FieldAddrInst)cached.Address).Field.DeclaringType ||
                address.IsVirtual) return false;
            target = new IRBuilder(cached, InsertionDir.After).CreateFieldLoad(receiver);
        }

        if (method.DeclaringType.IsValueType) {
            var unboxed = new CilIntrinsic.UnboxRef(method.DeclaringType, target);
            unboxed.InsertBefore(call);
            target = unboxed;
        }

        logger.Trace($"Replaced delegate invocation '{call}' in '{call.Block}' with a call to '{method}' using its captured receiver.");
        call.Method = method;
        call.SetArg(0, target);
        call.IsVirtual = address.IsVirtual;
        return true;
    }

}
