namespace Cilica.Passes;

using System.Reflection;
using Cilica.Analysis;
using Cilica.IR.Utils;

partial class ReflectionRewriter
{
    private readonly record struct DelegateBinding(MethodDesc Constructor, bool IsOpen);

    private bool FoldDelegate(CallInst call, DelegateCreation creation, MethodDefOrSpec method)
    {
        if (BindDelegate(creation, method) is not { } binding) return false;
        // Invoke keeps its delegate alive while arguments are evaluated.
        // Rewriting it must not expose a finalizer during those evaluations.
        if (TypeUtils.HasFinalizer(creation.Type)) return false;
        if (binding.IsOpen && method.IsInstance) {
            bool bindingIsValid = CanProveOpenInstanceBinding(method);
            bool changed = FoldOpenInstanceInvocations(call, creation.Type, method, bindingIsValid);
            if (bindingIsValid && call.NumUses == 0 && CanDiscardDelegateBinding(creation.Type, method)) {
                call.Remove();
                return true;
            }
            if (!bindingIsValid || !CanConstructOpenInstanceDelegate(method)) return changed;
        }

        bool virtualTarget = !binding.IsOpen && method.IsInstance &&
            !method.DeclaringType.IsValueType && method.Attribs.HasFlag(MethodAttributes.Virtual);
        if (virtualTarget && !PreservesDelegateIdentity(call, creation, method)) return false;

        var builder = BuilderBefore(call);
        // Reflection honors precise type initialization even when the runtime's
        // specialized delegate constructor does not run the static constructor.
        if (creation.Type.RequiresTypeInitialization()) builder.CreateTypeInitialization(creation.Type);
        // Keep the boxed receiver for virtual addresses; the runtime may need
        // both an unboxing stub and the constructed method's instantiation context.
        var address = builder.Emit(new FuncAddrInst(method, virtualTarget ? creation.Target : null));
        call.ReplaceWith(builder.CreateNewObj(binding.Constructor, creation.Target, address));
        return true;
    }

    private DelegateBinding? BindDelegate(DelegateCreation creation, MethodDefOrSpec method)
    {
        if (!CanTakeMethodAddress(method, allowAbstract: true)) return null;
        if (method.Definition.HasCustomAttrib("System.Runtime.InteropServices", "UnmanagedCallersOnlyAttribute")) return null;

        var delegateType = creation.Type;
        if (!ReflectionTypeFacts.IsRuntimeType(delegateType)) return null;
        if (!_typeFacts.CanInspectArgument(delegateType)) return null;
        if (delegateType.BaseType?.IsCorelibType(typeof(MulticastDelegate)) != true) return null;
        var invoke = delegateType.FindMethod("Invoke", throwIfNotFound: false);
        var constructor = delegateType.FindMethod(".ctor",
            new MethodSig(PrimType.Void, [PrimType.Object, PrimType.IntPtr], true), throwIfNotFound: false);
        if (invoke == null || constructor == null || !_access.CanUseMember(constructor)) return null;
        if (!CanBindSignature(method.ReturnSig, invoke.ReturnSig) || invoke.CallConv != CallConvention.Managed) return null;

        // An open delegate supplies every method argument, including 'this'.
        // A closed delegate captures the method's first argument in its target.
        int count = invoke.ParamSig.Count - 1;
        bool open = count == method.ParamSig.Count;
        if (open) {
            if (creation.Target is not ConstNull) return null;
        } else {
            if (!creation.AllowClosed || count != method.ParamSig.Count - 1) return null;
            if (!CanBindClosedTarget(creation.Target, method)) return null;
        }

        int offset = open ? 0 : 1;
        for (int i = 0; i < count; i++) {
            if (!CanBindSignature(invoke.ParamSig[i + 1], method.ParamSig[i + offset])) return null;
        }
        return new(constructor, open);
    }

    private bool CanBindSignature(TypeSig source, TypeSig target)
        => source == target || DelegateUtils.IsCompatibleSignature(source, target) &&
            (_ctx.Compilation.Settings.AllowCrossAssemblyIPO || _typeFacts.IsAssignable(source.Type, target.Type));

    private bool CanBindClosedTarget(Value target, MethodDefOrSpec method)
    {
        if (method.IsInstance) {
            // Reflection can bind a closed delegate to null, whereas the IL
            // delegate constructor requires a non-null instance target.
            return CanUseReceiver(target, method.DeclaringType);
        }
        var firstParameter = method.ParamSig[0].Type;
        return _typeFacts.IsReferenceType(firstParameter) && CanConvert(target, firstParameter);
    }

    private bool CanConstructOpenInstanceDelegate(MethodDefOrSpec method)
    {
        // NativeAOT cannot express an open instance binding with constructor IL.
        if (_ctx.Compilation.Settings.NativeAotCompatibility) return false;
        return !method.Attribs.HasFlag(MethodAttributes.Abstract);
    }

