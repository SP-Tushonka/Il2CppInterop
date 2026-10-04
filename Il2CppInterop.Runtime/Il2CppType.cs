using System.Collections.Concurrent;
using Il2CppInterop.Runtime.Runtime;
using Il2CppSystem;
using ArgumentException = System.ArgumentException;
using IntPtr = System.IntPtr;

namespace Il2CppInterop.Runtime;

public static class Il2CppType
{
    // il2cpp keeps one Type object per class, and asking for it is a runtime_invoke and a pool lookup
    private static readonly ConcurrentDictionary<IntPtr, Type> ourTypeObjects = new();

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

        if (ourTypeObjects.TryGetValue(classPointer, out var cached))
            return cached;

        var il2CppType = IL2CPP.il2cpp_class_get_type(classPointer);
        if (il2CppType == IntPtr.Zero)
        {
            if (throwOnFailure)
                throw new ArgumentException($"{typeName} does not have a corresponding IL2CPP type pointer");
            return null;
        }

        var type = Type.internal_from_handle(il2CppType);
        if (type != null)
            ourTypeObjects[classPointer] = type;
        return type;
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

    /// <summary>
    /// The generated wrapper of an il2cpp type, the reverse of <see cref="From(System.Type)"/>. Null for generic
    /// instantiations, arrays and types no loaded interop assembly declares.
    /// </summary>
    public static System.Type? ToManaged(Type? type)
    {
        if (type == null)
            return null;

        var klass = IL2CPP.il2cpp_class_from_system_type(IL2CPP.Il2CppObjectBaseToPtrNotNull(type));
        return klass == IntPtr.Zero ? null : Il2CppWrapperTypes.ResolveAny(klass);
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
}
