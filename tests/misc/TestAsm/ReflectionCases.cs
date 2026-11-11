using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;

public static class ReflectionCases
{
    public static bool KnownMethodExists() => typeof(Target).GetMethod(nameof(Target.Constant)) is not null;
    public static bool KnownFieldExists() => typeof(Target).GetField(nameof(Target.Value)) is not null;
    public static bool KnownPropertyExists() => typeof(Target).GetProperty(nameof(Target.Property)) is not null;
    public static bool KnownConstructorExists() => typeof(Target).GetConstructor(Type.EmptyTypes) is not null;
    public static bool KnownGenericMethodExists() => typeof(Target).GetMethod(nameof(Target.Generic))!.MakeGenericMethod(typeof(int)) is not null;
    public static bool KnownGenericTypeExists() => typeof(GenericTarget<>).MakeGenericType(typeof(int)) is not null;
    public static bool KnownMethodOperatorNullCheck() => typeof(Target).GetMethod(nameof(Target.Constant)) != null;
    public static bool MissingMethodOperatorNullCheck() => typeof(Target).GetMethod("Absent") == null;
    public static bool KnownFieldOperatorNullCheck() => typeof(Target).GetField(nameof(Target.Value)) != null;
    public static bool KnownPropertyOperatorNullCheck() => typeof(Target).GetProperty(nameof(Target.Property)) != null;
    public static bool KnownConstructorOperatorNullCheck() => typeof(Target).GetConstructor(Type.EmptyTypes) != null;
    public static bool KnownTypeOperatorNullCheck() => typeof(GenericTarget<>).MakeGenericType(typeof(int)) != null;
    public static bool KnownMemberOperatorNullCheck() => (MemberInfo?)typeof(Target).GetMethod(nameof(Target.Constant)) != null;
    public static bool KnownMethodBaseOperatorNullCheck() => (MethodBase?)typeof(Target).GetMethod(nameof(Target.Constant)) != null;
    public static bool KnownMethodReversedNullCheck() => null != typeof(Target).GetMethod(nameof(Target.Constant));
    public static bool MissingMethodIsNull() => typeof(Target).GetMethod("Absent") is null;
    public static bool MissingFieldIsNull() => typeof(Target).GetField("Absent") is null;
    public static bool MissingPropertyIsNull() => typeof(Target).GetProperty("Absent") is null;
    public static bool MissingConstructorIsNull() => typeof(Target).GetConstructor([typeof(int), typeof(int), typeof(int)]) is null;
    public static bool ExcludedFieldIsNull() => typeof(Target).GetField("Secret", BindingFlags.Instance | BindingFlags.Public) is null;
    public static bool MissingAccessorIsNull() => typeof(ReflectionAccessorCases.Target).GetProperty("WriteOnly")!.GetMethod is null;
    public static bool ExcludedAccessorIsNull() => typeof(ReflectionAccessorCases.Target).GetProperty("PrivateSet")!.GetSetMethod(false) is null;
    public static MethodInfo? MissingMethodResult() => typeof(Target).GetMethod("Absent");

    public static string KnownMethodMetadata()
    {
        var method = typeof(Target).GetMethod(nameof(Target.Constant));
        return method is null ? "missing" : method.Name;
    }

    public static MemberInfo[] MethodMetadata() => [
        typeof(Target).GetMethod(nameof(Target.Constant))!,
        typeof(Target).GetMethod(nameof(Target.Add))!,
        typeof(Target).GetMethod(nameof(Target.Overload), [typeof(int)])!,
        typeof(Target).GetMethod(nameof(Target.Generic))!,
        typeof(Target).GetMethod(nameof(Target.Generic))!.MakeGenericMethod(typeof(int)),
        typeof(FailedInitialization).GetMethod(nameof(FailedInitialization.Read))!,
        typeof(ReflectionGenericCases.Abstract).GetMethod("Read")!,
        typeof(ReflectionGenericCases.IValue).GetMethod("Read")!,
        typeof(ReflectionGenericCases.Owner<int>).GetMethod("Echo")!,
        typeof(ReflectionGenericCases.Owner<>).GetMethod("Echo")!,
        typeof(GenericTarget<>).GetMethod("Echo")!.MakeGenericMethod(typeof(int)),
        typeof(ReflectionGenericCases.Owner<int>).GetMethod("Describe")!.MakeGenericMethod(typeof(string))
    ];

    public static MemberInfo[] InlinedDefinitionMetadata() => DefinitionMetadata();
    public static MemberInfo[] InlinedGenericDefinitionMetadata() => DefinitionMetadata<int>();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static MemberInfo[] DefinitionMetadata() => [
        typeof(Target).GetMethod(nameof(Target.Generic))!,
        typeof(GenericTarget<>).GetMethod(nameof(GenericTarget<int>.Echo))!,
        typeof(GenericTarget<>).GetField(nameof(GenericTarget<int>.Value))!,
        typeof(GenericTarget<>).GetConstructor(Type.EmptyTypes)!,
        typeof(GenericTarget<int>).GetMethod(nameof(GenericTarget<int>.Echo))!
    ];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static MemberInfo[] DefinitionMetadata<T>() => [
        typeof(Target).GetMethod(nameof(Target.Generic))!,
        typeof(GenericTarget<>).GetMethod(nameof(GenericTarget<int>.Echo))!,
        typeof(GenericTarget<>).GetField(nameof(GenericTarget<int>.Value))!,
        typeof(GenericTarget<>).GetConstructor(Type.EmptyTypes)!,
        typeof(GenericTarget<T>).GetMethod(nameof(GenericTarget<int>.Echo))!,
        typeof(GenericTarget<T>).GetField(nameof(GenericTarget<int>.Value))!,
        typeof(GenericTarget<T>).GetConstructor(Type.EmptyTypes)!,
        typeof(GenericTarget<T>).GetMethod(nameof(GenericTarget<int>.Echo))!.MakeGenericMethod(typeof(T))
    ];

    public static MemberInfo[] FieldMetadata() => [
        typeof(Target).GetField(nameof(Target.Value))!,
        typeof(Target).GetField(nameof(Target.StaticValue))!,
        typeof(Target).GetField(nameof(Target.Readonly))!,
        typeof(Target).GetField("secret", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.IgnoreCase)!,
        typeof(GenericTarget<int>).GetField(nameof(GenericTarget<int>.Value))!,
        typeof(GenericTarget<string>).GetField(nameof(GenericTarget<string>.StaticValue))!,
        typeof(GenericTarget<>).GetField(nameof(GenericTarget<int>.Value))!,
        typeof(FailedInitialization).GetField(nameof(FailedInitialization.Value))!,
        typeof(FailedValue).GetField(nameof(FailedValue.Value))!
    ];

