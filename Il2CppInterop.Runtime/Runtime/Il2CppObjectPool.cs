using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Il2CppInterop.Runtime.InteropTypes;
using Object = Il2CppSystem.Object;

namespace Il2CppInterop.Runtime.Runtime;

public static class Il2CppObjectPool
{
    internal static bool DisableCaching { get; set; }

    private static readonly ConcurrentDictionary<IntPtr, WeakReference<Il2CppObjectBase>> s_cache = new();

    // A finalizer can run after a newer wrapper took the slot, so it only removes the entry it put there
    internal static void Remove(IntPtr ptr, WeakReference<Il2CppObjectBase> entry)
    {
        s_cache.TryRemove(KeyValuePair.Create(ptr, entry));
    }

    public static T Get<T>(IntPtr ptr)
    {
        if (ptr == IntPtr.Zero) return default;

        var ownClass = IL2CPP.il2cpp_object_get_class(ptr);
        if (RuntimeSpecificsStore.IsInjected(ownClass))
        {
            var monoObject = ClassInjectorBase.GetMonoObjectFromIl2CppPointer(ptr);
            if (monoObject is T monoObjectT) return monoObjectT;
        }

        if (DisableCaching) return Create<T>(ptr, ownClass, out _);

        if (s_cache.TryGetValue(ptr, out var reference) && reference.TryGetTarget(out var cachedObject))
        {
            if (cachedObject is T cachedObjectT) return cachedObjectT;
            var replacement = Create<T>(ptr, ownClass, out var mostDerived);
            // A class wrapper is worth more in the cache than an interface proxy of the same object
            if (!mostDerived && typeof(T).IsInterface) return replacement;
            return Cache(ptr, replacement);
        }

        return Cache(ptr, Create<T>(ptr, ownClass, out _));
    }

    // For wrappers the pool cannot construct itself, such as arrays of a generic element type. A Get overload would
    // make every by name lookup of Get ambiguous, and Harmony support and the trampolines find it by name.
    internal static T GetOrCreate<T>(IntPtr ptr, Func<IntPtr, T> create) where T : Il2CppObjectBase
    {
        if (DisableCaching) return create(ptr);

        if (s_cache.TryGetValue(ptr, out var reference) && reference.TryGetTarget(out var cachedObject))
        {
            if (cachedObject is T cachedObjectT) return cachedObjectT;
        }

        return Cache(ptr, create(ptr));
    }

    // The wrapper of the object's own class when the generated assemblies have one that fits T, so is and as see the
    // real type. T itself otherwise, which is the interface proxy when T is an interface.
    internal static T Create<T>(IntPtr ptr, IntPtr ownClass, out bool mostDerived)
    {
        var type = Il2CppWrapperTypes.Resolve(ownClass);
        mostDerived = type != null && type != typeof(T) && typeof(T).IsAssignableFrom(type);
        return mostDerived ? (T)(object)Il2CppWrapperTypes.Create(type!, ptr) : Il2CppObjectBase.InitializerStore<T>.Initializer(ptr);
    }

    private static T Cache<T>(IntPtr ptr, T newObj)
    {
        var il2CppObjectBase = Unsafe.As<T, Il2CppObjectBase>(ref newObj);
        var entry = new WeakReference<Il2CppObjectBase>(il2CppObjectBase);
        s_cache[ptr] = entry;
        il2CppObjectBase.poolEntry = entry;
        return newObj;
    }
}
