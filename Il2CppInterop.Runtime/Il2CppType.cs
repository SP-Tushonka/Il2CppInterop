using Il2CppInterop.Runtime.Runtime;
using Il2CppSystem;
using ArgumentException = System.ArgumentException;
using IntPtr = System.IntPtr;

namespace Il2CppInterop.Runtime;

public static class Il2CppType
{
    public static Type TypeFromPointer(IntPtr classPointer, string typeName = "<unknown type>")
    {
        return TypeFromPointerInternal(classPointer, typeName, true);
    }

    private static Type? TypeFromPointerInternal(IntPtr classPointer, string typeName, bool throwOnFailure)
    {
        if (classPointer == IntPtr.Zero)
        {
            if (throwOnFailure)
                throw new ArgumentException($"{typeName} does not have a corresponding IL2CPP class pointer");
            return null;
        }

        var il2CppType = IL2CPP.il2cpp_class_get_type(classPointer);
        if (il2CppType == IntPtr.Zero)
        {
            if (throwOnFailure)
                throw new ArgumentException($"{typeName} does not have a corresponding IL2CPP type pointer");
            return null;
        }

        return Type.internal_from_handle(il2CppType);
    }

    public static Type From(System.Type type)
    {
        return From(type, true);
    }

    public static Type From(System.Type type, bool throwOnFailure)
    {
        var pointer = Il2CppClassPointerStore.GetNativeClassPointer(type);
        return TypeFromPointerInternal(pointer, type.Name, throwOnFailure);
    }

    public static Type Of<T>()
    {
        return Of<T>(true);
    }

    public static Type Of<T>(bool throwOnFailure)
    {
        var classPointer = Il2CppClassPointerStore<T>.NativeClassPtr;
        return TypeFromPointerInternal(classPointer, typeof(T).Name, throwOnFailure);
    }

    // The generated wrapper type of an il2cpp type. Null for generic instantiations, arrays and types without a wrapper.
    public static System.Type? ToManaged(Type type)
    {
        if (type is null)
            return null;

        var classPointer = IL2CPP.il2cpp_class_from_system_type(type.Pointer);
        return classPointer == IntPtr.Zero ? null : Il2CppWrapperTypes.Resolve(classPointer);
    }
}