    // CoreCLR's open virtual dispatch stub rejects instantiated generic methods.
    // Abstract non-generic slots use normal virtual dispatch and can bind.
    private static bool IsGenericVirtualReferenceMethod(MethodDefOrSpec method)
        => method.IsGeneric && !method.DeclaringType.IsValueType && method.Attribs.HasFlag(MethodAttributes.Virtual);

    private bool CanProveOpenInstanceBinding(MethodDefOrSpec method)
    {
        if (IsGenericVirtualReferenceMethod(method)) return false;

        // Exact signatures can still exceed the runtime shuffle thunk's encoding.
        // Apple arm64 has the smallest range: 12-bit byte offsets. Bound each
        // scalar slot by 8 bytes, reserving delegate 'this' and a hidden argument.
        const int maxScalarArguments = (0x1000 / sizeof(ulong)) - 2;
        if (method.ParamSig.Count > maxScalarArguments) return false;
        if (method.ReturnType != PrimType.Void && !HasPortableDelegateSlot(method.ReturnType)) return false;
        return method.ParamSig.All(parameter => HasPortableDelegateSlot(parameter.Type));
    }

    private bool HasPortableDelegateSlot(TypeDesc type)
        // Aggregate layouts and unconstrained value-type variables need target
        // ABI information; their binding must remain a runtime operation.
        => type is GenericParamType ? _typeFacts.IsReferenceType(type)
            : type.StackType is StackType.Int or StackType.Long or StackType.NInt or StackType.Float or StackType.ByRef or StackType.Object;

    private bool CanDiscardDelegateBinding(TypeDefOrSpec delegateType, MethodDefOrSpec method)
    {
        // Binding can initialize the delegate type or activate modules used by
        // the target and its signature.
        // Keep the factory when those effects may be observable, including for
        // delegate types whose initializer is marked BeforeFieldInit.
        if (delegateType.Definition.HasStaticConstructor) return false;
        if (_typeFacts.Resolver.SkipModuleInitializers) return true;

        var pending = new Stack<TypeDesc>(method.GenericParams);
        var visited = new HashSet<TypeDesc>(ReferenceEqualityComparer.Instance);
        var visitedBases = new HashSet<TypeDef>();
        pending.Push(delegateType);
        pending.Push(method.DeclaringType);
        while (pending.TryPop(out var type)) {
            if (!visited.Add(type)) continue;
            if (type is GenericParamType) return false;
            if (type is CompoundType compound) {
                pending.Push(compound.ElemType);
                continue;
            }
            if (type is PrimType primitive) type = primitive.GetDefinition(_typeFacts.Resolver);
            if (type is not TypeDefOrSpec nominal) continue;
            if (nominal.RequiresModuleInitialization()) return false;
            foreach (var argument in nominal.GenericParams) pending.Push(argument);

            // The base introduces fixed metadata types; actual arguments are
            // inspected separately. Visiting each definition once also bounds
            // activation through recursive generic inheritance.
            if (visitedBases.Add(nominal.Definition) && nominal.BaseType is { } parent) pending.Push(parent);
        }
        return true;
    }

    private bool PreservesDelegateIdentity(CallInst factory, DelegateCreation creation, MethodDefOrSpec method)
    {
        // Invoke and null checks cannot observe which MethodInfo the delegate exposes.
        if (DelegateValues(factory, creation.Type).All(value => value.Uses().All(use => use.OperIndex == 0 &&
            (use.Parent is CilIntrinsic.NullCheck || IsDelegateCast(use.Parent, creation.Type) ||
                MatchDelegateInvocation(use.Parent, creation.Type) != null)))) return true;

        var receiverType = Devirtualize.ExactType(creation.Target);
        if (receiverType is PrimType primitive) receiverType = primitive.GetDefinition(_typeFacts.Resolver);
        if (receiverType is not TypeDefOrSpec exactType) return false;
        if (method.DeclaringType.IsInterface && TypeUtils.CanHaveDynamicInterfaces(exactType)) return false;

        // Reflection retains the requested virtual method as delegate identity.
        // A constructor using ldvirtftn can expose the selected override instead.
        return VirtualDispatch.SameMethod(_lookups.ResolveVirtualMethod(method, exactType), method);
    }

    private bool FoldOpenInstanceInvocations(CallInst factory, TypeDefOrSpec delegateType, MethodDefOrSpec method, bool bindingIsValid)
    {
        bool changed = bindingIsValid && !factory.ResultIsNonNull;
        if (bindingIsValid) factory.ResultIsNonNull = true;
        var values = DelegateValues(factory, delegateType).ToArray();
        var casts = new List<Instruction>();
        foreach (var value in values) {
            if (value != factory) casts.Add(value);
            foreach (var (user, operand) in value.Uses().AsEnumerable().ToArray()) {
                if (operand != 0) continue;
                if (bindingIsValid && user is CilIntrinsic.NullCheck) {
                    user.Remove();
                    changed = true;
                    continue;
                }
                if (MatchDelegateInvocation(user, delegateType) is not { } invocation) continue;
                var direct = DelegateUtils.CreateOpenInstanceCall(invocation, method);
                if (direct == null) continue;

                // A factory that returns null must still fail at invocation,
                // after the invocation arguments have been evaluated.
                if (!bindingIsValid && !invocation.InBounds) BuilderBefore(invocation).CreateNullCheck(value);
                _ctx.Logger.Trace($"Replaced open delegate invocation '{invocation}' in '{invocation.Block}' with a call to '{method}'.");
                invocation.ReplaceWith(direct, insertIfInst: true);
                changed = true;
            }
        }
        // These casts preserve the exact delegate type established by binding.
        // Remove unused children before parents so they cannot keep a dead factory.
        for (int i = casts.Count - 1; i >= 0; i--) {
            if (casts[i].NumUses == 0) {
                casts[i].Remove();
                changed = true;
            }
        }
        return changed;
    }

