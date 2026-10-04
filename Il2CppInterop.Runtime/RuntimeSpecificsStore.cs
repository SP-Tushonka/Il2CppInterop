using System;
using System.Collections.Generic;
using System.Threading;

namespace Il2CppInterop.Runtime;

public static class RuntimeSpecificsStore
{
    private static readonly Lock WriteLock = new();

    // Every wrapped object asks this while classes are only injected as mods load, so readers get an immutable set
    // and a write publishes a new copy. The read lock this replaced cost about 10 ns per wrapped object.
    private static volatile HashSet<IntPtr> InjectedClasses = new();

    public static bool IsInjected(IntPtr nativeClass)
    {
        return InjectedClasses.Contains(nativeClass);
    }

    public static void SetClassInfo(IntPtr nativeClass, bool wasInjected)
    {
        lock (WriteLock)
        {
            var copy = new HashSet<IntPtr>(InjectedClasses);
            if (wasInjected)
                copy.Add(nativeClass);
            else
                copy.Remove(nativeClass);
            InjectedClasses = copy;
        }
    }
}
