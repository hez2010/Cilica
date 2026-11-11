namespace Cilica.Passes;

using System.Reflection;
using Cilica.Analysis;
using Cilica.IR.Utils;

/// <summary> Replaces statically resolved reflection with direct member operations. </summary>
public sealed class FoldReflection : IMethodPass
{
    public MethodPassResult Run(MethodTransformContext ctx) => new ReflectionRewriter(ctx).Run();
}

// Lookup facts and access checks belong to one method transformation. The pass
// itself is stateless and can be reused by the compilation pipeline.
internal sealed partial class ReflectionRewriter
{
    readonly MethodTransformContext _ctx;
    readonly ReflectionTypeFacts _typeFacts;
    readonly ReflectionLookup _lookups;
    readonly InliningAccess _access;
    readonly InstructionOrder _order;
    readonly HashSet<CallInst> _filterCalls = new();

    public ReflectionRewriter(MethodTransformContext ctx)
    {
        _ctx = ctx;
        _typeFacts = new ReflectionTypeFacts(ctx.Definition, ctx.Compilation.Settings.AllowCrossAssemblyIPO);
        _order = new InstructionOrder(ctx.Method);
        _lookups = new ReflectionLookup(_typeFacts, _order);
        _access = new InliningAccess(ctx.Definition.DeclaringType,
            ignoreVisibility: ctx.Compilation.Module != ctx.Compilation.Resolver.CoreLib);
        if (ctx.Method.Instructions().OfType<GuardInst>().Any(guard => guard.HasFilter)) {
            var regions = ctx.GetAnalysis<ProtectedRegionAnalysis>();
            foreach (var call in ctx.Method.Instructions().OfType<CallInst>()) {
                if (regions.GetBlockRegion(call.Block).IsFilter) _filterCalls.Add(call);
            }
        }
    }

    public MethodPassResult Run()
    {
        long revision = _ctx.Method.Revision;
        long controlFlowRevision = _ctx.Method.ControlFlowRevision;

        // Rewriting suffixes first leaves earlier arrays and calls together and
        // bounds the total work of splitting blocks for exception wrappers.
        foreach (var call in _ctx.Method.Instructions().OfType<CallInst>().Reverse().ToArray()) {
            if (call.IsTailCall || call.IsReadOnly || call.Constraint != null) continue;
            FoldCall(call);
        }

        _lookups.ApplyFacts(_ctx.Compilation.Settings.PreserveTrimmingDependencies, _ctx.Logger);
        if (_ctx.Method.Revision == revision) return MethodInvalidations.None;
        return _ctx.Method.ControlFlowRevision == controlFlowRevision
            ? MethodInvalidations.DataFlow : MethodInvalidations.ExceptionRegions;
    }

    private void FoldCall(CallInst call)
    {
        if (call.Method.Name == "MakeGenericType" && _lookups.ResolveType(call) is { } constructedType) {
            FoldTypeConstruction(call, constructedType);
            return;
        }
        if (ReflectionLookup.IsMemberLookup(call)) {
            _lookups.Resolve(call);
            return;
        }
        if (ReflectionLookup.IsTypeLookup(call)) {
            _lookups.ResolveType(call);
            return;
        }

        var creation = DelegateCreation.Match(call, _ctx.Module.Resolver, _lookups);
        if (creation == null && !IsMemberAccess(call)) return;

        Value? source = creation?.MethodInfo ?? (call.IsStatic ? null : call.Args[0]);
        var resolved = source == null ? null : _lookups.Resolve(source);
        if (resolved == null && GetFunctionPointerTarget(call) is { } pointerTarget) {
            resolved = new(pointerTarget, pointerTarget.DeclaringType);
        }
        if (resolved == null) return;

        var original = _ctx.Logger.IsEnabled(LogLevel.Trace) ? call.ToString() : null;
        var block = call.Block;
        bool changed = resolved.Member switch {
            MethodDefOrSpec method when creation != null => FoldDelegate(call, creation, method),
            MethodDefOrSpec method when IsFunctionPointerCall(call) => FoldFunctionPointer(call, method),
            MethodDefOrSpec method when IsMethodHandleCall(call) => FoldMethodHandle(call, method),
            FieldDesc field => FoldField(call, field),
            MethodDefOrSpec method => FoldMethod(call, method),
            PropertyDef => FoldProperty(call, resolved),
            _ => false
        };
        if (changed && call.Block == null) {
            _ctx.Logger.Trace($"Folded reflection access '{original}' in '{block}' to '{resolved.Member}'.");
        }
    }