    public static MemberInfo[] ConstructorMetadata() => [
        typeof(Target).GetConstructor(Type.EmptyTypes)!,
        typeof(GenericTarget<int>).GetConstructor(Type.EmptyTypes)!,
        typeof(GenericTarget<>).GetConstructor(Type.EmptyTypes)!,
        typeof(ReflectionAccessorCases.GenericTarget<int>).GetConstructor([typeof(int)])!,
        typeof(ReflectionAccessorCases.GenericTarget<int>).GetConstructor(BindingFlags.Public | BindingFlags.Instance, null,
            CallingConventions.Standard, [typeof(byte)], null)!,
        typeof(FailedInitialization).GetConstructor(BindingFlags.NonPublic | BindingFlags.Static, null, Type.EmptyTypes, null)!,
        typeof(string).GetConstructor([typeof(char), typeof(int)])!
    ];

    public static MemberInfo[] AccessorMetadata() => [
        typeof(Target).GetProperty(nameof(Target.Property))!.GetMethod!,
        typeof(Target).GetProperty(nameof(Target.Property))!.SetMethod!,
        typeof(Target).GetProperty("Item")!.GetGetMethod()!,
        typeof(GenericTarget<int>).GetProperty(nameof(GenericTarget<int>.Property))!.GetMethod!,
        typeof(GenericTarget<>).GetProperty(nameof(GenericTarget<int>.Property))!.GetMethod!,
        typeof(GenericTarget<int>).GetProperty(nameof(GenericTarget<int>.PrivateSetter))!.GetSetMethod(true)!
    ];

    public static MemberInfo[] InheritedMethodMetadata() => [
        typeof(Target).GetMethod(nameof(Target.Read))!,
        typeof(GenericDerived).GetProperty(nameof(GenericDerived.Property))!.GetMethod!,
        typeof(GenericDerived).GetMethod(nameof(GenericDerived.Echo))!,
        typeof(GenericDerived).GetMethod(nameof(GenericDerived.Echo))!.MakeGenericMethod(typeof(string)),
        typeof(GenericDerived<string>).GetMethod("Echo")!.MakeGenericMethod(typeof(int)),
        typeof(GenericDerived<string>).GetProperty("Property")!.GetMethod!,
        typeof(LookupDerived).GetMethod("set_PrivateGet")!
    ];

    public static MemberInfo[] MetadataWithoutHandles() => [
        typeof(Target).GetProperty(nameof(Target.Property))!,
        typeof(Target).GetField(nameof(Target.EnumConstant))!,
        typeof(Target).GetField(nameof(Target.StringConstant))!,
        typeof(Target).GetField(nameof(Target.Inherited))!,
        typeof(GenericDerived).GetField(nameof(GenericDerived.Value))!,
        typeof(GenericDerived<>).GetMethod("Echo")!,
        typeof(GenericDerived<>).GetMethod("Echo")!.MakeGenericMethod(typeof(int)),
        typeof(GenericDerived<>).GetProperty("Property")!.GetMethod!,
        typeof(GenericDerived<>).GetField("Value")!
    ];

    public static RuntimeMethodHandle OpenInheritedHandle() => typeof(GenericDerived<>).GetMethod("Echo")!.MethodHandle;

    public static object? InvokeWithRuntimeArguments(object?[] arguments)
        => typeof(Target).GetMethod(nameof(Target.Echo))!.Invoke(null, arguments);

    public static FieldInfo MetadataThroughNullableReceiver(Target? target) => target!.GetType().GetField(nameof(Target.Value))!;

    public static object ParameterlessConstructor()
        => ((Target)typeof(Target).GetConstructor(Type.EmptyTypes)!.Invoke(null)).Value;

    public static object ConstructorArguments()
        => ((Target)typeof(Target).GetConstructor([typeof(int)])!.Invoke([(byte)42])).Value;

    public static object GenericConstructor()
        => ((ReflectionAccessorCases.GenericTarget<int>)typeof(ReflectionAccessorCases.GenericTarget<int>).GetConstructor([typeof(int)])!.Invoke([42])).Value;

    public static object StructConstructor()
        => ((Counter)typeof(Counter).GetConstructor([typeof(int)])!.Invoke([42])).Value;

    public static object NullableConstructor()
        => typeof(int?).GetConstructor([typeof(int)])!.Invoke([42]);

    public static bool ConstructorCopyBack()
    {
        var constructor = typeof(Target).GetConstructor([typeof(int).MakeByRefType(), typeof(string).MakeByRefType()])!;
        object?[] arguments = [1, null];
        var created = (Target)constructor.Invoke(arguments);
        return created.Value == 42 && (int)arguments[0]! == 42 && (string)arguments[1]! == "updated";
    }

    public static object ConstructorException()
    {
        try { return typeof(Target).GetConstructor([typeof(int)])!.Invoke([-1]); }
        catch (TargetInvocationException error) { return error.InnerException!.GetType().Name; }
    }

    public static object UnwrappedConstructorException()
    {
        try { return typeof(Target).GetConstructor([typeof(int)])!.Invoke(BindingFlags.DoNotWrapExceptions, null, [-1], null); }
        catch (InvalidOperationException error) { return error.Message; }
    }

    public static object ConstructorInitialization()
    {
        try { return typeof(FailedInitialization).GetConstructor(Type.EmptyTypes)!.Invoke(null); }
        catch (TargetInvocationException error) { return error.InnerException!.GetType().Name; }
    }

    public static object ExistingConstructorReceiver()
    {
        var target = new Target();
        typeof(Target).GetConstructor([typeof(int)])!.Invoke(target, [42]);
        return target.Value;
    }

    public static object StaticConstructorInvocation()
    {
        LookupEffects = 0;
        var constructor = typeof(InitializedValue).GetConstructor(BindingFlags.NonPublic | BindingFlags.Static, null, Type.EmptyTypes, null)!;
        constructor.Invoke(null, null);
        constructor.Invoke(null, null);
        return LookupEffects;
    }

    public static object AbstractConstructor()
    {
        try { return typeof(ReflectionGenericCases.Abstract).GetConstructor(BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null)!.Invoke(null); }
        catch (MemberAccessException error) { return error.GetType().Name; }
    }

