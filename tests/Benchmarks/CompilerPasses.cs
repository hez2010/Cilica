namespace Cilica.Benchmarks;

using System.Reflection;
using System.Runtime.CompilerServices;
using BenchmarkDotNet.Attributes;
using Cilica.AsmIO;
using Cilica.IR;
using Cilica.IR.Utils;
using Cilica.Passes;
using MethodBody = Cilica.IR.MethodBody;

[MemoryDiagnoser]
public class CompilerPasses
{
    [Params(1000, 4000, 8000)]
    public int Loads { get; set; }

    ModuleResolver _resolver = null!;
    Compilation _compilation = null!;
    MethodBody _template = null!;
    MethodTransformContext _context = null!;
    readonly ValueNumbering _pass = new();

    [GlobalSetup]
    public void Setup()
    {
        _resolver = new ModuleResolver();
        _resolver.AddTrustedSearchPaths();
        var module = _resolver.Create("CompilerBenchmark");
        var type = module.CreateType(null, "Methods");
        var method = type.CreateMethod("Sum", PrimType.Int32, [new ParamDef(PrimType.Int32.CreateByref(), "source")],
            MethodAttributes.Public | MethodAttributes.Static);
        _template = new MethodBody(method);
        var builder = new IRBuilder(_template.CreateBlock());
        Value sum = ConstInt.CreateI(0);

        for (int i = 0; i < Loads; i++) {
            sum = builder.CreateAdd(sum, builder.CreateLoad(_template.Args[0]));
        }

        builder.CreateReturn(sum);
        _compilation = new Compilation(module, new VoidLogger(), new());
    }

    [IterationSetup]
    public void Reset() => _context = new(_compilation, IRCloner.CloneBody(_template));

    [Benchmark]
    public MethodPassResult RepeatedLoads() => _pass.Run(_context);

    [GlobalCleanup]
    public void Cleanup() => _resolver.Dispose();
}

[MemoryDiagnoser]
public class ReflectionFoldingBenchmarks
{
    public enum Workload { NamedMembers, MemberMetadata, WrappedInvocations, ArgumentArrays, FunctionPointers, InterfaceDelegates }

    [Params(256, 1024, 4096)]
    public int Count { get; set; }

    [ParamsAllValues]
    public Workload Scenario { get; set; }

    ModuleResolver _resolver = null!;
    Compilation _compilation = null!;
    MethodBody _template = null!;
    MethodTransformContext _context = null!;

