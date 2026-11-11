namespace Cilica.Passes;

using System.Reflection;
using Cilica.Analysis;
using Cilica.IR.Utils;

partial class ReflectionRewriter
{
    // A missing receiver denotes allocation through ConstructorInfo.Invoke.
    private readonly record struct InvocationArguments(Value? Receiver, Value Array, bool WrapExceptions);

    private InvocationArguments? MatchInvocation(CallInst call)
    {
        if (Matches(call, typeof(MethodBase), "Invoke", PrimType.Object, PrimType.Object, PrimType.Object.CreateArray())) {
            return new(call.Args[1], call.Args[2], WrapExceptions: true);
        }
        if (Matches(call, typeof(ConstructorInfo), "Invoke", PrimType.Object, PrimType.Object.CreateArray())) {
            return new(null, call.Args[1], WrapExceptions: true);
        }

        var resolver = _ctx.Module.Resolver;
        bool construct = call.Method.DeclaringType.IsCorelibType(typeof(ConstructorInfo));
        TypeSig[] options = [resolver.Import(typeof(BindingFlags)), resolver.Import(typeof(Binder)),
            PrimType.Object.CreateArray(), resolver.Import(typeof(System.Globalization.CultureInfo))];
        if (!Matches(call, construct ? typeof(ConstructorInfo) : typeof(MethodBase), "Invoke", PrimType.Object,
                construct ? options : [PrimType.Object, ..options])) return null;

        // The built-in conversions below do not consult the binder or culture.
        // Only DoNotWrapExceptions affects these invocations.
        int offset = construct ? 1 : 2;
        if (call.Args[offset] is not ConstInt bits) return null;
        var flags = (BindingFlags)bits.Value;
        return new(construct ? null : call.Args[1], call.Args[offset + 2], WrapExceptions: !flags.HasFlag(BindingFlags.DoNotWrapExceptions));
    }

    private bool FoldMethod(CallInst call, MethodDefOrSpec method)
    {
        if (MatchInvocation(call) is not { } invocation) return false;
        int count = method.ParamSig.Count - (method.IsInstance ? 1 : 0);
        if (!KnownArray.TryRead(invocation.Array, call, count, _order, out var array)) return false;
        if (!FoldInvocation(call, method, invocation.Receiver, array.Values, invocation.WrapExceptions, array.IsObserved ? invocation.Array : null)) return false;
        array.RemoveIfUnobserved();
        return true;
    }

    private bool FoldProperty(CallInst call, ReflectionLookup.Result resolved)
    {
        var property = (PropertyDef)resolved.Member!;
        bool load = Matches(call, typeof(PropertyInfo), "GetValue", PrimType.Object, PrimType.Object) ||
            Matches(call, typeof(PropertyInfo), "GetValue", PrimType.Object, PrimType.Object, PrimType.Object.CreateArray());
        bool store = Matches(call, typeof(PropertyInfo), "SetValue", PrimType.Void, PrimType.Object, PrimType.Object) ||
            Matches(call, typeof(PropertyInfo), "SetValue", PrimType.Void, PrimType.Object, PrimType.Object, PrimType.Object.CreateArray());
        if (!load && !store) return false;

        if (_lookups.ResolvePropertyAccessor(resolved, setter: store)?.Member is not MethodDefOrSpec accessor) return false;

        bool hasIndexArgument = call.NumArgs == (load ? 3 : 4);
        if (!KnownArray.TryRead(hasIndexArgument ? call.Args[^1] : ConstNull.Create(), call, property.Sig.ParamTypes.Count, _order, out var index)) return false;
        if (accessor.ParamSig.Skip(accessor.IsInstance ? 1 : 0).Any(parameter => parameter.Type is ByrefType)) return false;
        // Property accessors receive a separate argument array. Their conversions
        // do not copy values back to the caller's index array.
        if (!FoldInvocation(call, accessor, call.Args[1], load ? index.Values : [..index.Values, call.Args[2]], wrapExceptions: true)) return false;
        index.RemoveIfUnobserved();
        return true;
    }