    private void FoldTypeConstruction(CallInst call, TypeDesc type)
    {
        if (_ctx.Compilation.Settings.PreserveTrimmingDependencies || !_access.CanUseType(type)) return;

        var original = _ctx.Logger.IsEnabled(LogLevel.Trace) ? call.ToString() : null;
        var builder = BuilderBefore(call);
        var replacement = CreateTypeReference(builder, type);
        call.ReplaceUses(replacement);
        _lookups.ResolveType(replacement);
        _ctx.Logger.Trace($"Folded reflection type construction '{original}' in '{call.Block}' to '{type}'.");
    }

    private static bool IsMemberAccess(CallInst call)
        => !call.IsStatic && (call.Method.Name switch {
            "GetValue" or "SetValue" => call.Method.DeclaringType.IsCorelibType(typeof(FieldInfo)) ||
                call.Method.DeclaringType.IsCorelibType(typeof(PropertyInfo)),
            "Invoke" => call.Method.DeclaringType.IsCorelibType(typeof(MethodBase)),
            "get_MethodHandle" => IsMethodHandleCall(call),
            "GetFunctionPointer" => IsFunctionPointerCall(call),
            _ => false
        });

    private bool FoldField(CallInst call, FieldDesc field)
    {
        bool load = Matches(call, typeof(FieldInfo), "GetValue", PrimType.Object, PrimType.Object);
        bool store = Matches(call, typeof(FieldInfo), "SetValue", PrimType.Void, PrimType.Object, PrimType.Object);
        if (!load && !store) return false;
        if (!ReflectionTypeFacts.IsRuntimeType(field.DeclaringType)) return false;
        if (!_access.CanUseMember(field) || !_typeFacts.IsBoxable(field.Type)) return false;
        if (field.Attribs.HasFlag(FieldAttributes.Literal)) return load && field is FieldDefOrSpec literal && FoldLiteralField(call, literal);
        if (field.DeclaringType.IsCorelibType(typeof(Nullable<>))) return false;
        if (!field.IsStatic && !CanUseReceiver(call.Args[1], field.DeclaringType)) return false;

        if (store) {
            if (field.Attribs.HasFlag(FieldAttributes.InitOnly) || !CanStoreField(field.Type)) return false;
            if (!CanConvert(call.Args[2], field.Type)) return false;
        }

        bool initialize = field.DeclaringType.RequiresTypeInitialization(fieldAccess: true);
        if (initialize && _filterCalls.Contains(call)) return false;

        var builder = BuilderBefore(call);
        if (initialize) EmitFieldInitialization(builder, call, field.DeclaringType);
        var receiver = field.IsStatic ? null : ConvertReceiver(builder, call.Args[1], field.DeclaringType);
        var address = builder.CreateFieldAddr(field, receiver, inBounds: true);
        // Reflection's field accessors use acquire/release accesses, including
        // reference fields without an IsVolatile modifier.
        if (load) {
            var value = builder.CreateLoad(address, flags: PointerFlags.Volatile);
            call.ReplaceWith(BoxResult(builder, value));
        } else {
            var value = ConvertArgument(builder, call.Args[2], field.Type);
            EmitFieldStore(builder, address, value);
            call.Remove();
        }
        return true;
    }

    private bool FoldLiteralField(CallInst call, FieldDefOrSpec field)
    {
        var constant = ReadConstant(field.Type, field.Definition.DefaultValue);
        if (constant == null) return false;
        var builder = BuilderBefore(call);
        if (constant is ConstString text) {
            // Metadata string constants are materialized afresh by GetValue.
            // A span constructor preserves that identity without a char array.
            var span = _typeFacts.Resolver.Import(typeof(ReadOnlySpan<char>));
            var stringType = _typeFacts.Resolver.Import(typeof(string));
            var characters = builder.CreateCall(stringType.FindMethod("op_Implicit", new MethodSig(span, [PrimType.String], false)), text);
            constant = builder.CreateNewObj(stringType.FindMethod(".ctor", new MethodSig(PrimType.Void, [span], true)), characters);
        }
        call.ReplaceWith(BoxResult(builder, constant));
        return true;
    }

