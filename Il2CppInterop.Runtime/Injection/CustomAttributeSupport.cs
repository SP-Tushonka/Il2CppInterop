using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Il2CppInterop.Common;
using Il2CppInterop.Runtime.Attributes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Microsoft.Extensions.Logging;

namespace Il2CppInterop.Runtime.Injection;

/// <summary>
///     Reports the <see cref="Il2CppMirroredAttribute" />s of injected classes to the engine. Unity reads class attributes
///     such as RequireComponent through the il2cpp custom attribute exports, which know nothing of injected classes.
///     Only the engine's pointers to those exports are swapped, il2cpp's own reflection is left alone
/// </summary>
internal static unsafe class CustomAttributeSupport
{
    private const int FromClassIndex = 0;
    private const int HasAttrIndex = 1;
    private const int GetAttrIndex = 2;
    private const int ConstructIndex = 3;
    private const int ClassHasAttributeIndex = 4;

    private static readonly ConcurrentDictionary<IntPtr, ClassAttributes> s_ByClass = new();
    private static readonly ConcurrentDictionary<IntPtr, ClassAttributes> s_ByHandle = new();
    private static readonly ConcurrentDictionary<Type, bool> s_PendingComponents = new();
    private static readonly IntPtr[] s_Originals = new IntPtr[5];
    private static readonly object s_PatchLock = new();
    private static bool s_PatchAttempted;
    private static bool s_Patched;

    /// <summary>
    ///     Attributes of one injected class and the handle the engine is given for them
    /// </summary>
    private sealed class ClassAttributes(Type type, Il2CppMirroredAttribute[] mirrors)
    {
        public readonly Type Type = type;
        public readonly Il2CppMirroredAttribute[] Mirrors = mirrors;
        public readonly IntPtr Handle = Marshal.AllocHGlobal(1);
        public IntPtr[]? Objects;
    }

    /// <summary>
    ///     Record the mirrored attributes declared on a newly injected class
    /// </summary>
    /// <param name="type">Managed type that was injected</param>
    /// <param name="classPointer">Its il2cpp class</param>
    internal static void Register(Type type, IntPtr classPointer)
    {
        try
        {
            if (s_PendingComponents.TryRemove(type, out _))
                KeepScriptIfComponent(type);

            var mirrors = Attribute.GetCustomAttributes(type, typeof(Il2CppMirroredAttribute), false).Cast<Il2CppMirroredAttribute>().ToArray();
            if (mirrors.Length == 0 || !EnsurePatched())
                return;

            var attributes = new ClassAttributes(type, mirrors);
            s_ByHandle[attributes.Handle] = attributes;
            s_ByClass[classPointer] = attributes;

            // A component named by RequireComponent needs a script before the engine looks for it
            foreach (var argument in mirrors.SelectMany(mirror => mirror.ConstructorArguments).OfType<Type>())
            {
                if (!typeof(InteropTypes.Il2CppObjectBase).IsAssignableFrom(argument))
                    continue;
                if (Il2CppClassPointerStore.GetNativeClassPointer(argument) == IntPtr.Zero)
                    s_PendingComponents.TryAdd(argument, true);
                else
                    KeepScriptIfComponent(argument);
            }
        }
        catch (Exception exception)
        {
            Logger.Instance.LogError("Could not register the il2cpp attributes of {Type}: {Exception}", type, exception);
        }
    }

    /// <summary>
    ///     Keep an engine script for an injected component, nothing for il2cpp classes or non components
    /// </summary>
    /// <param name="type">Type named by a mirrored attribute</param>
    private static void KeepScriptIfComponent(Type type)
    {
        var klass = Il2CppClassPointerStore.GetNativeClassPointer(type);
        var component = IL2CPP.GetIl2CppClass("UnityEngine.CoreModule.dll", "UnityEngine", "Component");
        if (klass == IntPtr.Zero || component == IntPtr.Zero || *(IntPtr*)klass != (IntPtr)InjectorHelpers.InjectedImage.ImagePointer
            || !IL2CPP.il2cpp_class_is_subclass_of(klass, component, false))
            return;

        InjectedScriptNames.KeepScript(type);
    }

    /// <summary>
    ///     Point the engine's custom attribute imports at the handlers below, once. The handle one handler gives out must
    ///     never reach an export that reads it as metadata, so the handle based imports are swapped together or not at all
    /// </summary>
    /// <returns>True when the engine now asks the handlers</returns>
    private static bool EnsurePatched()
    {
        lock (s_PatchLock)
        {
            if (s_PatchAttempted)
                return s_Patched;
            s_PatchAttempted = true;

            try
            {
                s_Patched = Patch();
            }
            catch (Exception exception)
            {
                Logger.Instance.LogError("Could not hook the engine's il2cpp attribute imports: {Exception}", exception);
            }

            if (!s_Patched)
                Logger.Instance.LogWarning("Il2cpp attributes on injected classes are not reported to the engine");
            return s_Patched;
        }
    }

