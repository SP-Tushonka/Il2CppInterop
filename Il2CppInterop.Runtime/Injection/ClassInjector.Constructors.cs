using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

namespace Il2CppInterop.Runtime.Injection;

public static unsafe partial class ClassInjector
{
    [ThreadStatic] private static Il2CppObjectBase? s_constructing;

    // True while the il2cpp base constructor of this object is running. What that constructor calls reaches the
    // injected overrides before the managed constructor has assigned the fields that follow InvokeBaseConstructor.
    public static bool IsConstructing(Il2CppObjectBase instance) => ReferenceEquals(s_constructing, instance);

    internal static Type? ConstructingType => s_constructing?.GetType();

    // Runs a base class il2cpp constructor on the object DerivedConstructorPointer allocated. Call it after
    // DerivedConstructorBody, the base constructor may call virtual methods the injected class overrides.
    public static void InvokeBaseConstructor(Il2CppObjectBase instance, params object?[] arguments)
    {
        var baseType = NativeBaseType(instance.GetType());
        if (baseType == null)
            throw new ArgumentException($"{instance.GetType()} has no il2cpp base class");

        InvokeConstructor(instance, Il2CppClassPointerStore.GetNativeClassPointer(baseType), arguments);
    }

    public static void InvokeBaseConstructor<TBase>(Il2CppObjectBase instance, params object?[] arguments) where TBase : Il2CppObjectBase
    {
        InvokeConstructor(instance, Il2CppClassPointerStore<TBase>.NativeClassPtr, arguments);
    }

    private static void InvokeConstructor(Il2CppObjectBase instance, IntPtr klass, object?[] arguments)
    {
        if (klass == IntPtr.Zero)
            throw new ArgumentException("The base class has no il2cpp class pointer");

        for (var i = 0; i < arguments.Length; i++)
            arguments[i] = WrapManagedArray(arguments[i]);

        var constructor = FindConstructor(klass, arguments);
        if (constructor == IntPtr.Zero)
            throw new MissingMethodException($"{IL2CPP.il2cpp_class_get_name_(klass)} has no constructor taking ({string.Join(", ", Array.ConvertAll(arguments, DescribeArgument))})");

        var pinned = new List<GCHandle>();
        var nativeArguments = stackalloc IntPtr[Math.Max(arguments.Length, 1)];
        var outer = s_constructing;
        s_constructing = instance;
        try
        {
            for (var i = 0; i < arguments.Length; i++)
                nativeArguments[i] = MarshalArgument(arguments[i], pinned);

            var exception = IntPtr.Zero;
            IL2CPP.il2cpp_runtime_invoke(constructor, instance.Pointer, (void**)nativeArguments, ref exception);
            Il2CppException.RaiseExceptionIfNecessary(exception);
        }
        finally
        {
            s_constructing = outer;
            foreach (var handle in pinned)
                handle.Free();
        }
    }

    // A managed array has no il2cpp class, so it matches no constructor. The il2cpp wrapper is what the caller
    // meant by it, and building it here saves every mod writing the conversion.
    private static object? WrapManagedArray(object? argument)
    {
        if (argument is not Array array) return argument;

        var element = argument.GetType().GetElementType()!;
        if (element == typeof(string)) return new Il2CppStringArray((string[])array);

        Type wrapper;
        if (element.IsPrimitive || element.IsEnum)
            wrapper = typeof(Il2CppStructArray<>);
        else if (typeof(Il2CppObjectBase).IsAssignableFrom(element))
            wrapper = typeof(Il2CppReferenceArray<>);
        else
            return argument;

        return Activator.CreateInstance(wrapper.MakeGenericType(element), new object[] { array });
    }

    private static string DescribeArgument(object? argument)
    {
        if (argument == null) return "null";

        var type = argument.GetType();
        if (typeof(Il2CppObjectBase).IsAssignableFrom(type) || type == typeof(string)) return type.Name;

        var suggestion = SuggestIl2CppType(type);
        return suggestion != null ? $"{type.Name} (il2cpp wants {suggestion})" : type.Name;
    }

    private static readonly ConcurrentDictionary<IntPtr, (IntPtr Method, IntPtr[] Parameters)[]> Constructors = new();

    private static IntPtr FindConstructor(IntPtr klass, object?[] arguments)
    {
        foreach (var (method, parameters) in Constructors.GetOrAdd(klass, ListConstructors))
        {
            if (parameters.Length != arguments.Length)
                continue;

            var matches = true;
            for (var i = 0; i < arguments.Length && matches; i++)
                matches = ArgumentMatches(parameters[i], arguments[i]);
            if (matches)
                return method;
        }

        return IntPtr.Zero;
    }

    private static (IntPtr Method, IntPtr[] Parameters)[] ListConstructors(IntPtr klass)
    {
        var constructors = new List<(IntPtr, IntPtr[])>();
        var iterator = IntPtr.Zero;
        IntPtr method;
        while ((method = IL2CPP.il2cpp_class_get_methods(klass, ref iterator)) != IntPtr.Zero)
        {
            if (IL2CPP.il2cpp_method_get_name_(method) != ".ctor")
                continue;

            var parameters = new IntPtr[IL2CPP.il2cpp_method_get_param_count(method)];
            for (var i = 0; i < parameters.Length; i++)
                parameters[i] = IL2CPP.il2cpp_class_from_type(IL2CPP.il2cpp_method_get_param(method, (uint)i));
            constructors.Add((method, parameters));
        }

        return constructors.ToArray();
    }

    private static bool ArgumentMatches(IntPtr parameterClass, object? argument)
    {
        if (argument == null)
            return !IL2CPP.il2cpp_class_is_valuetype(parameterClass);

        var argumentClass = argument switch
        {
            Il2CppObjectBase wrapped => IL2CPP.il2cpp_object_get_class(wrapped.Pointer),
            string => Il2CppClassPointerStore<string>.NativeClassPtr,
            _ => Il2CppClassPointerStore.GetNativeClassPointer(argument.GetType()),
        };
        return argumentClass != IntPtr.Zero && IL2CPP.il2cpp_class_is_assignable_from(parameterClass, argumentClass);
    }

    // il2cpp_runtime_invoke wants object pointers for reference types and pointers to the raw data for value types
    private static IntPtr MarshalArgument(object? argument, List<GCHandle> pinned)
    {
        switch (argument)
        {
            case null:
                return IntPtr.Zero;
            case string text:
                return IL2CPP.ManagedStringToIl2Cpp(text);
            case Il2CppObjectBase wrapped:
                var pointer = wrapped.Pointer;
                return IL2CPP.il2cpp_class_is_valuetype(IL2CPP.il2cpp_object_get_class(pointer)) ? IL2CPP.il2cpp_object_unbox(pointer) : pointer;
            default:
                if (!argument.GetType().IsValueType)
                    throw new ArgumentException($"{argument.GetType()} cannot be passed to an il2cpp constructor");

                // bool and char cannot be pinned, il2cpp reads them as a one and a two byte value
                object value = argument switch
                {
                    bool flag => (byte)(flag ? 1 : 0),
                    char character => (ushort)character,
                    _ => argument
                };

                var handle = GCHandle.Alloc(value, GCHandleType.Pinned);
                pinned.Add(handle);
                return handle.AddrOfPinnedObject();
        }
    }
}
