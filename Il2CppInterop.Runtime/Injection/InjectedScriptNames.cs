using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Il2CppInterop.Common;
using Microsoft.Extensions.Logging;

namespace Il2CppInterop.Runtime.Injection;

/// <summary>
///     Gives injected classes the assembly name of their first il2cpp ancestor when the engine asks. A MonoScript keeps
///     only assembly, namespace and class name and resolves the class through the engine's own assembly list, which
///     has no entry for the injected image. Unresolved, a script never matches its class again, so RequireComponent
///     cannot add an injected component and every AddComponent of one creates a new MonoScript
/// </summary>
internal static unsafe class InjectedScriptNames
{
    private const string ExportName = "il2cpp_class_get_assemblyname";

    // HideInHierarchy, DontSaveInEditor, NotEditable, DontSaveInBuild and DontUnloadUnusedAsset
    private const int HideAndDontSave = 61;

    private static readonly ConcurrentDictionary<IntPtr, IntPtr> s_Names = new();
    private static readonly ConcurrentDictionary<Type, bool> s_KeptScripts = new();
    private static readonly object s_PatchLock = new();
    private static bool s_PatchAttempted;
    private static IntPtr s_Original;

    /// <summary>
    ///     True once the engine resolves scripts of injected classes
    /// </summary>
    internal static bool Active => s_Original != IntPtr.Zero;

    /// <summary>
    ///     Make sure the engine has a script for an injected component and keeps it. RequireComponent only finds existing
    ///     scripts and the engine creates one on the first add, so the component is added once to a hidden inactive object
    ///     that stays alive. An inactive object runs no messages
    /// </summary>
    /// <param name="type">Injected component type</param>
    internal static void KeepScript(Type type)
    {
        if (!Active || !s_KeptScripts.TryAdd(type, true))
            return;

        try
        {
            var gameObjectType = Type.GetType("UnityEngine.GameObject, UnityEngine.CoreModule", true)!;
            var objectType = Type.GetType("UnityEngine.Object, UnityEngine.CoreModule", true)!;
            var hideFlagsType = Type.GetType("UnityEngine.HideFlags, UnityEngine.CoreModule", true)!;
            var gameObject = Activator.CreateInstance(gameObjectType, "Il2CppInterop script of " + type.FullName)!;
            gameObjectType.GetMethod("SetActive", [typeof(bool)])!.Invoke(gameObject, [false]);
            objectType.GetProperty("hideFlags")!.SetValue(gameObject, Enum.ToObject(hideFlagsType, HideAndDontSave));
            gameObjectType.GetMethod("AddComponent", [typeof(Il2CppSystem.Type)])!.Invoke(gameObject, [Il2CppType.From(type)]);
        }
        catch (Exception exception)
        {
            s_KeptScripts.TryRemove(type, out _);
            Logger.Instance.LogWarning("Could not create the engine script of {Type}, RequireComponent will only add it once it was added elsewhere: {Exception}",
                type, (exception as System.Reflection.TargetInvocationException)?.InnerException ?? exception);
        }
    }

    /// <summary>
    ///     Point the engine's assembly name import at the handler below, once. It has to be in place before the engine
    ///     first sees an injected class because script class ids are built from the name
    /// </summary>
    internal static void EnsurePatched()
    {
        lock (s_PatchLock)
        {
            if (s_PatchAttempted)
                return;
            s_PatchAttempted = true;

            try
            {
                if (EngineImports.TryFindSlot(ExportName, out var slot, out var export))
                {
                    s_Original = export;
                    EngineImports.Swap(ExportName, slot, export, (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr>)&GetAssemblyName);
                    return;
                }
            }
            catch (Exception exception)
            {
                Logger.Instance.LogError("Could not hook the engine's il2cpp assembly name import: {Exception}", exception);
            }

            Logger.Instance.LogWarning("The engine cannot resolve scripts of injected classes, RequireComponent will not add them");
        }
    }

    private static IntPtr Forward(IntPtr a0, IntPtr a1, IntPtr a2, IntPtr a3) =>
        ((delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr>)s_Original)(a0, a1, a2, a3);

    [UnmanagedCallersOnly]
    private static IntPtr GetAssemblyName(IntPtr klass, IntPtr a1, IntPtr a2, IntPtr a3)
    {
        if (klass == IntPtr.Zero || !IsInjected(klass))
            return Forward(klass, a1, a2, a3);

        if (s_Names.TryGetValue(klass, out var name))
            return name;

        name = ResolveName(klass);
        s_Names[klass] = name;
        return name;
    }

    private static bool IsInjected(IntPtr klass) => *(IntPtr*)klass == (IntPtr)InjectorHelpers.InjectedImage.ImagePointer;

    /// <summary>
    ///     Name the assembly of the first il2cpp ancestor when il2cpp's class lookup in that image finds the injected class,
    ///     which it does unless the class was injected into chosen assemblies only
    /// </summary>
    /// <param name="klass">Injected class</param>
    /// <returns>Assembly name the engine should use for it</returns>
    private static IntPtr ResolveName(IntPtr klass)
    {
        try
        {
            var ancestor = IL2CPP.il2cpp_class_get_parent(klass);
            while (ancestor != IntPtr.Zero && IsInjected(ancestor))
                ancestor = IL2CPP.il2cpp_class_get_parent(ancestor);

            if (ancestor != IntPtr.Zero)
            {
                var image = IL2CPP.il2cpp_class_get_image(ancestor);
                var namespaze = Marshal.PtrToStringUTF8(IL2CPP.il2cpp_class_get_namespace(klass)) ?? string.Empty;
                var name = Marshal.PtrToStringUTF8(IL2CPP.il2cpp_class_get_name(klass)) ?? string.Empty;
                if (IL2CPP.il2cpp_class_from_name(image, namespaze, name) == klass)
                    return Forward(ancestor, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            }
        }
        catch (Exception exception)
        {
            Logger.Instance.LogError("Could not name the assembly of an injected class: {Exception}", exception);
        }

        return Forward(klass, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
    }
}