    private static bool Patch()
    {
        // The engine frees handles from the handlers too, which is only harmless while the export does nothing
        var free = InjectorHelpers.GetIl2CppExport(nameof(IL2CPP.il2cpp_custom_attrs_free));
        if (*(byte*)free != 0xC3 && *(byte*)free != 0xC2)
        {
            Logger.Instance.LogWarning("il2cpp_custom_attrs_free is not empty in this build");
            return false;
        }

        var targets = new (string Name, IntPtr Handler, bool Required)[]
        {
            (nameof(IL2CPP.il2cpp_custom_attrs_from_class), (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr>)&FromClass, true),
            (nameof(IL2CPP.il2cpp_custom_attrs_has_attr), (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr>)&HasAttr, true),
            (nameof(IL2CPP.il2cpp_custom_attrs_get_attr), (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr>)&GetAttr, true),
            (nameof(IL2CPP.il2cpp_custom_attrs_construct), (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr>)&Construct, true),
            ("il2cpp_class_has_attribute", (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr>)&ClassHasAttribute, false),
        };

        var slots = new IntPtr[targets.Length];
        for (var i = 0; i < targets.Length; i++)
        {
            if (EngineImports.TryFindSlot(targets[i].Name, out var slot, out var export))
            {
                slots[i] = slot;
                s_Originals[i] = export;
            }
            else if (targets[i].Required)
            {
                return false;
            }
        }

        for (var i = 0; i < targets.Length; i++)
        {
            if (slots[i] != IntPtr.Zero)
                EngineImports.Swap(targets[i].Name, slots[i], s_Originals[i], targets[i].Handler);
        }

        return true;
    }

    private static IntPtr Forward(int index, IntPtr a0, IntPtr a1, IntPtr a2, IntPtr a3) =>
        ((delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr>)s_Originals[index])(a0, a1, a2, a3);

    // The handlers take and forward four register arguments, so an export that grows a parameter keeps receiving it

    [UnmanagedCallersOnly]
    private static IntPtr FromClass(IntPtr klass, IntPtr a1, IntPtr a2, IntPtr a3)
    {
        if (klass != IntPtr.Zero && s_ByClass.TryGetValue(klass, out var attributes))
            return attributes.Handle;
        return Forward(FromClassIndex, klass, a1, a2, a3);
    }

    [UnmanagedCallersOnly]
    private static IntPtr HasAttr(IntPtr handle, IntPtr attributeClass, IntPtr a2, IntPtr a3)
    {
        if (s_ByHandle.TryGetValue(handle, out var attributes))
            return Find(attributes, attributeClass) != IntPtr.Zero ? 1 : 0;
        return Forward(HasAttrIndex, handle, attributeClass, a2, a3);
    }

    [UnmanagedCallersOnly]
    private static IntPtr GetAttr(IntPtr handle, IntPtr attributeClass, IntPtr a2, IntPtr a3)
    {
        if (s_ByHandle.TryGetValue(handle, out var attributes))
            return Find(attributes, attributeClass);
        return Forward(GetAttrIndex, handle, attributeClass, a2, a3);
    }

    [UnmanagedCallersOnly]
    private static IntPtr Construct(IntPtr handle, IntPtr a1, IntPtr a2, IntPtr a3)
    {
        if (!s_ByHandle.TryGetValue(handle, out var attributes))
            return Forward(ConstructIndex, handle, a1, a2, a3);

        try
        {
            var objects = Materialize(attributes);
            var array = new Il2CppReferenceArray<Il2CppSystem.Object>(objects.Length);
            for (var i = 0; i < objects.Length; i++)
                array[i] = new Il2CppSystem.Object(objects[i]);
            return array.Pointer;
        }
        catch (Exception exception)
        {
            Logger.Instance.LogError("Could not list the il2cpp attributes of {Type}: {Exception}", attributes.Type, exception);
            return IntPtr.Zero;
        }
    }

    [UnmanagedCallersOnly]
    private static IntPtr ClassHasAttribute(IntPtr klass, IntPtr attributeClass, IntPtr a2, IntPtr a3)
    {
        if (klass != IntPtr.Zero && s_ByClass.TryGetValue(klass, out var attributes))
            return Find(attributes, attributeClass) != IntPtr.Zero ? 1 : 0;
        return Forward(ClassHasAttributeIndex, klass, attributeClass, a2, a3);
    }

    /// <summary>
    ///     Find the first attribute of a class that is the given attribute class or derives from it
    /// </summary>
    /// <param name="attributes">Attributes of an injected class</param>
    /// <param name="attributeClass">Il2cpp attribute class asked for</param>
    /// <returns>The il2cpp attribute object, zero when there is none</returns>
    private static IntPtr Find(ClassAttributes attributes, IntPtr attributeClass)
    {
        try
        {
            foreach (var attribute in Materialize(attributes))
            {
                if (IL2CPP.il2cpp_class_is_assignable_from(attributeClass, IL2CPP.il2cpp_object_get_class(attribute)))
                    return attribute;
            }
        }
        catch (Exception exception)
        {
            Logger.Instance.LogError("Could not search the il2cpp attributes of {Type}: {Exception}", attributes.Type, exception);
        }

        return IntPtr.Zero;
    }

    /// <summary>
    ///     Build the il2cpp attribute objects of a class on first use, by then the classes their arguments name are
    ///     injected too. An attribute that fails is logged once and left out
    /// </summary>
    /// <param name="attributes">Attributes of an injected class</param>
    /// <returns>Il2cpp attribute objects, each kept alive by a gc handle</returns>
    private static IntPtr[] Materialize(ClassAttributes attributes)
    {
        var objects = Volatile.Read(ref attributes.Objects);
        if (objects != null)
            return objects;

        lock (attributes)
        {
            if (attributes.Objects != null)
                return attributes.Objects;

            var created = new List<IntPtr>();
            foreach (var mirror in attributes.Mirrors)
            {
                try
                {
                    var pointer = mirror.CreateIl2CppAttribute().Pointer;
                    IL2CPP.il2cpp_gchandle_new(pointer, false);
                    created.Add(pointer);
                }
                catch (Exception exception)
                {
                    Logger.Instance.LogError("Could not create {Attribute} for {Type}: {Exception}", mirror.Il2CppAttributeType, attributes.Type, exception);
                }
            }

            objects = created.ToArray();
            Volatile.Write(ref attributes.Objects, objects);
            return objects;
        }
    }
}
