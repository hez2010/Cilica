namespace Cilica.Passes;

using System.Reflection;
using Cilica.IR.Utils;

partial class ReflectionRewriter
{
    readonly Dictionary<LocalSlot, StoreInst?> _handleDefinitions = new();

    private bool CanTakeMethodAddress(MethodDefOrSpec method, bool allowAbstract = false)
    {
        if (!CanReferenceMethod(method)) return false;
        if (!method.Attribs.HasFlag(MethodAttributes.Abstract)) return true;
        return allowAbstract && method.IsInstance && method.Attribs.HasFlag(MethodAttributes.Virtual);
    }

    private static bool IsMethodHandleCall(CallInst call)
        => call.Method.ReturnType.IsCorelibType(typeof(RuntimeMethodHandle)) &&
            Matches(call, typeof(MethodBase), "get_MethodHandle", call.Method.ReturnType);

    private static bool IsFunctionPointerCall(CallInst call)
        => Matches(call, typeof(RuntimeMethodHandle), "GetFunctionPointer", PrimType.IntPtr);

    private bool FoldMethodHandle(CallInst call, MethodDefOrSpec method)
    {
        if (!_access.CanUseMember(method)) return false;
        call.ReplaceWith(new CilIntrinsic.LoadHandle(_ctx.Module.Resolver, method) { DebugLocation = call.DebugLocation }, insertIfInst: true);
        return true;
    }

    private bool FoldFunctionPointer(CallInst call, MethodDefOrSpec method)
    {
        if (!method.IsStatic || !CanTakeMethodAddress(method)) return false;
        var builder = BuilderBefore(call);
        // Preserve the MethodSpec so ldftn supplies an instantiating stub when
        // the shared implementation requires hidden generic-context arguments.
        call.ReplaceWith(builder.CreateConvert(builder.Emit(new FuncAddrInst(method)), PrimType.IntPtr));
        return true;
    }

    private MethodDefOrSpec? GetFunctionPointerTarget(CallInst call)
    {
        if (!IsFunctionPointerCall(call) || call.Args[0] is not LocalSlot { IsPinned: false, IsHardExposed: false } slot) return null;
        if (!_handleDefinitions.TryGetValue(slot, out var definition)) {
            definition = FindHandleDefinition(slot);
            _handleDefinitions.Add(slot, definition);
        }
        if (definition == null || !_order.PrecedesInBlock(definition, call)) return null;
        return definition.Value switch {
            CilIntrinsic.LoadHandle { Entity: MethodDefOrSpec method } => method,
            CallInst handle when IsMethodHandleCall(handle) => _lookups.Resolve(handle.Args[0])?.Member as MethodDefOrSpec,
            _ => null
        };
    }

    private static StoreInst? FindHandleDefinition(LocalSlot slot)
    {
        StoreInst? definition = null;
        foreach (var (user, operand) in slot.Uses()) {
            if (operand != 0) return null;
            if (user is StoreInst { Flags: PointerFlags.None } store && store.ElemType == slot.Type) {
                if (definition != null) return null;
                definition = store;
            } else if (user is LoadInst { Flags: PointerFlags.None } load && load.ElemType == slot.Type) {
                continue;
            } else if (user is not CallInst use || !IsFunctionPointerCall(use)) {
                return null;
            }
        }
        return definition;
    }
}
