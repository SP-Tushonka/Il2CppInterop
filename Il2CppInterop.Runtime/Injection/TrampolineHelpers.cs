using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime.InteropTypes;

namespace Il2CppInterop.Runtime.Injection;

internal static class TrampolineHelpers
{
    private static AssemblyBuilder _fixedStructAssembly;
    private static ModuleBuilder _fixedStructModuleBuilder;
    private static readonly Dictionary<int, Type> _fixedStructCache = new();

    private static Type GetFixedSizeStructType(int size)
    {
        // Trampolines are generated on whichever thread first needs one, and defining the same type twice throws
        lock (_fixedStructCache)
        {
            if (_fixedStructCache.TryGetValue(size, out var result))
            {
                return result;
            }

            _fixedStructAssembly ??= AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("FixedSizeStructAssembly"), AssemblyBuilderAccess.Run);
            _fixedStructModuleBuilder ??= _fixedStructAssembly.DefineDynamicModule("FixedSizeStructAssembly");

            var tb = _fixedStructModuleBuilder.DefineType($"IL2CPPDetour_FixedSizeStruct_{size}b", TypeAttributes.ExplicitLayout, typeof(ValueType), size);

            var type = tb.CreateType();
            return _fixedStructCache[size] = type;
        }
    }

    internal static int ValueSize(Type managedType)
    {
        uint align = 0;
        return IL2CPP.il2cpp_class_value_size(Il2CppClassPointerStore.GetNativeClassPointer(managedType), ref align);
    }

    // x86 passes every struct on the stack. Win64 passes structs of 1, 2, 4 or 8 bytes in a register and the
    // rest through a pointer, other 64 bit ABIs stay pointers.
    internal static bool IsPassedByValue(Type managedType)
    {
        if (!managedType.IsSubclassOf(typeof(Il2CppSystem.ValueType)))
            return false;
        if (!Environment.Is64BitProcess)
            return true;
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return false;
        var size = ValueSize(managedType);
        return size == 1 || size == 2 || size == 4 || size == 8;
    }

    // A struct wrapper not returned in a register comes back through a caller allocated buffer passed as the first argument
    internal static bool NeedsReturnBuffer(Type returnType)
    {
        return returnType.IsSubclassOf(typeof(Il2CppSystem.ValueType)) && !IsPassedByValue(returnType);
    }

    // A byref to a boxed struct wrapper points at the struct's bytes, not at an object. The wrapper needs a box of
    // its own, so the bytes are copied into one on the way in and back out after the call.
    internal static bool IsBoxedStructByRef(Type managedType)
    {
        return managedType.IsByRef && managedType.GetElementType()!.IsSubclassOf(typeof(Il2CppSystem.ValueType));
    }

    internal static IntPtr BoxStructAt(IntPtr klass, IntPtr data)
    {
        var box = IL2CPP.il2cpp_object_new(klass);
        IL2CPP.CopyValue(box, IL2CPP.il2cpp_object_unbox(box), data, klass);
        return box;
    }

    // The byref can point into a heap object, so references in the struct go through the write barrier
    internal static void CopyBoxedStructTo(Il2CppObjectBase box, IntPtr data)
    {
        if (box == null) return;

        var pointer = box.Pointer;
        IL2CPP.CopyValue(IntPtr.Zero, data, IL2CPP.il2cpp_object_unbox(pointer), IL2CPP.il2cpp_object_get_class(pointer));
    }

    internal static readonly MethodInfo BoxStructAtMethod = typeof(TrampolineHelpers).GetMethod(nameof(BoxStructAt), BindingFlags.Static | BindingFlags.NonPublic)!;
    internal static readonly MethodInfo CopyBoxedStructToMethod = typeof(TrampolineHelpers).GetMethod(nameof(CopyBoxedStructTo), BindingFlags.Static | BindingFlags.NonPublic)!;

    internal static Type NativeType(this Type managedType)
    {
        if (managedType.IsByRef)
        {
            var directType = managedType.GetElementType();

            // bool is byte in Il2Cpp, but int in CLR => force size to be correct
            if (directType == typeof(bool))
            {
                return typeof(byte).MakeByRefType();
            }

            if (directType == typeof(string) || directType.IsSubclassOf(typeof(Il2CppObjectBase)) || directType.IsInterface)
            {
                return typeof(IntPtr*);
            }
        }
        else if (managedType.IsSubclassOf(typeof(Il2CppSystem.ValueType)) && IsPassedByValue(managedType))
        {
            return GetFixedSizeStructType(ValueSize(managedType));
        }
        else if (managedType == typeof(string) || managedType.IsSubclassOf(typeof(Il2CppObjectBase)) || managedType.IsInterface) // General reference type
        {
            return typeof(IntPtr);
        }
        else if (managedType == typeof(bool))
        {
            // bool is byte in Il2Cpp, but int in CLR => force size to be correct
            return typeof(byte);
        }

        return managedType;
    }
}
