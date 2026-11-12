namespace Cilica.CodeGen.Cil;

partial class ILAssembler
{
    readonly Dictionary<Instruction, StackValue> _stackValues = new();
    readonly Dictionary<int, StackValue> _stackLoads = new();
    readonly List<int> _referenceClears = new();
    readonly HashSet<int> _catchStarts = new();

    internal void EmitStore(ILVariable register, Instruction definition, bool preservesStackType)
    {
        int index = _index;
        EmitStore(register);

        // Phi copies and address-based struct updates access the allocated
        // register directly. Narrowing and floating-point stores can change
        // the value, so their typed storage must also remain explicit.
        var storageType = register.Type.UnderlyingEnumType ?? register.Type;
        if (_index != index + 1 || definition is GuardInst or FieldInsertInst or CilIntrinsic.RefCast ||
            !preservesStackType || register.Type != definition.ResultType || storageType.Kind.IsSmallInt() || storageType.IsFloat() ||
            definition.Users().Any(user => user is PhiInst or FieldInsertInst)) return;

        (_stackValues.GetOrAddRef(definition) ??= new()).StoreIndex = index;
    }

    internal void EmitLoad(ILVariable register, Instruction definition)
    {
        var value = _stackValues.GetOrAddRef(definition) ??= new();
        value.Loads.Add(_index);
        _stackLoads.Add(_index, value);
        EmitLoad(register);
    }

    internal void EmitClear(ILVariable register)
    {
        _referenceClears.Add(_index);
        Emit(ILCode.Ldnull);
        EmitStore(register);
    }

    internal void ReuseStack(SequencePointBuilder? sequencePoints)
    {
        var boundaries = _labelStarts.Values.ToHashSet();
        var rewrites = PlanStackReuse(boundaries);
        bool expands = PreserveLocalLifetimes(rewrites);
        ApplyStackRewrites(rewrites, boundaries, expands, sequencePoints);
    }

    private StackRewrite[] PlanStackReuse(HashSet<int> boundaries)
    {
        var rewrites = new StackRewrite[_index];
        var positions = new (int Depth, int Region)[_index];
        int depth = 0, region = 0;
        for (int i = 0; i < _index; i++) {
            var inst = _insts[i];
            if (boundaries.Contains(i)) {
                region++;
                depth = 0;
            }
            if (inst.OpCode.IsTerminator() || inst.OpCode is ILCode.Localloc or ILCode.Tail_) {
                region++;
                depth = 0;
                continue;
            }
            positions[i] = (depth, region);
            depth += GetStackChange(inst);
        }

        // Process later definitions first so each enclosing lifetime sees the
        // values already retained on the stack. Inspect only use groups, not
        // the instructions between them, to bound planning by O(n log n).
        var retained = new StackOccupancy();
        foreach (var value in _stackValues.Values.OrderByDescending(value => value.StoreIndex)) {
            if (value.StoreIndex < 0 || value.Loads.Count == 0) continue;
            var store = positions[value.StoreIndex];
            if (positions[value.Loads[0]].Region != store.Region || positions[value.Loads[^1]].Region != store.Region) continue;

            int finalGroup = -1;
            for (int next = 0; next < value.Loads.Count;) {
                int first = value.Loads[next++];
                // A root's store leaves an empty stack. A use group must
                // reach that depth with no other retained value above it.
                if (positions[first].Depth != store.Depth - 1 || retained.Contains(first)) {
                    finalGroup = -1;
                    break;
                }
                while (next < value.Loads.Count && value.Loads[next] == value.Loads[next - 1] + 1) next++;
                finalGroup = first;
            }

            if (finalGroup < 0) continue;
            rewrites[value.StoreIndex] = StackRewrite.Remove;
            foreach (int load in value.Loads) rewrites[load] = StackRewrite.Duplicate;
            rewrites[finalGroup] = StackRewrite.Remove;
            retained.Add(value.StoreIndex, finalGroup);
        }
        return rewrites;
    }