    private static bool IsDelegateCast(Instruction value, TypeDefOrSpec delegateType)
        => value is CilIntrinsic.CastClass or CilIntrinsic.RefCast or CilIntrinsic.AsInstance && value.ResultType == delegateType;

    private static IEnumerable<Instruction> DelegateValues(Instruction factory, TypeDefOrSpec delegateType)
    {
        var pending = new Stack<Instruction>();
        var visited = new HashSet<Instruction>();
        pending.Push(factory);
        while (pending.TryPop(out var value)) {
            if (!visited.Add(value)) continue;
            yield return value;
            foreach (var (user, operand) in value.Uses()) {
                if (operand == 0 && IsDelegateCast(user, delegateType)) pending.Push(user);
            }
        }
    }

    private static CallInst? MatchDelegateInvocation(Instruction instruction, TypeDefOrSpec delegateType)
        => instruction is CallInst { Method.Name: "Invoke", IsStatic: false } invocation &&
            invocation.Method.DeclaringType == delegateType ? invocation : null;

    private sealed record DelegateCreation(TypeDefOrSpec Type, Value MethodInfo, Value Target, bool AllowClosed)
    {
        public static DelegateCreation? Match(CallInst call, ModuleResolver resolver, ReflectionLookup lookups)
        {
            if (call.Method.Name != "CreateDelegate") return null;
            if (!call.IsVirtual && call.Method.Attribs.HasFlag(MethodAttributes.Virtual)) return null;
            if (call.Method.DeclaringType.IsCorelibType(typeof(MethodInfo)) && !call.IsStatic) {
                return MatchMethodInfoFactory(call, resolver, lookups);
            }
            if (call.Method.DeclaringType.IsCorelibType(typeof(Delegate)) && call.IsStatic) {
                return MatchStaticFactory(call, resolver, lookups);
            }
            return null;
        }

        private static DelegateCreation? MatchMethodInfoFactory(CallInst call, ModuleResolver resolver, ReflectionLookup lookups)
        {
            var typeType = resolver.Import(typeof(Type));
            var delegateBase = resolver.Import(typeof(Delegate));
            TypeDesc? delegateType;
            bool allowClosed;
            if (call.Method.GenericParams is [var argument]) {
                delegateType = argument;
                if (new MethodSig(argument, [], true, numGenPars: 1).Matches(call.Method)) {
                    allowClosed = false;
                } else if (new MethodSig(argument, [PrimType.Object], true, numGenPars: 1).Matches(call.Method)) {
                    allowClosed = true;
                } else return null;
            } else {
                if (Matches(call, typeof(MethodInfo), "CreateDelegate", delegateBase, typeType)) {
                    allowClosed = false;
                } else if (Matches(call, typeof(MethodInfo), "CreateDelegate", delegateBase, typeType, PrimType.Object)) {
                    allowClosed = true;
                } else return null;
                delegateType = lookups.ResolveType(call.Args[1]);
            }
            var target = allowClosed ? call.Args[^1] : ConstNull.Create();
            return delegateType is TypeDefOrSpec nominal ? new(nominal, call.Args[0], target, allowClosed) : null;
        }

        private static DelegateCreation? MatchStaticFactory(CallInst call, ModuleResolver resolver, ReflectionLookup lookups)
        {
            var typeType = resolver.Import(typeof(Type));
            var delegateBase = resolver.Import(typeof(Delegate));
            var methodInfoType = resolver.Import(typeof(MethodInfo));
            Value methodInfo, target;
            bool allowClosed;
            if (new MethodSig(delegateBase, [typeType, methodInfoType], false).Matches(call.Method) ||
                new MethodSig(delegateBase, [typeType, methodInfoType, PrimType.Bool], false).Matches(call.Method)) {
                methodInfo = call.Args[1];
                target = ConstNull.Create();
                allowClosed = false;
            } else if (new MethodSig(delegateBase, [typeType, PrimType.Object, methodInfoType], false).Matches(call.Method) ||
                new MethodSig(delegateBase, [typeType, PrimType.Object, methodInfoType, PrimType.Bool], false).Matches(call.Method)) {
                methodInfo = call.Args[2];
                target = call.Args[1];
                allowClosed = true;
            } else return null;

            return lookups.ResolveType(call.Args[0]) is TypeDefOrSpec delegateType
                ? new(delegateType, methodInfo, target, allowClosed) : null;
        }
    }
}
