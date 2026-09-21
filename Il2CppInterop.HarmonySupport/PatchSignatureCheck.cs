using System.Collections.Concurrent;
using System.Reflection;
using HarmonyLib;
using Il2CppInterop.Common;
using Il2CppInterop.Runtime.InteropTypes;
using Microsoft.Extensions.Logging;

namespace Il2CppInterop.HarmonySupport;

// A patch parameter typed as the managed class of the same name takes the il2cpp object as if it were one. Nothing
// checks it, so the mismatch surfaces later as a cast failure or as memory read through the wrong fields.
internal static class PatchSignatureCheck
{
    private static readonly ConcurrentDictionary<MethodInfo, bool> Reported = new();

    public static void Report(MethodBase original, PatchInfo patches)
    {
        if (patches == null) return;

        foreach (var patch in Concat(patches.prefixes, patches.postfixes, patches.finalizers))
            if (patch.PatchMethod != null && Reported.TryAdd(patch.PatchMethod, true))
                Report(original, patch.PatchMethod);
    }

    private static IEnumerable<Patch> Concat(params Patch[][] sets)
    {
        foreach (var set in sets)
        foreach (var patch in set ?? Array.Empty<Patch>())
            yield return patch;
    }

    private static void Report(MethodBase original, MethodInfo patchMethod)
    {
        foreach (var parameter in patchMethod.GetParameters())
        {
            if (!TryResolve(original, parameter.Name, out var expected)) continue;
            if (IsCompatible(parameter.ParameterType, expected)) continue;

            Logger.Instance.LogWarning(
                "{Patch} takes {Parameter} as {Declared} where {Original} has {Expected}, il2cpp passes the object either way and the patch will read it as the wrong type",
                Describe(patchMethod), parameter.Name, Describe(parameter.ParameterType), Describe(original), Describe(expected));
        }
    }

    private static bool TryResolve(MethodBase original, string name, out Type expected)
    {
        expected = typeof(void);
        if (name == null) return false;

        if (name == "__instance")
        {
            expected = original.DeclaringType!;
            return expected != null;
        }

        if (name == "__result")
        {
            expected = AccessTools.GetReturnedType(original);
            return expected != typeof(void);
        }

        var parameters = original.GetParameters();
        if (name.StartsWith("__", StringComparison.Ordinal))
        {
            // __0, __1 name a parameter by position, every other __ name is Harmony's own
            if (!int.TryParse(name.Substring(2), out var index) || index >= parameters.Length) return false;
            expected = parameters[index].ParameterType;
            return true;
        }

        foreach (var parameter in parameters)
            if (parameter.Name == name)
            {
                expected = parameter.ParameterType;
                return true;
            }

        return false;
    }

    private static bool IsCompatible(Type declared, Type expected)
    {
        if (declared.IsByRef) declared = declared.GetElementType()!;
        if (expected.IsByRef) expected = expected.GetElementType()!;

        if (declared == expected) return true;
        // the object stays a pointer either way, and anything can be handed back as Il2CppSystem.Object
        if (declared == typeof(object) || declared == typeof(IntPtr)) return true;
        if (declared.IsAssignableFrom(expected) || expected.IsAssignableFrom(declared)) return true;
        // a blittable stand in for a value type reads the same bytes, which is the patch author's business
        if (declared.IsValueType && expected.IsValueType) return true;

        return false;
    }

    private static string Describe(MethodBase method) => $"{method.DeclaringType?.FullName}.{method.Name}";

    private static string Describe(Type type) =>
        typeof(Il2CppObjectBase).IsAssignableFrom(type) || typeof(IIl2CppObjectBase).IsAssignableFrom(type)
            ? type.FullName!
            : $"{type.FullName} (not an il2cpp type)";
}
