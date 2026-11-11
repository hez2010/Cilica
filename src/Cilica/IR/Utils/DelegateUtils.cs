namespace Cilica.IR.Utils;

using System.Reflection;

/// <summary> Constructs direct invocations of delegates with a proven binding. </summary>
internal static class DelegateUtils
{
    internal static bool IsCompatibleSignature(TypeSig source, TypeSig target)
        => source == target || !source.HasModifiers && !target.HasModifiers &&
            source.Type is not GenericParamType && target.Type is not GenericParamType &&
            source.Type.IsManagedObject() && target.Type.IsManagedObject() && source.Type.IsAssignableTo(target.Type);

    // The caller establishes an open binding. Compatible reference signatures
    // let the explicit first argument become the method's receiver.
    public static CallInst? CreateOpenInstanceCall(CallInst invocation, MethodDesc method)
    {
        if (invocation.IsReadOnly || invocation.Constraint != null || invocation.Method.IsGeneric) return null;
        if ((invocation.Method.ImplAttribs & MethodImplAttributes.CodeTypeMask) != MethodImplAttributes.Runtime) return null;

        if (invocation.NumArgs - 1 != method.ParamSig.Count) return null;
        if (!IsCompatibleSignature(method.ReturnSig, invocation.Method.ReturnSig)) return null;
        for (int i = 0; i < method.ParamSig.Count; i++) {
            if (!IsCompatibleSignature(invocation.Method.ParamSig[i + 1], method.ParamSig[i])) return null;
        }

        // Open virtual delegates dispatch on the supplied receiver. Nonvirtual
        // delegates may pass null through to a method that never dereferences it.
        bool dispatch = !method.DeclaringType.IsValueType && method.Attribs.HasFlag(MethodAttributes.Virtual);
        return new CallInst(method, invocation.Args[1..].ToArray(), isVirtual: dispatch) {
            IsTailCall = invocation.IsTailCall,
            ResultIsNonNull = invocation.ResultIsNonNull,
            DebugLocation = invocation.DebugLocation
        };
    }
}