    public static object InvalidConstructorArguments()
    {
        try { return typeof(Target).GetConstructor([typeof(int)])!.Invoke(["invalid"]); }
        catch (ArgumentException error) { return error.GetType().Name; }
    }

    public static int LookupChoice(bool chooseField)
    {
        MemberInfo? member = chooseField ? typeof(Target).GetField(nameof(Target.Value)) : typeof(Target).GetMethod(nameof(Target.Constant));
        return member is null ? 0 : 42;
    }

    public static bool LookupOnNullableReceiver(Target? target) => target!.GetType().GetField("Absent") is null;

    public static int LookupEffects;

    public static int MissingLookupWithReceiverEffects()
    {
        LookupEffects = 0;
        return LookupReceiver().GetType().GetField("Absent") is null ? LookupEffects : -1;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Target LookupReceiver()
    {
        LookupEffects++;
        return new Target();
    }

    public static int MissingAccessorWithFlagEffects()
    {
        LookupEffects = 0;
        return typeof(ReflectionAccessorCases.Target).GetProperty("WriteOnly")!.GetGetMethod(AccessorFlag()) is null ? LookupEffects : -1;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool AccessorFlag()
    {
        LookupEffects++;
        return false;
    }

    public static bool MetadataEqualityKeepsEqualsEffects()
    {
        LookupEffects = 0;
        var first = new ObservedType();
        var second = new ObservedType();
        return first == second && LookupEffects == 1;
    }

    private sealed class ObservedType() : TypeDelegator(typeof(Target))
    {
        public override bool Equals(Type? other)
        {
            LookupEffects++;
            return true;
        }
    }

    public static string AmbiguousLookupNullCheck()
    {
        try { return typeof(Target).GetMethod(nameof(Target.Overload)) is null ? "missing" : "found"; }
        catch (AmbiguousMatchException) { return "ambiguous"; }
    }

    public static bool InheritedLookupNullCheck() => typeof(Target).GetField(nameof(Target.Inherited)) is not null;
    public static bool RuntimeLookupNullCheck(string name) => typeof(Target).GetMethod(name) is null;

    public static bool ThrowingAccessorNullCheck() => typeof(Target).GetProperty("Absent")!.GetMethod is null;

    public static bool LookupResultInCatch()
    {
        MethodInfo? method = null;
        try {
            method = typeof(Target).GetMethod(nameof(Target.Overload));
        } catch (AmbiguousMatchException) {
            return method is null;
        }
        return false;
    }

    public static object Fields()
    {
        var target = new Target();
        var field = typeof(Target).GetField(nameof(Target.Value))!;
        field.SetValue(target, 42);
        return field.GetValue(target)!;
    }

    public static object StaticFields()
    {
        var field = typeof(Target).GetField(nameof(Target.StaticValue))!;
        field.SetValue(new object(), "updated");
        return field.GetValue(null)!;
    }

    public static object ObjectFieldStoresMissing()
    {
        var target = new Target();
        var field = typeof(Target).GetField(nameof(Target.ObjectValue))!;
        field.SetValue(target, Type.Missing);
        return ReferenceEquals(Type.Missing, field.GetValue(target));
    }

    public static object StructFieldLoad()
    {
        var target = new Target { Counter = new Counter { Value = 42 } };
        return ((Counter)typeof(Target).GetField(nameof(Target.Counter))!.GetValue(target)!).Value;
    }

    public static object FieldBoxIdentity()
    {
        var target = new Target();
        var field = typeof(Target).GetField(nameof(Target.Value))!;
        return !ReferenceEquals(field.GetValue(target), field.GetValue(target));
    }

    public static object LiteralEnum() => typeof(Target).GetField(nameof(Target.EnumConstant))!.GetValue(null)!;

    public static object LiteralString() => typeof(Target).GetField(nameof(Target.StringConstant))!.GetValue(null)!;

    public static bool LiteralNull() => typeof(Target).GetField(nameof(Target.NullConstant))!.GetValue(null) is null;

    public static bool LiteralStringIdentity()
    {
        var field = typeof(Target).GetField(nameof(Target.StringConstant))!;
        return ReferenceEquals(field.GetValue(null), field.GetValue(null));
    }

    public static object Properties()
    {
        var target = new Target();
        var property = typeof(Target).GetProperty(nameof(Target.Property))!;
        property.SetValue(target, 41, null);
        return property.GetValue(target, null)!;
    }

    public static object StaticProperties()
    {
        var property = typeof(Target).GetProperty(nameof(Target.StaticProperty))!;
        property.SetValue(null, 17);
        return property.GetValue(new object())!;
    }

    public static object MethodArguments()
        => typeof(Target).GetMethod(nameof(Target.Add))!.Invoke(new Target(), new object[] { 37, "hello" })!;

    public static object VoidInvocation()
    {
        var target = new Target();
        object? result = typeof(Target).GetMethod(nameof(Target.Set))!.Invoke(target, new object[] { 42 });
        return result == null && target.Value == 42;
    }

    public static object VirtualInvocation()
        => typeof(Base).GetMethod(nameof(Base.Compute))!.Invoke(new Target(), null)!;

    public static object BoxedStructFields()
    {
        object target = new Counter();
        var field = typeof(Counter).GetField(nameof(Counter.Value))!;
        field.SetValue(target, 42);
        return field.GetValue(target)!;
    }

    public static object BoxedStructMethod()
    {
        object target = new Counter();
        typeof(Counter).GetMethod(nameof(Counter.Increment))!.Invoke(target, null);
        return ((Counter)target).Value;
    }

    public static object MutatedBoxArgument()
    {
        object boxed = 1;
        object[] arguments = [boxed];
        Unsafe.Unbox<int>(boxed) = 42;
        return typeof(Target).GetMethod(nameof(Target.Echo))!.Invoke(null, arguments)!;
    }

    public static object ClosedGeneric()
    {
        var target = new GenericTarget<int>();
        typeof(GenericTarget<int>).GetField(nameof(GenericTarget<int>.Value))!.SetValue(target, 20);
        typeof(GenericTarget<int>).GetProperty(nameof(GenericTarget<int>.Property))!.SetValue(target, 22);
        return target.Value + (int)typeof(GenericTarget<int>).GetProperty(nameof(GenericTarget<int>.Property))!.GetValue(target)!;
    }

    public static object ExactGetType()
    {
        var target = new Target();
        return target.GetType().GetField(nameof(Target.Value))!.GetValue(target)!;
    }

    public static object ChangedArguments()
    {
        object[] arguments = [1];
        arguments[0] = 42;
        return typeof(Target).GetMethod(nameof(Target.Echo))!.Invoke(null, arguments)!;
    }

    public static int PreparedArrays()
    {
        object[] first = [20];
        object[] second = [22];
        var method = typeof(Target).GetMethod(nameof(Target.Echo))!;
        return (int)method.Invoke(null, first)! + (int)method.Invoke(null, second)!;
    }

    public static object EmptyArguments()
        => typeof(Target).GetMethod(nameof(Target.Constant))!.Invoke(null, Array.Empty<object>())!;

    public static object ReferenceArguments()
        => typeof(Target).GetMethod(nameof(Target.ObjectEcho))!.Invoke(null, new object[] { "hello" })!;

    public static object NullReferenceArgument()
        => typeof(Target).GetMethod(nameof(Target.ObjectEcho))!.Invoke(null, new object?[] { null }) == null;

    public static object NullableResult()
        => typeof(Target).GetMethod(nameof(Target.NullableConstant))!.Invoke(null, null)!;

    public static object StaticLookupThroughNullableTarget(Target? target)
        => target!.GetType().GetField(nameof(Target.StaticValue))!.GetValue(null)!;

    private static int PrivateValue = 42;

    public static object DeclaredNonPublicField()
        => typeof(ReflectionCases).GetField(nameof(PrivateValue), BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)!.GetValue(null)!;

    public static object DeclaredOverride()
        => typeof(Target).GetMethod(nameof(Target.Compute), BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)!.Invoke(new Target(), null)!;

    public static object ReflectionExceptionFilter()
    {
        int result = 0;
        try {
            try { typeof(Target).GetMethod(nameof(Target.Fail))!.Invoke(null, null); }
            finally { result += 10; }
        } catch (Exception exception) when (RecordException(exception, ref result)) {
            result += 100;
        }
        return result;
    }

    private static bool RecordException(Exception exception, ref int result)
    {
        result += exception is TargetInvocationException ? 1 : 2;
        return true;
    }

    public static object WrappedException()
    {
        try {
            return typeof(Target).GetMethod(nameof(Target.Fail))!.Invoke(null, null)!;
        } catch (TargetInvocationException exception) {
            return exception.InnerException!.GetType().Name;
        }
    }

    public static object UnwrappedException()
    {
        try {
            return typeof(Target).GetMethod(nameof(Target.Fail))!.Invoke(null, BindingFlags.DoNotWrapExceptions, null, null, null)!;
        } catch (InvalidOperationException exception) {
            return exception.Message;
        }
    }

    public static object PropertyException()
    {
        try {
            return typeof(Target).GetProperty(nameof(Target.FailingProperty))!.GetValue(null)!;
        } catch (TargetInvocationException exception) {
            return exception.InnerException!.GetType().Name;
        }
    }

    public static object InvocationInsideFinally()
    {
        int result = 0;
        try { result = 10; }
        finally { result += (int)typeof(Target).GetMethod(nameof(Target.Constant))!.Invoke(null, null)!; }
        return result;
    }

    public static object InvocationInsideFilter()
    {
        int value = (int)typeof(Target).GetMethod(nameof(Target.Constant))!.Invoke(null, null)!;
        try { throw new InvalidOperationException(); }
        catch (InvalidOperationException) when ((int)typeof(Target).GetMethod(nameof(Target.Constant))!.Invoke(null, null)! == value) {
            return value;
        }
    }

    public static object UnwrappedInvocationInsideFilter()
    {
        try { throw new InvalidOperationException(); }
        catch (InvalidOperationException) when ((int)typeof(Target).GetMethod(nameof(Target.Constant))!
            .Invoke(null, BindingFlags.DoNotWrapExceptions, null, null, null)! == 42) {
            return 42;
        }
    }

    public static object PropertyInsideFilter()
    {
        try { throw new InvalidOperationException(); }
        catch (InvalidOperationException) when ((int)typeof(Target).GetProperty(nameof(Target.FailingProperty))!.GetValue(null)! == 42) {
            return "unexpected";
        }
        catch (InvalidOperationException) { return 42; }
    }

    public static object EscapedArguments()
    {
        object[] arguments = [1];
        Change(arguments);
        return typeof(Target).GetMethod(nameof(Target.Echo))!.Invoke(null, arguments)!;
    }

    public static object ArgumentsUsedAfterInvocation()
    {
        object[] arguments = [1];
        object result = typeof(Target).GetMethod(nameof(Target.Echo))!.Invoke(null, arguments)!;
        arguments[0] = 42;
        return (int)result + (int)arguments[0];
    }

    public static object PrimitiveConversion()
        => typeof(Target).GetMethod(nameof(Target.LongEcho))!.Invoke(null, new object[] { 42 })!;

    public static object UnsignedWidening()
        => typeof(Target).GetMethod(nameof(Target.LongEcho))!.Invoke(null, new object[] { uint.MaxValue })!;

    public static bool UnsignedFloatingWidening()
        => (double)typeof(Target).GetMethod(nameof(Target.DoubleEcho))!.Invoke(null, new object[] { ulong.MaxValue })! == (double)ulong.MaxValue;

    public static object NullableValue()
        => typeof(Target).GetMethod(nameof(Target.NullableEcho))!.Invoke(null, new object[] { 42 })!;

    public static bool NullableNull()
        => typeof(Target).GetMethod(nameof(Target.NullableEcho))!.Invoke(null, new object?[] { null }) is null;

    public static object NullValueArgument()
        => typeof(Target).GetMethod(nameof(Target.Echo))!.Invoke(null, new object?[] { null })!;

    public static object WrongArgumentCount()
    {
        try { return typeof(Target).GetMethod(nameof(Target.Constant))!.Invoke(null, new object[] { 1 })!; }
        catch (TargetParameterCountException exception) { return exception.GetType().Name; }
    }

    public static object InvalidFieldValue()
    {
        try { typeof(Target).GetField(nameof(Target.Value))!.SetValue(new Target(), "hello"); }
        catch (ArgumentException exception) { return exception.GetType().Name; }
        return "unexpected";
    }

    public static object IndexedSetter()
    {
        var receiver = new Target();
        var property = typeof(Target).GetProperty("Item")!;
        property.SetValue(receiver, 42, [2]);
        return property.GetValue(receiver, [2])!;
    }

    public static object IndexedProperty()
        => typeof(Target).GetProperty("Item")!.GetValue(new Target(), new object[] { 35 })!;

    public static object GenericMethodDefinition()
    {
        try { return typeof(Target).GetMethod(nameof(Target.Generic))!.Invoke(null, null)!; }
        catch (InvalidOperationException exception) { return exception.GetType().Name; }
    }

    public static object PrivateField()
        => typeof(Target).GetField("Secret", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(new Target())!;

    public static object RefArgument()
    {
        object[] arguments = [1];
        typeof(Target).GetMethod(nameof(Target.ByRef))!.Invoke(null, arguments);
        return arguments[0];
    }

    public static object RefAndOutCopyBack()
    {
        object[] arguments = [1, null!];
        typeof(Target).GetMethod(nameof(Target.Update))!.Invoke(null, arguments);
        return arguments[0] + ":" + arguments[1];
    }

    public static object RefNullableCopyBack()
    {
        object?[] arguments = [null];
        typeof(Target).GetMethod(nameof(Target.ReplaceNullable))!.Invoke(null, arguments);
        return arguments[0]!;
    }

    public static bool RefValueCopiesBox()
    {
        object original = 42;
        object[] arguments = [original];
        typeof(Target).GetMethod(nameof(Target.ObserveReference))!.Invoke(null, arguments);
        return !ReferenceEquals(original, arguments[0]);
    }

    public static bool NullableArgumentCopiesBox()
    {
        object original = 42;
        object[] arguments = [original];
        typeof(Target).GetMethod(nameof(Target.NullableEcho))!.Invoke(null, arguments);
        return !ReferenceEquals(original, arguments[0]);
    }

    public static bool WideningKeepsOriginalBox()
    {
        object original = 42;
        object[] arguments = [original];
        typeof(Target).GetMethod(nameof(Target.LongEcho))!.Invoke(null, arguments);
        return ReferenceEquals(original, arguments[0]);
    }

    public static object RefReturnValue() => typeof(Target).GetMethod(nameof(Target.RefInt))!.Invoke(null, null)!;

    public static object RefReturnReference() => typeof(Target).GetMethod(nameof(Target.RefString))!.Invoke(null, null)!;

    public static string NullRefReturn()
    {
        try { typeof(Target).GetMethod(nameof(Target.NullRef))!.Invoke(null, null); return "no exception"; }
        catch (TargetInvocationException e) { return e.InnerException!.GetType().Name; }
    }

    public static object BinderArgument()
        => typeof(Target).GetMethod(nameof(Target.Echo))!.Invoke(null, BindingFlags.Default, Type.DefaultBinder, new object[] { 42 }, null)!;

    public static object CompatibleBinder()
        => typeof(Target).GetMethod(nameof(Target.Echo))!.Invoke(null, BindingFlags.ExactBinding, new RejectingBinder(), [42], CultureInfo.InvariantCulture)!;

    public static string RequiredBinder()
    {
        try { typeof(Target).GetMethod(nameof(Target.Echo))!.Invoke(null, BindingFlags.Default, new RejectingBinder(), ["42"], null); return "no exception"; }
        catch (InvalidOperationException e) { return e.Message; }
    }

    public static object MissingOptionalArgument()
        => typeof(Target).GetMethod(nameof(Target.OptionalObject))!.Invoke(null, new object[] { Type.Missing })!;

    public static object OpaqueObjectArgument(object? value)
    {
        try { return typeof(Target).GetMethod(nameof(Target.ObjectEcho))!.Invoke(null, new object?[] { value }) ?? "null"; }
        catch (ArgumentException exception) { return exception.GetType().Name; }
    }

    public static long WideIntegerStore()
    {
        var receiver = new Target();
        typeof(Target).GetField("WideValue")!.SetValue(receiver, 0x123456789ABCDEF0L);
        return receiver.WideValue;
    }

    public static double WideFloatingStore()
    {
        var receiver = new Target();
        typeof(Target).GetField("RealValue")!.SetValue(receiver, 12345.6789012345);
        return receiver.RealValue;
    }

    public static object WideFieldStore()
    {
        var target = new Target();
        typeof(Target).GetField(nameof(Target.WideValue))!.SetValue(target, 42L);
        return target.WideValue;
    }

    public static object InheritedMember()
        => typeof(Target).GetField(nameof(Base.Inherited))!.GetValue(new Target())!;

    public static object InheritedGenericField()
        => typeof(GenericDerived).GetField("Value")!.GetValue(new GenericDerived { Value = 42 })!;

    public static object InheritedGenericProperty()
    {
        var receiver = new GenericDerived();
        var property = typeof(GenericDerived).GetProperty("Property")!;
        property.SetValue(receiver, 42);
        return property.GetValue(receiver)!;
    }

    public static bool InheritedPrivateAccessor()
        => typeof(GenericDerived).GetProperty("PrivateSetter")!.GetSetMethod(true) is null;

    public static object InheritedMethod()
        => typeof(Target).GetMethod("Read")!.Invoke(new Target(), null)!;

    public static object HiddenMethod()
        => typeof(Target).GetMethod("Hidden")!.Invoke(new Target(), null)!;

    public static object FlattenedField()
        => typeof(GenericDerived).GetField("StaticNumber", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)!.GetValue(null)!;

    public static object SignatureLookup()
        => typeof(Target).GetMethod(nameof(Target.Overload), [typeof(int)])!.Invoke(null, new object[] { 42 })!;

    public static object WideningSignatureLookup()
        => typeof(Target).GetMethod(nameof(Target.LongEcho), [typeof(int)])!.Invoke(null, new object[] { 42 })!;

    public static bool VoidPropertyLookup() => typeof(Target).GetProperty(nameof(Target.Property), typeof(void)) is null;

    public static object PropertyTypeLookup()
        => typeof(LookupDerived).GetProperty("ChangedType", typeof(object))!.GetValue(new LookupDerived())!;

    public static object InterfaceConstant()
        => typeof(LookupDerived).GetField("Answer", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)!.GetValue(null)!;

    public static object IgnoreCaseLookup()
        => typeof(Target).GetField("value", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)!.GetValue(new Target())!;

    public static object AmbiguousLookup()
    {
        try { return typeof(Target).GetMethod(nameof(Target.Overload))!.Invoke(null, null)!; }
        catch (AmbiguousMatchException exception) { return exception.GetType().Name; }
    }

    public static object MissingMember()
    {
        try { return typeof(Target).GetField("Missing")!.GetValue(null)!; }
        catch (NullReferenceException exception) { return exception.GetType().Name; }
    }

    public static object InvalidReceiver(object? target)
    {
        try { return typeof(Target).GetField(nameof(Target.Value))!.GetValue(target)!; }
        catch (Exception exception) { return exception.GetType().Name; }
    }

    public static object ReadonlyStore()
    {
        try {
            typeof(Target).GetField(nameof(Target.Readonly))!.SetValue(null, 100);
            return Target.Readonly;
        } catch (FieldAccessException exception) { return exception.GetType().Name; }
    }

    public static object OpenGeneric()
    {
        try { return typeof(GenericTarget<>).GetField(nameof(GenericTarget<int>.StaticValue))!.GetValue(null)!; }
        catch (InvalidOperationException exception) { return exception.GetType().Name; }
    }

    public static object LookupStillUsed()
    {
        var field = typeof(Target).GetField(nameof(Target.Value))!;
        return field.Name + ":" + field.GetValue(new Target());
    }

    public static int InstanceFieldInitialization()
    {
        LookupEffects = 0;
        object target = default(InitializedValue);
        var field = typeof(InitializedValue).GetField("Value")!;
        field.SetValue(target, 42);
        return LookupEffects * 100 + (int)field.GetValue(target)!;
    }

    public static int InstanceFieldReadInitialization()
    {
        LookupEffects = 0;
        _ = typeof(InitializedValue).GetField("Value")!.GetValue((object)default(InitializedValue));
        return LookupEffects;
    }

    public static string FailedInstanceField()
    {
        try { return typeof(FailedValue).GetField("Value")!.GetValue((object)default(FailedValue))!.ToString()!; }
        catch (Exception e) { return e.GetType().Name + ":" + e.InnerException?.GetType().Name; }
    }

    public static string FailedStaticFieldRead()
    {
        try { return typeof(FailedInitialization).GetField("Value")!.GetValue(null)!.ToString()!; }
        catch (Exception e) { return e.GetType().Name + ":" + e.InnerException?.GetType().Name; }
    }

    public static string FailedStaticFieldStore()
    {
        try { typeof(FailedInitialization).GetField("Value")!.SetValue(null, 42); return "no exception"; }
        catch (Exception e) { return e.GetType().Name + ":" + e.InnerException?.GetType().Name; }
    }

    public static object NullStructArgumentInitialization()
    {
        LookupEffects = 0;
        return typeof(Target).GetMethod(nameof(Target.ValueEcho))!.Invoke(null, new object?[] { null })!;
    }

    public static object InitializedField()
        => typeof(Initialized).GetField(nameof(Initialized.Value))!.GetValue(null)!;

    public static object InitializedMethod()
        => typeof(Initialized).GetMethod(nameof(Initialized.Read))!.Invoke(null, null)!;

    public static object ThrowingInitializer()
    {
        try { return typeof(FailedInitialization).GetMethod(nameof(FailedInitialization.Read))!.Invoke(null, null)!; }
        catch (TargetInvocationException exception) { return exception.InnerException!.GetType().Name; }
    }

    public static object? InitializationBox;

    public static object FieldInitializationMutatesArgument()
    {
        object value = 1;
        InitializationBox = value;
        typeof(MutatingFieldInitialization).GetField(nameof(MutatingFieldInitialization.Value))!.SetValue(null, value);
        return MutatingFieldInitialization.Value;
    }

    public static object MethodInitializationMutatesArgument()
    {
        object value = 1;
        InitializationBox = value;
        return typeof(MutatingMethodInitialization).GetMethod(nameof(MutatingMethodInitialization.Echo))!.Invoke(null, new object[] { value })!;
    }

    public static object ThroughHelper() => LookupValue().GetValue(new Target())!;

    public static FieldInfo LookupValue() => typeof(Target).GetField(nameof(Target.Value))!;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Change(object[] arguments) => arguments[0] = 42;

    public enum WideEnum : ulong { Maximum = ulong.MaxValue }

    public interface IConstant { public const int Answer = 42; }

    public sealed class RejectingBinder : Binder
    {
        public override object ChangeType(object value, Type type, CultureInfo? culture) => throw new InvalidOperationException("binder");
        public override FieldInfo BindToField(BindingFlags bindingAttr, FieldInfo[] match, object value, CultureInfo? culture) => throw new NotSupportedException();
        public override MethodBase BindToMethod(BindingFlags bindingAttr, MethodBase[] match, ref object?[] args, ParameterModifier[]? modifiers, CultureInfo? culture, string[]? names, out object state) => throw new NotSupportedException();
        public override void ReorderArgumentArray(ref object?[] args, object state) => throw new NotSupportedException();
        public override MethodBase SelectMethod(BindingFlags bindingAttr, MethodBase[] match, Type[] types, ParameterModifier[]? modifiers) => throw new NotSupportedException();
        public override PropertyInfo SelectProperty(BindingFlags bindingAttr, PropertyInfo[] match, Type? returnType, Type[]? indexes, ParameterModifier[]? modifiers) => throw new NotSupportedException();
    }

    public class LookupBase<T>
    {
        public int Field;
        protected int ProtectedField;
        internal int InternalField = 1;
        private int PrivateField = 1;
        public static int StaticField;
        public T Value { get; set; } = default!;
        public object ChangedType => 42;
        public int PrivateGet { private get; set; }
        public virtual int Virtual() => 1;
        public int Hidden() => 1;
        public void Overload(int value) { }
        public void Overload(string value) { }
        private int Private() => PrivateField;
    }

    public sealed class LookupDerived : LookupBase<string>, IConstant
    {
        public override int Virtual() => 42;
        public new int Hidden() => 42;
        public new string ChangedType => "derived";
        public int FIELD;
        public int Declared;
        public static int \u03C2() => 42;
        public int \u212A;
        public int \u00C5 { get; set; }
    }

    public class Base
    {
        public int Inherited = 9;
        public virtual int Compute() => 1;
        public int Read() => 42;
        public int Hidden() => 1;
    }

    public sealed class Target : Base
    {
        public Target() { }
        public Target(int value)
        {
            if (value < 0) throw new InvalidOperationException("constructor failure");
            Value = value;
        }
        public Target(ref int value, out string text)
        {
            Value = value = 42;
            text = "updated";
        }

        public int Value = 7;
        public long WideValue;
        public double RealValue;
        private static int RefValue = 42;
        private static string RefText = "hello";
        public object? ObjectValue;
        public Counter Counter;
#pragma warning disable CS0414 // Accessed through reflection.
        private int Secret = 42;
#pragma warning restore CS0414
        public const WideEnum EnumConstant = WideEnum.Maximum;
        public const string StringConstant = "hello";
        public const object? NullConstant = null;

        public static string StaticValue = "initial";
        public static readonly int Readonly = 7;
        public int Property { get => Value + 1; set => Value = value; }
        public int this[int index] { get => Value + index; set => Value = value - index; }
        public static int StaticProperty { get; set; }
        public static int FailingProperty => throw new InvalidOperationException("failure");
        public int Add(int value, string text) => value + text.Length;
        public void Set(int value) => Value = value;
        public override int Compute() => 42;
        public new int Hidden() => 42;
        public static int Echo(int value) => value;
        public static object? ObjectEcho(object? value) => value;
        public static object OptionalObject(object? value = null) => value ?? "default";
        public static long LongEcho(long value) => value;
        public static int Constant() => 42;
        public static int? NullableConstant() => 42;
        public static T? Generic<T>() => default;
        public static int Fail() => throw new InvalidOperationException("failure");
        public static double DoubleEcho(double value) => value;
        public static int? NullableEcho(int? value) => value;
        public static ref int RefInt() => ref RefValue;
        public static ref string RefString() => ref RefText;
        public static ref int NullRef() => ref Unsafe.NullRef<int>();
        public static int ValueEcho(InitializedValue value) => LookupEffects;
        public static void Update(ref int value, out string text) { value = 42; text = "updated"; }
        public static void ReplaceNullable(ref int? value) => value = 42;
        public static void ObserveReference(ref int value) { }

        public static void ByRef(ref int value) => value = 42;
        public static int Overload() => 1;
        public static int Overload(int value) => value;
        public static int Overload(string value) => value.Length;
    }

    public struct Counter
    {
        public Counter(int value) => Value = value;
        public int Value;
        public void Increment() => Value++;
    }

    public class GenericTarget<T>
    {
        public U Echo<U>(U value) => value;
        public T? Value;
        public T? Property { get; set; }
        public T? PrivateSetter { get; private set; }
        public static T? StaticValue;
        public static int StaticNumber = 42;
    }

    public sealed class GenericDerived : GenericTarget<int> { }
    public sealed class GenericDerived<T> : GenericTarget<List<T>> { }

    public struct InitializedValue
    {
        static InitializedValue() => LookupEffects++;
        public int Value;
    }

    public struct FailedValue
    {
        static FailedValue() => throw new InvalidOperationException();
        public int Value;
    }

    public class Initialized
    {
        public static int Value;
        static Initialized() => Value = 42;
        public static int Read() => Value;
    }

    public class FailedInitialization
    {
        public static int Value;
        static FailedInitialization() => throw new InvalidOperationException();
        public static int Read() => 42;
    }

    public class MutatingFieldInitialization
    {
        static MutatingFieldInitialization() => Unsafe.Unbox<int>(InitializationBox!) = 42;
        public static int Value;
    }

    public class MutatingMethodInitialization
    {
        static MutatingMethodInitialization() => Unsafe.Unbox<int>(InitializationBox!) = 42;
        public static int Echo(int value) => value;
    }
}

public static unsafe class ReflectionAccessorCases
{
    public static object InstanceDelegates()
    {
        var target = new Target();
        var property = typeof(Target).GetProperty(nameof(Target.Value))!;
        property.SetMethod!.CreateDelegate<Action<int>>(target)(42);
        return property.GetMethod!.CreateDelegate<Func<int>>(target)();
    }

    public static object StaticDelegates()
    {
        var property = typeof(Target).GetProperty(nameof(Target.StaticValue))!;
        property.SetMethod!.CreateDelegate<Action<int>>()(42);
        return property.GetMethod!.CreateDelegate<Func<int>>()();
    }

    public static object InvokeAccessors()
    {
        var target = new Target();
        var property = typeof(Target).GetProperty(nameof(Target.Value))!;
        property.SetMethod!.Invoke(target, new object[] { 42 });
        return property.GetMethod!.Invoke(target, null)!;
    }

    public static object PrivateAccessors()
    {
        var target = new Target();
        var property = typeof(Target).GetProperty("Hidden", BindingFlags.Instance | BindingFlags.NonPublic)!;
        property.SetMethod!.CreateDelegate<Action<int>>(target)(42);
        return property.GetMethod!.CreateDelegate<Func<int>>(target)();
    }

    public static object PrivateSetter()
    {
        var target = new Target();
        var property = typeof(Target).GetProperty(nameof(Target.PrivateSet))!;
        property.SetMethod!.CreateDelegate<Action<int>>(target)(42);
        return property.GetMethod!.CreateDelegate<Func<int>>(target)();
    }

    public static object PrivateGetter()
        => typeof(Target).GetProperty(nameof(Target.PrivateGet))!.GetMethod!.CreateDelegate<Func<int>>(new Target())();

    public static object ParameterlessAccessorMethods()
    {
        var target = new Target();
        var property = typeof(Target).GetProperty(nameof(Target.Value))!;
        property.GetSetMethod()!.CreateDelegate<Action<int>>(target)(42);
        return property.GetGetMethod()!.CreateDelegate<Func<int>>(target)();
    }

    public static object PublicAccessorMethods()
    {
        var target = new Target();
        var property = typeof(Target).GetProperty(nameof(Target.Value))!;
        property.GetSetMethod(false)!.CreateDelegate<Action<int>>(target)(42);
        return property.GetGetMethod(false)!.CreateDelegate<Func<int>>(target)();
    }

    public static object NonPublicAccessorMethods()
    {
        var target = new Target();
        var property = typeof(Target).GetProperty("Hidden", BindingFlags.Instance | BindingFlags.NonPublic)!;
        property.GetSetMethod(true)!.CreateDelegate<Action<int>>(target)(42);
        return property.GetGetMethod(true)!.CreateDelegate<Func<int>>(target)();
    }

    public static object StaticFactory()
        => ((Func<int>)Delegate.CreateDelegate(typeof(Func<int>), typeof(Target).GetProperty(nameof(Target.ReadOnly))!.GetMethod!))();

    public static object GetterFunctionPointer()
    {
        var get = (delegate* managed<int>)typeof(Target).GetProperty(nameof(Target.ReadOnly))!.GetMethod!.MethodHandle.GetFunctionPointer();
        return get();
    }

    public static object SetterFunctionPointer()
    {
        var set = (delegate* managed<int, void>)typeof(Target).GetProperty(nameof(Target.StaticValue))!.SetMethod!.MethodHandle.GetFunctionPointer();
        set(42);
        return Target.StaticValue;
    }

    public static object GenericOwner()
        => typeof(GenericTarget<int>).GetProperty(nameof(GenericTarget<int>.Value))!.GetMethod!
            .CreateDelegate<Func<int>>(new GenericTarget<int>(42))();

    public static T GenericBody<T>(T value)
    {
        var target = new GenericTarget<T>(default!);
        var property = typeof(GenericTarget<T>).GetProperty(nameof(GenericTarget<T>.Value))!;
        property.SetMethod!.CreateDelegate<Action<T>>(target)(value);
        return property.GetMethod!.CreateDelegate<Func<T>>(target)();
    }

    public static object ConstructedGenericOwner()
        => typeof(GenericTarget<>).MakeGenericType(typeof(int)).GetProperty(nameof(GenericTarget<int>.Value))!.GetMethod!
            .CreateDelegate<Func<int>>(new GenericTarget<int>(42))();

    public static object VirtualGetter()
        => typeof(Base).GetProperty(nameof(Base.Value))!.GetMethod!.CreateDelegate<Func<int>>(new Derived())();

    public static int OpenAbstractProperty()
    {
        var callback = typeof(AbstractBase).GetProperty("Value")?.GetMethod?.CreateDelegate<Func<AbstractBase, int>>();
        return callback!.Invoke(new AbstractBar());
    }

    public static object VirtualGetterIdentity()
    {
        var method = typeof(Base).GetProperty(nameof(Base.Value))!.GetMethod!;
        return method == method.CreateDelegate<Func<int>>(new Derived()).Method;
    }

    public static object InterfaceGetter()
        => typeof(IValue).GetProperty(nameof(IValue.Value))!.GetMethod!.CreateDelegate<Func<int>>(new Derived())();

    public static object BoxedAccessors()
    {
        object target = new Counter();
        var property = typeof(Counter).GetProperty(nameof(Counter.Value))!;
        property.SetMethod!.CreateDelegate<Action<int>>(target)(42);
        return property.GetMethod!.CreateDelegate<Func<int>>(target)();
    }

    public static object IndexerAccessors()
    {
        var target = new Target();
        var property = typeof(Target).GetProperty("Item")!;
        property.SetMethod!.CreateDelegate<Action<int, int>>(target)(2, 42);
        return property.GetMethod!.CreateDelegate<Func<int, int>>(target)(2);
    }

    public static object ThrowingGetter()
    {
        try { return typeof(Target).GetProperty(nameof(Target.Failure))!.GetMethod!.Invoke(null, null)!; }
        catch (TargetInvocationException error) { return error.InnerException!.GetType().Name; }
    }

    public static object MetadataObserved()
    {
        var method = typeof(Target).GetProperty(nameof(Target.ReadOnly))!.GetMethod!;
        return method.Name + ":" + method.CreateDelegate<Func<int>>()();
    }

    public static object MissingGetter()
    {
        try { return typeof(Target).GetProperty(nameof(Target.WriteOnly))!.GetMethod!.CreateDelegate<Func<int>>()(); }
        catch (NullReferenceException error) { return error.GetType().Name; }
    }

    public static object MissingSetter()
    {
        try { typeof(Target).GetProperty(nameof(Target.ReadOnly))!.SetMethod!.CreateDelegate<Action<int>>()(42); }
        catch (NullReferenceException error) { return error.GetType().Name; }
        return "unexpected";
    }

    public static object PrivateGetterExcluded()
    {
        try { return typeof(Target).GetProperty(nameof(Target.PrivateGet))!.GetGetMethod()!.CreateDelegate<Func<int>>(new Target())(); }
        catch (NullReferenceException error) { return error.GetType().Name; }
    }

    public static object PrivateSetterExcluded()
    {
        try { typeof(Target).GetProperty(nameof(Target.PrivateSet))!.GetSetMethod(false)!.CreateDelegate<Action<int>>(new Target())(42); }
        catch (NullReferenceException error) { return error.GetType().Name; }
        return "unexpected";
    }

    public static object PublicGetterWithRuntimeFlag(bool nonPublic)
        => typeof(Target).GetProperty(nameof(Target.Value))!.GetGetMethod(nonPublic)!.CreateDelegate<Func<int>>(new Target())();

    public static object VisibilityFlagEffects()
    {
        var target = new Target();
        return typeof(Target).GetProperty(nameof(Target.Value))!.GetGetMethod(SetValue(target))!.CreateDelegate<Func<int>>(target)();
    }

    private static bool SetValue(Target target)
    {
        target.Value = 42;
        return false;
    }

    public static object PrivateGetterWithRuntimeFlag(bool nonPublic)
    {
        try { return typeof(Target).GetProperty(nameof(Target.PrivateGet))!.GetGetMethod(nonPublic)!.CreateDelegate<Func<int>>(new Target())(); }
        catch (NullReferenceException error) { return error.GetType().Name; }
    }

    public static object UnknownProperty(PropertyInfo property)
        => property.GetMethod!.CreateDelegate<Func<int>>("hello")();

    public sealed class Target
    {
        private int _value = 40;
        public int Value { get => _value; set => _value = value; }
        private int Hidden { get => _value; set => _value = value; }
        public int PrivateSet { get => _value; private set => _value = value; }
        public int PrivateGet { private get => _value; set => _value = value; }
        public static int StaticValue { get; set; }
        public static int ReadOnly => 42;
        public static int WriteOnly { set => StaticValue = value; }
        public static int Failure => throw new InvalidOperationException();
        public int this[int index] { get => _value + index; set => _value = value - index; }
    }

    public class GenericTarget<T>(T value) { public T Value { get; set; } = value; }
    public interface IValue { int Value { get; } }
    public class Base : IValue { public virtual int Value => 1; }
    public sealed class Derived : Base { public override int Value => 42; }
    public abstract class AbstractBase { public abstract int Value { get; } }
    public class AbstractFoo : AbstractBase { public override int Value => 1; }
    public class AbstractBar : AbstractFoo { public override int Value => 2; }
    public struct Counter { public int Value { get; set; } }
}
