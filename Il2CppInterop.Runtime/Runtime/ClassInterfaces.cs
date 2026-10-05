using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime.Runtime.VersionSpecific.Class;

namespace Il2CppInterop.Runtime.Runtime;

/// <summary>
///     Reads and writes a class's interfaces in either layout. Before 6000.6 a class keeps them in two arrays, from
///     6000.6 in one.
/// </summary>
public static unsafe class ClassInterfaces
{
    /// <summary>
    ///     Get an interface with a vtable block
    /// </summary>
    /// <param name="klass">Class to read</param>
    /// <param name="index">Index below <see cref="INativeClassStruct.InterfaceOffsetsCount"/></param>
    /// <returns>Interface class</returns>
    public static Il2CppClass* DispatchInterface(this INativeClassStruct klass, int index) =>
        UnityVersionHandler.HasSingleInterfaceArray ? klass.Interfaces[index].interfaceType : klass.InterfaceOffsets[index].interfaceType;

    /// <summary>
    ///     Get the vtable offset of an interface's block
    /// </summary>
    /// <param name="klass">Class to read</param>
    /// <param name="index">Index below <see cref="INativeClassStruct.InterfaceOffsetsCount"/></param>
    /// <returns>Vtable offset</returns>
    public static int DispatchInterfaceOffset(this INativeClassStruct klass, int index) =>
        UnityVersionHandler.HasSingleInterfaceArray ? klass.Interfaces[index].offset : klass.InterfaceOffsets[index].offset;

    /// <summary>
    ///     Get an interface the class implements
    /// </summary>
    /// <param name="klass">Class to read</param>
    /// <param name="index">Index below <see cref="INativeClassStruct.InterfaceCount"/></param>
    /// <returns>Interface class</returns>
    public static Il2CppClass* ImplementedInterface(this INativeClassStruct klass, int index) =>
        UnityVersionHandler.HasSingleInterfaceArray ? klass.Interfaces[index].interfaceType : klass.ImplementedInterfaces[index];

    /// <summary>
    ///     Give an injected class its base class's interfaces followed by the ones it declares
    /// </summary>
    /// <param name="klass">Injected class</param>
    /// <param name="baseClass">Base class of the injected class</param>
    /// <param name="declared">Interfaces the injected class declares</param>
    /// <param name="offsets">Vtable offset of each declared interface's block</param>
    public static void InheritInterfaces(this INativeClassStruct klass, INativeClassStruct baseClass,
        IReadOnlyList<INativeClassStruct> declared, IReadOnlyList<int> offsets)
    {
        if (UnityVersionHandler.HasSingleInterfaceArray)
            InheritSingleArray(klass, baseClass, declared, offsets);
        else
            InheritTwoArrays(klass, baseClass, declared, offsets);
    }

    private static void InheritTwoArrays(INativeClassStruct klass, INativeClassStruct baseClass,
        IReadOnlyList<INativeClassStruct> declared, IReadOnlyList<int> offsets)
    {
        var interfaceCount = baseClass.InterfaceCount + declared.Count;
        klass.InterfaceCount = (ushort)interfaceCount;
        klass.ImplementedInterfaces = (Il2CppClass**)Marshal.AllocHGlobal(interfaceCount * IntPtr.Size);
        for (var i = 0; i < baseClass.InterfaceCount; i++)
            klass.ImplementedInterfaces[i] = baseClass.ImplementedInterfaces[i];
        for (var i = 0; i < declared.Count; i++)
            klass.ImplementedInterfaces[baseClass.InterfaceCount + i] = declared[i].ClassPointer;

        var offsetsCount = baseClass.InterfaceOffsetsCount + declared.Count;
        klass.InterfaceOffsetsCount = (ushort)offsetsCount;
        klass.InterfaceOffsets = (Il2CppRuntimeInterfaceOffsetPair*)Marshal.AllocHGlobal(offsetsCount * sizeof(Il2CppRuntimeInterfaceOffsetPair));
        for (var i = 0; i < baseClass.InterfaceOffsetsCount; i++)
            klass.InterfaceOffsets[i] = baseClass.InterfaceOffsets[i];
        for (var i = 0; i < declared.Count; i++)
            klass.InterfaceOffsets[baseClass.InterfaceOffsetsCount + i] = new Il2CppRuntimeInterfaceOffsetPair
            {
                interfaceType = declared[i].ClassPointer,
                offset = offsets[i],
            };
    }

    /// <summary>
    ///     Build the single array from the base class's dispatchable entries, the declared ones and then the base
    ///     class's reflection only entries. Inherited entries are one more hop from their declaring class.
    /// </summary>
    /// <param name="klass">Injected class</param>
    /// <param name="baseClass">Base class of the injected class</param>
    /// <param name="declared">Interfaces the injected class declares</param>
    /// <param name="offsets">Vtable offset of each declared interface's block</param>
    private static void InheritSingleArray(INativeClassStruct klass, INativeClassStruct baseClass,
        IReadOnlyList<INativeClassStruct> declared, IReadOnlyList<int> offsets)
    {
        var baseInterfaces = baseClass.Interfaces;
        var baseDispatch = baseClass.InterfaceOffsetsCount;
        var baseCount = baseClass.InterfaceCount;

        var entries = (Il2CppRuntimeInterfaceData*)Marshal.AllocHGlobal((baseCount + declared.Count) * sizeof(Il2CppRuntimeInterfaceData));
        var next = 0;
        for (var i = 0; i < baseDispatch; i++)
            entries[next++] = Inherited(baseInterfaces[i]);
        for (var i = 0; i < declared.Count; i++)
            entries[next++] = new Il2CppRuntimeInterfaceData { interfaceType = declared[i].ClassPointer, offset = offsets[i], depth = 0 };
        for (var i = baseDispatch; i < baseCount; i++)
            entries[next++] = Inherited(baseInterfaces[i]);

        klass.Interfaces = entries;
        klass.InterfaceCount = (ushort)next;
        klass.InterfaceOffsetsCount = (ushort)(baseDispatch + declared.Count);
    }

    private static Il2CppRuntimeInterfaceData Inherited(Il2CppRuntimeInterfaceData entry)
    {
        if (entry.depth >= 0)
            entry.depth++;
        return entry;
    }
}