    private bool FoldInvocation(CallInst reflection, MethodDefOrSpec method, Value? receiver, Value[] values, bool wrapExceptions, Value? observedArray = null)
    {
        if (!CanInvokeWithArguments(method, receiver, values)) return false;
        if (wrapExceptions && !CanWrapInvocation(reflection)) return false;

        // Reflection does not wrap errors raised while preparing the arguments.
        var builder = BuilderBefore(reflection);
        var arguments = PrepareInvocationArguments(builder, method, receiver, values);
        var continuation = wrapExceptions ? CreateExceptionWrapper(builder, reflection) : null;
        Value result;
        if (receiver == null) {
            if (DelegateUtils.IsDelegateType(method.DeclaringType) && method.DeclaringType.RequiresTypeInitialization()) {
                builder.CreateTypeInitialization(method.DeclaringType);
            }
            result = BoxResult(builder, builder.CreateNewObj(method, arguments));
        } else {
            var direct = builder.Emit(new CallInst(method, arguments, isVirtual: method.IsInstance && !method.DeclaringType.IsValueType) {
                InBounds = true,
                DebugLocation = reflection.DebugLocation
            });
            var value = method.ReturnType is ByrefType ? builder.CreateLoad(direct) : (Value)direct;
            result = method.ReturnType == PrimType.Void ? ConstNull.Create() : BoxResult(builder, value);
        }
        if (continuation != null) EndExceptionWrapper(builder, reflection, continuation);

        // Argument updates follow a successful invocation and are not wrapped.
        if (observedArray != null) CopyBackArguments(builder, method,
            arguments.AsSpan(method.IsInstance && receiver != null ? 1 : 0), observedArray);
        if (reflection.ResultType == PrimType.Void) reflection.Remove();
        else reflection.ReplaceWith(result);
        return true;
    }

    private bool CanInvokeWithArguments(MethodDefOrSpec method, Value? receiver, Value[] values)
    {
        if (!CanReferenceMethod(method) || !CanInvokeSignature(method)) return false;
        var owner = method.DeclaringType;
        if (!_typeFacts.IsBoxable(owner) || Compilation.IsFrameSensitive(method)) return false;
        if (receiver == null) {
            if (!method.IsConstructor || owner.Attribs.HasFlag(TypeAttributes.Abstract)) return false;
            // NativeAOT requires a statically encoded method address in delegate
            // constructor IL, rather than an arbitrary converted IntPtr argument.
            if (DelegateUtils.IsDelegateType(owner) && _ctx.Compilation.Settings.NativeAotCompatibility) return false;
        } else {
            if (method.IsConstructor || method.IsStaticConstructor || owner.IsCorelibType(typeof(Nullable<>))) return false;
            if (method.IsInstance && !CanUseReceiver(receiver, owner)) return false;
        }

        int offset = method.IsInstance ? 1 : 0;
        if (values.Length != method.ParamSig.Count - offset) return false;
        for (int i = 0; i < values.Length; i++) {
            var parameter = method.ParamSig[i + offset].Type;
            var element = parameter is ByrefType reference ? reference.ElemType : parameter;
            if (!CanConvert(values[i], element, allowWidening: parameter is not ByrefType) || !IsOrdinaryArgument(values[i])) return false;
        }
        return true;
    }

    private Value[] PrepareInvocationArguments(IRBuilder builder, MethodDefOrSpec method, Value? receiver, Value[] values)
    {
        int offset = method.IsInstance ? 1 : 0;
        int receiverCount = method.IsInstance && receiver != null ? 1 : 0;
        var arguments = new Value[values.Length + receiverCount];
        if (receiverCount != 0) arguments[0] = ConvertReceiver(builder, receiver!, method.DeclaringType);
        for (int i = 0; i < values.Length; i++) {
            var parameter = method.ParamSig[i + offset].Type;
            if (parameter is ByrefType reference) {
                // Each byref argument owns a copy, even when input boxes alias.
                var slot = _ctx.Method.CreateVar(reference.ElemType);
                builder.CreateStore(slot, ConvertArgument(builder, values[i], reference.ElemType));
                arguments[i + receiverCount] = slot;
            } else {
                arguments[i + receiverCount] = ConvertArgument(builder, values[i], parameter);
            }
        }
        return arguments;
    }

