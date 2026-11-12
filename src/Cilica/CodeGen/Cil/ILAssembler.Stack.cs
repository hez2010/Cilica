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
        var removed = new bool[_index];
        var boundaries = _labelStarts.Values.ToHashSet();

        // Process later definitions first so each enclosing lifetime sees the
        // stack space occupied by values that already stay on the stack.
        foreach (var value in _stackValues.Values.OrderByDescending(value => value.StoreIndex)) {
            if (value.StoreIndex < 0 || value.Loads.Count == 0) continue;
            int lastLoad = value.Loads[^1];
            int depth = 0, nextLoad = 0, finalGroup = -1;

            for (int i = value.StoreIndex + 1; i <= lastLoad; i++) {
                if (boundaries.Contains(i)) break;
                if (removed[i]) continue;
                var inst = _insts[i];
                if (inst.OpCode.IsTerminator() || inst.OpCode is ILCode.Localloc or ILCode.Tail_) break;

                if (i == value.Loads[nextLoad]) {
                    // Consecutive uses can share the top copy. Separate groups
                    // require the retained value to be exposed again.
                    if (depth != 0) break;
                    int first = nextLoad++;
                    while (nextLoad < value.Loads.Count && value.Loads[nextLoad] == value.Loads[nextLoad - 1] + 1 &&
                           !boundaries.Contains(value.Loads[nextLoad])) nextLoad++;
                    depth += nextLoad - first;
                    i = value.Loads[nextLoad - 1];
                    if (nextLoad == value.Loads.Count) {
                        finalGroup = value.Loads[first];
                        break;
                    }
                } else {
                    depth += GetStackChange(inst);
                    if (depth < 0) break;
                }
            }

            if (finalGroup < 0) continue;
            removed[value.StoreIndex] = true;
            foreach (int load in value.Loads) _insts[load] = new(ILCode.Dup);
            removed[finalGroup] = true;
        }

        // A cleared reference needs no local when its entire lifetime is on
        // the stack. Keep clears for registers shared with other lifetimes.
        var clearStores = _referenceClears.Select(index => index + 1).ToHashSet();
        var used = new HashSet<ILVariable>();
        for (int i = 0; i < _index; i++) {
            if (!removed[i] && !clearStores.Contains(i) && _insts[i].Operand is ILVariable register) used.Add(register);
        }
        foreach (int index in _referenceClears) {
            if (!used.Contains((ILVariable)_insts[index + 1].Operand!)) removed[index] = removed[index + 1] = true;
        }

        var indices = new int[_index + 1];
        foreach (var register in _usedVars) register.Index = -1;
        _usedVars.Clear();
        _stackDepth = _maxStackDepth = 0;
        int count = 0;
        ILVariable? topLocal = null;
        for (int i = 0; i < _index; i++) {
            indices[i] = count;
            if (boundaries.Contains(i)) topLocal = null;
            if (_catchStarts.Contains(i)) {
                _stackDepth++;
                _maxStackDepth = Math.Max(_maxStackDepth, _stackDepth);
            }
            if (removed[i]) continue;

            var inst = _insts[i];
            if (inst.OpCode == ILCode.Ldloc && inst.Operand == topLocal) {
                inst = new(ILCode.Dup);
            } else if (inst.OpCode != ILCode.Dup) {
                topLocal = inst.OpCode == ILCode.Ldloc ? (ILVariable)inst.Operand! : null;
            }
            if (inst.Operand is ILVariable register) {
                RecordVariableUse(register);
            }
            UpdateStackDepth(inst.OpCode, inst.Operand);
            _insts[count++] = inst;
        }
        indices[_index] = count;
        _index = count;
        foreach (var label in _labelStarts.Keys) _labelStarts[label] = indices[_labelStarts[label]];
        sequencePoints?.RemapIndices(indices);
    }

    sealed class StackValue
    {
        public int StoreIndex = -1;
        public readonly List<int> Loads = new();
    }
}
