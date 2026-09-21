using System.Reflection;
using HarmonyLib;
using Il2CppInterop.Common;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Runtime;
using Il2CppInterop.Runtime.Runtime.VersionSpecific.MethodInfo;
using Microsoft.Extensions.Logging;

namespace Il2CppInterop.HarmonySupport;

// il2cpp folds methods with identical code into one native body, and a detour on that body receives the calls of
// every method sharing it, with their own arguments and callers expecting their own results.
internal static unsafe class SharedBodyCheck
{
    private static readonly object Lock = new();
    private static Dictionary<IntPtr, int> _bodyCounts;

    public static void Report(MethodBase original, IntPtr methodPointer)
    {
        if (methodPointer == IntPtr.Zero) return;

        int count;
        lock (Lock)
        {
            _bodyCounts ??= CountBodies();
            _bodyCounts.TryGetValue(methodPointer, out count);
        }

        if (count <= 1) return;

        Logger.Instance.LogWarning(
            "{Method} shares its native body with {Count} other methods and the detour runs for all of them, the body belongs to {Examples}. Patch a method with its own body instead",
            original.FullDescription(), count - 1, string.Join(", ", Sharers(methodPointer, 5)));
    }

    private static Dictionary<IntPtr, int> CountBodies()
    {
        var counts = new Dictionary<IntPtr, int>();
        foreach (var method in AllMethods())
        {
            var pointer = BodyOf(method);
            if (pointer == IntPtr.Zero) continue;
            counts.TryGetValue(pointer, out var count);
            counts[pointer] = count + 1;
        }

        return counts;
    }

    private static IEnumerable<string> Sharers(IntPtr methodPointer, int limit)
    {
        var found = 0;
        foreach (var method in AllMethods())
        {
            if (BodyOf(method) != methodPointer) continue;

            var klass = IL2CPP.il2cpp_method_get_class(method);
            yield return $"{IL2CPP.il2cpp_class_get_name_(klass)}.{IL2CPP.il2cpp_method_get_name_(method)}";
            if (++found >= limit) yield break;
        }
    }

    private static IntPtr BodyOf(IntPtr method) => UnityVersionHandler.Wrap((Il2CppMethodInfo*)method).MethodPointer;

    private static IEnumerable<IntPtr> AllMethods()
    {
        foreach (var image in IL2CPP.GetIl2CppImages())
        {
            var classCount = IL2CPP.il2cpp_image_get_class_count(image);
            for (uint i = 0; i < classCount; i++)
            {
                var klass = IL2CPP.il2cpp_image_get_class(image, i);
                if (klass == IntPtr.Zero) continue;

                var iter = IntPtr.Zero;
                IntPtr method;
                while ((method = IL2CPP.il2cpp_class_get_methods(klass, ref iter)) != IntPtr.Zero)
                    yield return method;
            }
        }
    }
}