    private bool PreserveLocalLifetimes(StackRewrite[] rewrites)
    {
        // Explicit overwrites and clears delimit GC-local lifetimes, including
        // registers whose ordinary loads are all replaced by stack reuse.
        var clearStores = _referenceClears.Select(index => index + 1).ToHashSet();
        var cleared = _referenceClears.Select(index => (ILVariable)_insts[index + 1].Operand!).ToHashSet();
        var used = new HashSet<ILVariable>();
        for (int i = 0; i < _index; i++) {
            if (rewrites[i] == StackRewrite.Keep && !clearStores.Contains(i) && _insts[i].Operand is ILVariable register) used.Add(register);
        }
        // Load retained GC values from their backing local. Duplicating the
        // producer can create a hidden JIT root that outlives the local's clear.
        var referenceTypes = new Dictionary<TypeDesc, bool>();
        bool expands = false;
        for (int i = 0; i < _index; i++) {
            if (rewrites[i] != StackRewrite.Remove || _insts[i] is not { OpCode: ILCode.Stloc, Operand: ILVariable register } || !used.Contains(register) && !cleared.Contains(register)) continue;
            if (!referenceTypes.TryGetValue(register.Type, out bool containsReferences)) {
                containsReferences = register.Type.IsRefOrContainsRefs() != false;
                referenceTypes.Add(register.Type, containsReferences);
            }
            if (containsReferences) {
                rewrites[i] = StackRewrite.StoreAndLoad;
                used.Add(register);
                expands = true;
            }
        }
        foreach (int index in _referenceClears) {
            if (!used.Contains((ILVariable)_insts[index + 1].Operand!)) rewrites[index] = rewrites[index + 1] = StackRewrite.Remove;
        }
        return expands;
    }

    private void ApplyStackRewrites(StackRewrite[] rewrites, HashSet<int> boundaries, bool expands, SequencePointBuilder? sequencePoints)
    {
        var indices = new int[_index + 1];
        var source = GetInstructions();
        if (expands) _insts = new ILInstruction[_insts.Length];
        foreach (var register in _usedVars) register.Index = -1;
        _usedVars.Clear();
        _stackDepth = _maxStackDepth = 0;
        _index = 0;
        ILVariable? topLocal = null;
        for (int i = 0; i < source.Length; i++) {
            indices[i] = _index;
            if (boundaries.Contains(i)) topLocal = null;
            if (_catchStarts.Contains(i)) {
                _stackDepth++;
                _maxStackDepth = Math.Max(_maxStackDepth, _stackDepth);
            }
            if (rewrites[i] == StackRewrite.Remove) continue;
            var inst = rewrites[i] == StackRewrite.Duplicate ? new ILInstruction(ILCode.Dup) : source[i];
            if (inst.OpCode == ILCode.Ldloc && inst.Operand == topLocal) {
                inst = new(ILCode.Dup);
            } else if (inst.OpCode != ILCode.Dup) {
                topLocal = inst.OpCode == ILCode.Ldloc ? (ILVariable)inst.Operand! : null;
            }
            if (inst.Operand is ILVariable register) {
                RecordVariableUse(register);
            }
            Emit(inst.OpCode, inst.Operand);
            if (rewrites[i] == StackRewrite.StoreAndLoad) {
                topLocal = (ILVariable)inst.Operand!;
                EmitLoad(topLocal);
            }
        }
        indices[source.Length] = _index;
        foreach (var label in _labelStarts.Keys) _labelStarts[label] = indices[_labelStarts[label]];
        sequencePoints?.RemapIndices(indices);
    }

    enum StackRewrite : byte { Keep, Remove, Duplicate, StoreAndLoad }

    // Disjoint intervals (store, final use], ordered by decreasing start.
    // Reverse definition order permits amortized constant-time union, with
    // binary searches to detect another retained value at a use.
    sealed class StackOccupancy
    {
        readonly List<(int Start, int End)> _ranges = new();

        public bool Contains(int index)
        {
            int lo = 0, hi = _ranges.Count;
            while (lo < hi) {
                int mid = lo + (hi - lo) / 2;
                if (_ranges[mid].Start >= index) lo = mid + 1;
                else hi = mid;
            }
            return lo < _ranges.Count && index <= _ranges[lo].End;
        }

        public void Add(int start, int end)
        {
            while (_ranges.Count != 0 && _ranges[^1].Start <= end) {
                end = Math.Max(end, _ranges[^1].End);
                _ranges.RemoveAt(_ranges.Count - 1);
            }
            _ranges.Add((start, end));
        }
    }

    sealed class StackValue
    {
        public int StoreIndex = -1;
        public readonly List<int> Loads = new();
    }
}
