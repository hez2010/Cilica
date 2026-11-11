namespace Cilica.Passes;

using System.Reflection;
using Cilica.Analysis;

partial class ReflectionRewriter
{
    private void FoldMemberConstruction(CallInst call, ReflectionLookup.Result resolved)
    {
        if (ReflectionLookup.IsHandleLookup(call) || !_access.CanUseMember(resolved.Member!)) return;

        Type factoryType;
        string factoryName;
        if (resolved.Member is MethodDefOrSpec) {
            // NativeAOT requires the declaring type's handle. CoreCLR also
            // accepts a derived reflected type and preserves its member cache.
            if (resolved.Owner != resolved.ReflectedType && _ctx.Compilation.Settings.NativeAotCompatibility) return;
            factoryType = typeof(MethodBase);
            factoryName = "GetMethodFromHandle";
        } else if (resolved.Member is FieldDesc field) {
            // Literal fields have no runtime handle. Field handles identify
            // metadata reflected through the declaring type only.
            if (field.Attribs.HasFlag(FieldAttributes.Literal) || resolved.Owner != resolved.ReflectedType) return;
            factoryType = typeof(FieldInfo);
            factoryName = "GetFieldFromHandle";
        } else return;
        if (!_access.CanUseType(resolved.ReflectedType)) return;

        var original = _ctx.Logger.IsEnabled(LogLevel.Trace) ? call.ToString() : null;
        var builder = BuilderBefore(call);
        var resolver = _typeFacts.Resolver;
        var factoryOwner = resolver.Import(factoryType);
        var memberHandle = builder.Emit(new CilIntrinsic.LoadHandle(resolver, resolved.Member!));
        var typeHandle = builder.Emit(new CilIntrinsic.LoadHandle(resolver, resolved.ReflectedType));
        var factory = factoryOwner.FindMethod(factoryName, new MethodSig(factoryOwner, [memberHandle.ResultType, typeHandle.ResultType], false));
        var metadata = builder.CreateCall(factory, memberHandle, typeHandle);
        metadata.ResultIsNonNull = true;
        Value replacement = metadata.ResultType == call.ResultType ? metadata
            : builder.Emit(new CilIntrinsic.RefCast(call.ResultType, metadata));
        call.ReplaceUses(replacement);
        _ctx.Logger.Trace($"Folded reflection member construction '{original}' in '{call.Block}' to '{resolved.Member}'.");
    }
}
