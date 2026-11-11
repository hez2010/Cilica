namespace Cilica.Analysis;

/// <summary> The contents of a private, straight-line array at one consuming call. </summary>
internal sealed record KnownArray(Value[] Values, Instruction? Allocation, Instruction[] Stores, Instruction[] Addresses, bool IsObserved = false)
{
    public static bool TryRead(Value value, CallInst consumer, int count, InstructionOrder order, out KnownArray result,
        TypeDesc? elementType = null, bool allowNull = true)
    {
        result = null!;
        elementType ??= PrimType.Object;
        if (count == 0 && IsEmpty(value, elementType, allowNull)) {
            result = new([], value as Instruction, [], []);
            return true;
        }
        if (value is not CilIntrinsic.NewArray { StaticArgs.Length: 0 } allocation) return false;
        if (allocation.ElemType != elementType || !order.PrecedesInBlock(allocation, consumer)) return false;
        if (allocation.Args[0] is not ConstInt length || length.Value != count) return false;

        // Only element reads/writes and this consumer may use the array.
        // An escaping array or element address is not a known value.
        var addresses = new HashSet<Instruction>();
        var stores = new HashSet<Instruction>();
        bool observed = false;
        int consumerUses = 0;
        foreach (var (user, operand) in allocation.Uses()) {
            if (user == consumer) {
                consumerUses++;
                continue;
            }
            if (user is not ArrayAddrInst address || operand != 0 || address.ElemType != elementType) return false;
            if (address.Index is not ConstInt index || index.Value < 0 || index.Value >= count) return false;
            addresses.Add(address);
            foreach (var (use, position) in address.Uses()) {
                if (position != 0) return false;
                if (use is LoadInst load && load.ElemType == elementType) {
                    observed = true;
                } else if (use is StoreInst store && store.ElemType == elementType && store.Flags == PointerFlags.None) {
                    stores.Add(store);
                } else return false;
            }
        }
        if (consumerUses != 1) return false;

        // The allocation and consumer share a straight-line prefix. Only writes
        // in that prefix can initialize this array before consumption.
        var values = Enumerable.Repeat<Value>(ConstNull.Create(), count).ToArray();
        var lastWrites = new int[count];
        foreach (StoreInst store in stores) {
            if (!order.PrecedesInBlock(store, consumer)) continue;
            int index = (int)((ConstInt)((ArrayAddrInst)store.Address).Index).Value;
            int position = order.Index(store);
            if (position > lastWrites[index]) {
                values[index] = store.Value;
                lastWrites[index] = position;
            }
        }
        result = new(values, allocation, stores.ToArray(), addresses.ToArray(), observed);
        return true;
    }

    private static bool IsEmpty(Value value, TypeDesc elementType, bool allowNull)
    {
        if (value is ConstNull) return allowNull;
        if (elementType.IsCorelibType(typeof(Type)) && value is LoadInst {
            Address: FieldAddrInst { Field: { IsStatic: true, Name: "EmptyTypes", DeclaringType: var owner } }
        } && owner.IsCorelibType(typeof(Type))) return true;
        return value is CallInst { Method.Name: "Empty", NumArgs: 0 } call &&
            call.Method.DeclaringType.IsCorelibType(typeof(Array)) &&
            call.Method.GenericParams is [var element] && element == elementType;
    }

    public void RemoveIfUnobserved()
    {
        if (IsObserved) return;
        foreach (var store in Stores) store.Remove();
        foreach (var address in Addresses) address.Remove();
        if (Allocation?.NumUses == 0) Allocation.Remove();
    }
}