    private void EmitFieldInitialization(IRBuilder builder, CallInst reflection, TypeDesc owner)
    {
        var continuation = CreateExceptionWrapper(builder, reflection, _typeFacts.Resolver.Import(typeof(TypeInitializationException)));
        builder.CreateTypeInitialization(owner);
        EndExceptionWrapper(builder, reflection, continuation);
    }

    private void EmitFieldStore(IRBuilder builder, FieldAddrInst address, Value value)
    {
        var type = address.Field.Type;
        if (type.Kind is TypeKind.Int64 or TypeKind.UInt64 or TypeKind.Double) {
            var storageType = PrimType.GetFromKind(type.Kind);
            var write = _typeFacts.Resolver.Import(typeof(System.Threading.Volatile)).FindMethod("Write",
                new MethodSig(PrimType.Void, [storageType.CreateByref(), storageType], false));
            Value storage = type == storageType ? address : builder.Emit(new CilIntrinsic.RefCast(storageType.CreateByref(), address));
            builder.CreateCall(write, storage, value);
        } else {
            builder.CreateStore(address, value, flags: PointerFlags.Volatile);
        }
    }

    private CallInst CreateTypeReference(IRBuilder builder, TypeDesc type)
    {
        var typeType = _typeFacts.Resolver.Import(typeof(Type));
        var handle = builder.Emit(new CilIntrinsic.LoadHandle(_typeFacts.Resolver, type));
        return builder.CreateCall(typeType.FindMethod("GetTypeFromHandle", new MethodSig(typeType, [handle.ResultType], false)), handle);
    }

    // Callable references require a constructed managed method. Boxing, caller
    // frame, and receiver restrictions depend on the consuming operation.
    private bool CanReferenceMethod(MethodDefOrSpec method)
    {
        if (!ReflectionTypeFacts.IsConstructedMethod(method)) return false;
        if (method.CallConv != CallConvention.Managed || method.IsRuntimeAsync) return false;
        if (method.IsStatic && method.Attribs.HasFlag(MethodAttributes.Virtual)) return false;
        if (!_typeFacts.CanInspect(method.DeclaringType)) return false;

        // Class MethodImpls can redirect direct calls and method addresses.
        if (TypeUtils.HasClassMethodImpls(method)) return false;
        return _access.CanUseMember(method);
    }

    private static bool Matches(CallInst call, Type owner, string name, TypeDesc result, params TypeSig[] parameters)
        => call.Method.Name == name && call.Method.DeclaringType.IsCorelibType(owner) &&
            new MethodSig(result, parameters, true).Matches(call.Method);

    private static IRBuilder BuilderBefore(Instruction instruction)
        => new IRBuilder(instruction, InsertionDir.Before).SetNextDebugLocation(instruction.DebugLocation);

    private bool CanUseReceiver(Value receiver, TypeDesc owner)
    {
        if (!_typeFacts.IsNonNull(receiver)) return false;
        if (owner.IsValueType) return receiver is CilIntrinsic.Box box && box.SourceType == owner;

        var receiverType = receiver is CilIntrinsic.Box boxed ? boxed.SourceType : receiver.ResultType;
        return _typeFacts.IsAssignable(receiverType, owner) &&
            (receiver is not NewObjInst || TypeUtils.HasConcreteType(receiver));
    }

    private static Value ConvertReceiver(IRBuilder builder, Value receiver, TypeDesc owner)
    {
        if (owner.IsValueType) return builder.CreateUnboxRef(owner, receiver);
        if (receiver.ResultType.IsAssignableTo(owner)) return receiver;
        return builder.Emit(new CilIntrinsic.RefCast(owner, receiver));
    }

    private bool CanStoreField(TypeDesc type)
        // Arbitrary structs require layout-sensitive copying. Known wide scalars
        // use Volatile.Write to retain atomicity on 32-bit targets.
        => type is GenericParamType ? _typeFacts.IsReferenceType(type)
            : type.Kind != TypeKind.Struct;

    private static Value BoxResult(IRBuilder builder, Value value)
        => value.ResultType.IsValueType || value.ResultType is GenericParamType ? builder.CreateBox(value.ResultType, value) : value;
}
