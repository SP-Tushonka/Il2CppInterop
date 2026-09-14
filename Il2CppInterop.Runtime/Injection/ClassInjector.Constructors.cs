using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime.InteropTypes;

namespace Il2CppInterop.Runtime.Injection;

public static unsafe partial class ClassInjector
{
    // Runs a base class il2cpp constructor on the object DerivedConstructorPointer allocated. Call it after
    // DerivedConstructorBody, the base constructor may call virtual methods the injected class overrides.
    public static void InvokeBaseConstructor(Il2CppObjectBase instance, params object?[] arguments)
    {
        var baseType = instance.GetType().BaseType;
        while (baseType != null && IsManagedTypeInjected(baseType))
            baseType = baseType.BaseType;
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

        var constructor = FindConstructor(klass, arguments);
        if (constructor == IntPtr.Zero)
            throw new MissingMethodException($"{IL2CPP.il2cpp_class_get_name_(klass)} has no constructor taking ({string.Join(", ", Array.ConvertAll(arguments, argument => argument?.GetType().Name ?? "null"))})");

        var pinned = new List<GCHandle>();
        var nativeArguments = stackalloc IntPtr[Math.Max(arguments.Length, 1)];
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
            foreach (var handle in pinned)
                handle.Free();
        }
    }

    private static IntPtr FindConstructor(IntPtr klass, object?[] arguments)
    {
        var iterator = IntPtr.Zero;
        IntPtr method;
        while ((method = IL2CPP.il2cpp_class_get_methods(klass, ref iterator)) != IntPtr.Zero)
        {
            if (IL2CPP.il2cpp_method_get_name_(method) != ".ctor" || IL2CPP.il2cpp_method_get_param_count(method) != arguments.Length)
                continue;

            var matches = true;
            for (var i = 0; i < arguments.Length && matches; i++)
                matches = ArgumentMatches(IL2CPP.il2cpp_class_from_type(IL2CPP.il2cpp_method_get_param(method, (uint)i)), arguments[i]);
            if (matches)
                return method;
        }

        return IntPtr.Zero;
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
                var handle = GCHandle.Alloc(argument, GCHandleType.Pinned);
                pinned.Add(handle);
                return handle.AddrOfPinnedObject();
        }
    }
}