    [GlobalSetup]
    public void Setup()
    {
        _resolver = new ModuleResolver();
        _resolver.AddTrustedSearchPaths();
        var module = _resolver.Create("ReflectionBenchmark");
        module.GetCustomAttribs(forAssembly: true).Add(new CustomAttrib(_resolver.Import(typeof(RuntimeCompatibilityAttribute)).FindMethod(".ctor"),
            namedArgs: [new() { Name = "WrapNonExceptionThrows", Type = PrimType.Bool, Value = true, IsField = false }]));
        var caller = module.CreateType(null, "Caller").CreateMethod("Run", PrimType.Void, [], MethodAttributes.Public | MethodAttributes.Static);
        _template = new MethodBody(caller);
        var builder = new IRBuilder(_template.CreateBlock());
        var owner = module.CreateType(null, "Target");
        var typeType = _resolver.Import(typeof(Type));
        var methodInfo = _resolver.Import(typeof(MethodInfo));
        var methodBase = _resolver.Import(typeof(MethodBase));
        var getMethod = typeType.FindMethod("GetMethod", new MethodSig(methodInfo, [PrimType.String], true));

        if (Scenario == Workload.InterfaceDelegates) {
            var itf = module.CreateType(null, "IReader", TypeAttributes.Public | TypeAttributes.Interface | TypeAttributes.Abstract);
            itf.SetBaseType(null!);
            owner.Interfaces.Add(itf);
            for (int i = 0; i < Count; i++) {
                itf.CreateMethod("Read" + i, PrimType.Int32, [new ParamDef(itf, "this")], MethodAttributes.Public | MethodAttributes.Abstract | MethodAttributes.Virtual | MethodAttributes.NewSlot);
                var implementation = owner.CreateMethod("Read" + i, PrimType.Int32, [new ParamDef(owner, "this")], MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.NewSlot);
                implementation.Body = new MethodBody(implementation);
                new IRBuilder(implementation.Body.CreateBlock()).CreateReturn(ConstInt.CreateI(i));
            }
            var constructor = owner.CreateMethod(".ctor", PrimType.Void, [new ParamDef(owner, "this")], MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName);
            constructor.Body = new MethodBody(constructor);
            var initialize = new IRBuilder(constructor.Body.CreateBlock());
            initialize.CreateCall(_resolver.Import(typeof(object)).FindMethod(".ctor"), constructor.Body.Args[0]);
            initialize.CreateReturn();
            var receiver = builder.CreateNewObj(constructor);
            var reflected = TypeOf(builder, itf);
            var delegateType = TypeOf(builder, _resolver.Import(typeof(Func<int>)));
            var create = methodInfo.FindMethod("CreateDelegate", new MethodSig(_resolver.Import(typeof(Delegate)), [typeType, PrimType.Object], true));
            var identity = _resolver.Import(typeof(Delegate)).FindMethod("get_Method");
            for (int i = 0; i < Count; i++) {
                var method = builder.CreateCallVirt(getMethod, reflected, ConstString.Create("Read" + i));
                builder.CreateCallVirt(identity, builder.CreateCallVirt(create, method, delegateType, receiver));
            }
        } else {
            var reflected = TypeOf(builder, owner);
            if (Scenario is Workload.NamedMembers or Workload.MemberMetadata) {
                for (int i = 0; i < Count; i++) owner.CreateField("Field" + i, PrimType.Int32, FieldAttributes.Public | FieldAttributes.Static);
                var getField = typeType.FindMethod("GetField", new MethodSig(_resolver.Import(typeof(FieldInfo)), [PrimType.String], true));
                var getName = _resolver.Import(typeof(MemberInfo)).FindMethod("get_Name");
                for (int i = 0; i < Count; i++) {
                    var field = builder.CreateCallVirt(getField, reflected, ConstString.Create("Field" + i));
                    if (Scenario == Workload.MemberMetadata) builder.CreateCallVirt(getName, field);
                }
            } else {
                var target = owner.CreateMethod("Read", PrimType.Int32, Scenario == Workload.ArgumentArrays ? [new ParamDef(PrimType.Object, "value")] : [], MethodAttributes.Public | MethodAttributes.Static);
                target.Body = new MethodBody(target);
                new IRBuilder(target.Body.CreateBlock()).CreateReturn(ConstInt.CreateI(42));
                var method = builder.CreateCallVirt(getMethod, reflected, ConstString.Create("Read"));
                if (Scenario == Workload.FunctionPointers) {
                    var handleType = _resolver.Import(typeof(RuntimeMethodHandle));
                    var slot = _template.CreateVar(handleType);
                    builder.CreateStore(slot, builder.CreateCallVirt(methodBase.FindMethod("get_MethodHandle"), method));
                    var getPointer = handleType.FindMethod("GetFunctionPointer", new MethodSig(PrimType.IntPtr, [], true));
                    for (int i = 0; i < Count; i++) builder.CreateCall(getPointer, slot);
                } else if (Scenario == Workload.ArgumentArrays) {
                    var arrays = Enumerable.Range(0, Count).Select(_ => builder.CreateNewArray(PrimType.Object, ConstInt.CreateI(1))).ToArray();
                    var invoke = methodBase.FindMethod("Invoke", new MethodSig(PrimType.Object, [PrimType.Object, _resolver.Import(typeof(BindingFlags)), _resolver.Import(typeof(Binder)), PrimType.Object.CreateArray(), _resolver.Import(typeof(System.Globalization.CultureInfo))], true));
                    foreach (var array in arrays) builder.CreateCallVirt(invoke, method, ConstNull.Create(), ConstInt.CreateI((int)BindingFlags.DoNotWrapExceptions), ConstNull.Create(), array, ConstNull.Create());
                } else {
                    var invoke = methodBase.FindMethod("Invoke", new MethodSig(PrimType.Object, [PrimType.Object, PrimType.Object.CreateArray()], true));
                    for (int i = 0; i < Count; i++) builder.CreateCallVirt(invoke, method, ConstNull.Create(), ConstNull.Create());
                }
            }
        }
        builder.CreateReturn();
        _compilation = new Compilation(module, new VoidLogger(), new());
    }

    private Value TypeOf(IRBuilder builder, TypeDesc type)
    {
        var fromHandle = _resolver.Import(typeof(Type)).FindMethod("GetTypeFromHandle");
        return builder.CreateCall(fromHandle, builder.Emit(new CilIntrinsic.LoadHandle(_resolver, type)));
    }

    [IterationSetup]
    public void Reset() => _context = new(_compilation, IRCloner.CloneBody(_template));

    [Benchmark]
    public MethodPassResult Fold() => new FoldReflection().Run(_context);

    [GlobalCleanup]
    public void Cleanup() => _resolver.Dispose();
}
