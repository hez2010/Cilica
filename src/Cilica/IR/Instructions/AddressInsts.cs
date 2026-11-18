namespace Cilica.IR;

/// <summary> Base class for an instruction that computes the address of a field, array index, or pointer offset. </summary>
public abstract class AddressInst : Instruction
{
    public TypeDesc ElemType => ((PointerType)ResultType).ElemType;

    protected AddressInst(params Value[] opers)
        : base(opers) { }

    /// <summary> Returns the pointer offset factors or <see langword="default"/> if not known at compile time. </summary>
    /// <remarks> These can be used for the formula: <c>basePtr + BaseDisp + index * Stride</c> </remarks>
    public virtual (int Stride, int BaseDisp) GetKnownOffsetFactors() => default;
}
public class ArrayAddrInst : AddressInst
{
    public Value Array {
        get => _operands[0];
        set => ReplaceOperand(0, value);
    }
    public Value Index {
        get => _operands[1];
        set => ReplaceOperand(1, value);
    }

    /// <summary> Indicates whether <see cref="Array"/> is known to be non-null and have in-bounds index. </summary>
    public bool InBounds { get => _inBounds; set => SetProperty(ref _inBounds, value); }

    private bool _inBounds;

    /// <summary> Hints that the result address will only be read from and no variance checks are necessary. </summary>
    public bool IsReadOnly { get => _isReadOnly; set => SetProperty(ref _isReadOnly, value); }

    private bool _isReadOnly;

    // An indexed store checks the stored value against the runtime element type.
    // Taking a writable managed reference requires an exact element type.
    internal bool IsStoreLocation { get; init; }

    /// <summary> Checks if <see cref="AddressInst.ElemType"/> differs from the array type. </summary>
    public bool IsCasting => ElemType != Array.ResultType.ElemType;

    /// <summary> Whether taking a writable address requires a runtime array variance check. </summary>
    public bool RequiresVarianceCheck => TypeCheck != ArrayTypeCheck.None;

    internal ArrayTypeCheck TypeCheck {
        get {
            if (IsReadOnly || ElemType.IsValueType || Array is CilIntrinsic.NewArray allocation && allocation.ElemType == ElemType ||
                Array.ResultType is ArrayType array && array.ElemType == ElemType && (ElemType.Kind == TypeKind.String || TypeUtils.IsExactType(ElemType))) {
                return ArrayTypeCheck.None;
            }
            return IsStoreLocation ? ArrayTypeCheck.StoredValue : ArrayTypeCheck.WritableReference;
        }
    }

    public override bool MayThrow => !InBounds || TypeCheck == ArrayTypeCheck.WritableReference;
    public override string InstName => "arraddr" + (IsStoreLocation ? ".store" : "") + (IsReadOnly ? ".readonly" : "") + (InBounds ? ".inbounds" : "");

    public ArrayAddrInst(Value array, Value index, TypeDesc? elemType = null, bool inBounds = false, bool readOnly = false)
        : base(array, index)
    {
        elemType ??= ((ArrayType)array.ResultType).ElemType;
        ResultType = elemType.CreateByref();
        InBounds = inBounds;
        IsReadOnly = readOnly;
    }

    public override void Accept(InstVisitor visitor) => visitor.Visit(this);
}

internal enum ArrayTypeCheck { None, WritableReference, StoredValue }

public class FieldAddrInst : AddressInst
{
    public Value? Obj {
        get => IsStatic ? null : _operands[0];
        set {
            Ensure.That(!IsStatic && value != null);
            ReplaceOperand(0, value);
        }
    }

    public FieldDesc Field { get => _field; set => SetProperty(ref _field, value); }

    private FieldDesc _field = default!;

    /// <summary> Indicates whether <see cref="Obj"/> is known to be a non-null object instance containing <see cref="Field"/>. </summary>
    public bool InBounds { get => _inBounds; set => SetProperty(ref _inBounds, value); }

    private bool _inBounds;

    [MemberNotNullWhen(false, nameof(Obj))]
    public bool IsStatic => Field.IsStatic;

    [MemberNotNullWhen(true, nameof(Obj))]
    public bool IsInstance => !IsStatic;

    private bool MayInitializeType => IsStatic && Field.DeclaringType.RequiresTypeInitialization(fieldAccess: true);
    public override bool MayThrow => MayInitializeType || (IsInstance && !InBounds);
    public override bool MayWriteToMemory => MayInitializeType;
    public override bool MayReadFromMemory => MayInitializeType;

    public override string InstName => "fldaddr" + (InBounds ? ".inbounds" : "");

    public FieldAddrInst(FieldDesc field, Value? obj = null, bool inBounds = false)
        : base(obj == null ? [] : [obj])
    {
        Ensure.That(field.IsInstance == (obj != null));
        Ensure.That(obj == null || obj.ResultType.IsPointerOrObject());

        ResultType = field.Type.CreateByref();
        Field = field;
        InBounds = inBounds || obj is LocalSlot;
    }

    public override void Accept(InstVisitor visitor) => visitor.Visit(this);

    protected override void PrintOperands(PrintContext ctx)
    {
        ctx.Print(" ");
        ctx.PrintAsOperand(Field);

        if (IsInstance) {
            ctx.Print(", ");
            ctx.PrintAsOperand(Obj);
        }
    }
}

/// <summary> Computes the offset address of a pointer. </summary>
public class PtrOffsetInst : AddressInst
{
    public Value BasePtr {
        get => _operands[0];
        set => ReplaceOperand(0, value);
    }
    public Value Index {
        get => _operands[1];
        set => ReplaceOperand(1, value);
    }

    /// <summary> Known index stride. If zero, this is computed at runtime to be the size of <see cref="AddressInst.ElemType"/>. </summary>
    public int Stride { get => _stride; set => SetProperty(ref _stride, value); }

    private int _stride;

    /// <summary> Whether the index stride is a constant known at compile time. </summary>
    public bool KnownStride => Stride > 0;

    public override string InstName => "lea";

    public PtrOffsetInst(Value basePtr, Value index, TypeDesc strideType)
        : base(basePtr, index)
    {
        ResultType = basePtr.ResultType is ByrefType 
            ? strideType.CreateByref()
            : strideType.CreatePointer();
            
        // sizeof(void) makes no sense and is illegal in IL, but we'll assume
        // it's a byte offset for a pointer of unknown type.
        Stride = strideType.Kind == TypeKind.Void ? 1 : strideType.Kind.Size();
    }
    public PtrOffsetInst(Value basePtr, Value index, int stride)
        : base(basePtr, index)
    {
        Ensure.That(stride > 0);
        ResultType = basePtr.ResultType as PointerType ?? PrimType.Void.CreatePointer();
        Stride = stride;
    }
    /// <summary> Internal unchecked cloning constructor. </summary>
    internal PtrOffsetInst(Value basePtr, Value index, TypeDesc resultType, int stride, int _)
        : base(basePtr, index)
    {
        ResultType = resultType;
        Stride = stride;
    }

    public override void Accept(InstVisitor visitor) => visitor.Visit(this);

    protected override void PrintOperands(PrintContext ctx)
    {
        ctx.Print($" {BasePtr} + {Index}");

        if (Stride != 0) {
            ctx.Print($" * {PrintToner.Number}{Stride.ToString()}");
        }
    }
}
