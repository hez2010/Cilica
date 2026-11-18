namespace Cilica.IR.Utils;

using System.Reflection;
using System.Runtime.CompilerServices;

/// <summary> Constructs direct invocations of delegates with a proven binding. </summary>
internal static class DelegateUtils
{
    public static bool IsDelegateType(TypeDesc type)
        => type.BaseType?.IsCorelibType(typeof(MulticastDelegate)) == true;

    internal static bool IsRuntimeInvoke(MethodDesc method)
        => method.Name == "Invoke" && method.IsInstance && IsDelegateType(method.DeclaringType) &&
           (method.ImplAttribs & MethodImplAttributes.CodeTypeMask) == MethodImplAttributes.Runtime;

    // A Roslyn cache merges a non-null field load with the binding assigned on the miss edge.
    // The load and store can use separate fldaddr instructions for the same cache field.
    // See SimplifyInsts.DevirtualizeLambda for the cache diamond and the direct call it enables.
    internal static NewObjInst? GetCachedAllocation(PhiInst phi, out LoadInst? cached)
    {
        cached = null;
        if (phi.NumArgs != 2) return null;
        int hit = phi.GetValue(0) is LoadInst ? 0 : 1;
        if (phi.GetValue(hit) is not LoadInst load || !IRMatcher.StaticFieldLoad(load, out var field) ||
            !IsCompilerCache(field) || phi.GetValue(1 - hit) is not NewObjInst allocation) return null;
        var hitBlock = phi.GetBlock(hit);
        var missBlock = phi.GetBlock(1 - hit);
        if (hitBlock.Last is not BranchInst { Cond: CompareInst { Op: CompareOp.Ne, Left: var value, Right: ConstNull } } branch ||
            value != load || branch.Then != phi.Block || branch.Else != missBlock ||
            !IRMatcher.StaticFieldStore(missBlock.Last.Prev, out var store, out var destination) ||
            destination != field || store.Value != allocation) return null;

        cached = load;
        return allocation;
    }

    private static bool IsCompilerCache(FieldDesc field)
    {
        if (field.DeclaringType is not TypeDefOrSpec owner || (owner.Attribs & TypeAttributes.VisibilityMask) != TypeAttributes.NestedPrivate ||
            !owner.Definition.HasCustomAttrib(typeof(CompilerGeneratedAttribute))) return false;
        // Roslyn uses <>9__... for lambda caches and <N>__... on its static method-group holders.
        return field.Name.StartsWith("<>9__", StringComparison.Ordinal) ||
               owner.IsStatic && field.Name.StartsWith('<') && field.Name.Contains(">__", StringComparison.Ordinal);
    }

    public static FuncAddrInst? GetMethodAddress(Value value)
    {
        while (value is ConvertInst { CheckOverflow: false, ResultType.StackType: StackType.NInt,
            Value.ResultType.StackType: StackType.NInt } conversion) value = conversion.Value;
        return value as FuncAddrInst;
    }

    internal static bool IsCompatibleSignature(TypeSig source, TypeSig target)
        => source == target || !source.HasModifiers && !target.HasModifiers &&
            source.Type is not GenericParamType && target.Type is not GenericParamType &&
            source.Type.IsManagedObject() && target.Type.IsManagedObject() && source.Type.IsAssignableTo(target.Type);

    internal static bool MatchesInvocation(MethodDesc invoke, MethodDesc target, bool open)
    {
        int offset = open ? 0 : 1;
        if (invoke.ParamSig.Count - 1 != target.ParamSig.Count - offset || !IsCompatibleSignature(target.ReturnSig, invoke.ReturnSig)) return false;
        for (int i = offset; i < target.ParamSig.Count; i++) {
            if (!IsCompatibleSignature(invoke.ParamSig[i - offset + 1], target.ParamSig[i])) return false;
        }
        return true;
    }

    // An open binding supplies every argument, including the receiver of an
    // instance method. Reference parameters can have compatible variance.
    public static CallInst? CreateOpenCall(CallInst invocation, MethodDesc method)
    {
        if (invocation.IsReadOnly || invocation.Constraint != null || invocation.Method.IsGeneric) return null;
        if (!IsRuntimeInvoke(invocation.Method) || !MatchesInvocation(invocation.Method, method, open: true)) return null;

        // Open virtual delegates dispatch on the supplied receiver. Nonvirtual
        // delegates may pass null through to a method that never dereferences it.
        bool dispatch = method.IsInstance && !method.DeclaringType.IsValueType && method.Attribs.HasFlag(MethodAttributes.Virtual);
        return new CallInst(method, invocation.Args[1..].ToArray(), isVirtual: dispatch) {
            IsTailCall = invocation.IsTailCall,
            ResultIsNonNull = invocation.ResultIsNonNull,
            DebugLocation = invocation.DebugLocation
        };
    }
}