    private static void CopyBackArguments(IRBuilder builder, MethodDefOrSpec method, ReadOnlySpan<Value> arguments, Value array)
    {
        int offset = method.IsInstance ? 1 : 0;
        for (int i = 0; i < arguments.Length; i++) {
            var parameter = method.ParamSig[i + offset].Type;
            Value value;
            if (parameter is ByrefType) value = builder.CreateLoad(arguments[i]);
            else if (parameter.IsCorelibType(typeof(Nullable<>))) value = arguments[i];
            else continue;
            builder.CreateArrayStore(array, ConstInt.CreateI(i), BoxResult(builder, value), inBounds: true);
        }
    }

    private bool CanInvokeSignature(MethodDefOrSpec method)
    {
        if (method.Definition.HasCustomAttrib("System.Runtime.InteropServices", "UnmanagedCallersOnlyAttribute")) return false;
        var resultType = method.ReturnType is ByrefType reference ? reference.ElemType : method.ReturnType;
        if (resultType != PrimType.Void && !_typeFacts.IsBoxable(resultType)) return false;
        if (method.ReturnType is ByrefType && resultType == PrimType.Void) return false;
        int offset = method.IsInstance ? 1 : 0;
        return !method.ReturnSig.HasModifiers && !method.ParamSig.Skip(offset).Any(sig => sig.HasModifiers);
    }

    private bool CanWrapInvocation(CallInst reflection)
    {
        // A catch in this module must see non-Exception throws as reflection's catch does.
        if (_ctx.Compilation.WrapsNonExceptions(_ctx.Module) != _ctx.Compilation.WrapsNonExceptions(_ctx.Module.Resolver.CoreLib)) return false;
        // CIL exception filters cannot contain a nested try/catch.
        return !_filterCalls.Contains(reflection);
    }

    private BasicBlock CreateExceptionWrapper(IRBuilder builder, CallInst reflection, TypeDesc? catchType = null)
    {
        var previous = reflection.Block;
        var continuation = previous.Split(reflection);
        var body = _ctx.Method.CreateBlock(insertAfter: previous);
        var handler = _ctx.Method.CreateBlock(insertAfter: body);
        previous.SetBranch(body);

        var exceptionType = _ctx.Module.Resolver.Import(typeof(Exception));
        var guard = new GuardInst(GuardKind.Catch, handler, catchType ?? exceptionType);
        body.InsertFirst(guard);
        var caught = new IRBuilder(handler);
        var constructor = _ctx.Module.Resolver.Import(typeof(TargetInvocationException))
            .FindMethod(".ctor", new MethodSig(PrimType.Void, [exceptionType], true));
        caught.Emit(new ThrowInst(caught.CreateNewObj(constructor, guard)));

        builder.SetPosition(body, InsertionDir.After);
        return continuation;
    }

    private static void EndExceptionWrapper(IRBuilder builder, CallInst reflection, BasicBlock continuation)
    {
        builder.Emit(new LeaveInst(continuation));
        builder.SetPosition(reflection, InsertionDir.Before);
    }

    private bool IsOrdinaryArgument(Value value)
    {
        // Invoke substitutes optional defaults for Missing, including object parameters.
        if (value is ConstNull or CilIntrinsic.Box { SourceType.IsValueType: true }) return true;
        if (value is CilIntrinsic.Box box) return !_typeFacts.CanContainMissing(box.SourceType);
        var missing = _typeFacts.Resolver.Import(typeof(Missing));
        return TypeUtils.HasConcreteType(value) ? value.ResultType != missing : !_typeFacts.CanContainMissing(value.ResultType);
    }
}
