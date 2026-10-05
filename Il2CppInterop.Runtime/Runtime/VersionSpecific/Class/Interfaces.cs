using System;
using Il2CppInterop.Runtime.Runtime.VersionSpecific.Type;

namespace Il2CppInterop.Runtime.Runtime.VersionSpecific.Class;

public interface INativeClassStructHandler : INativeStructHandler
{
    INativeClassStruct CreateNewStruct(int vTableSlots);
    unsafe INativeClassStruct Wrap(Il2CppClass* classPointer);
}

public interface INativeClassStruct : INativeStruct
{
    unsafe Il2CppClass* ClassPointer { get; }
    IntPtr VTable { get; }

    ref uint InstanceSize { get; }
    ref ushort VtableCount { get; }
    ref ushort InterfaceCount { get; }
    ref ushort InterfaceOffsetsCount { get; }
    ref byte TypeHierarchyDepth { get; }
    ref int NativeSize { get; }
    ref uint ActualSize { get; }
    ref ushort MethodCount { get; }
    ref ushort FieldCount { get; }
    ref Il2CppClassAttributes Flags { get; }

    bool ValueType { get; set; }
    bool EnumType { get; set; }
    bool IsGeneric { get; set; }
    bool Initialized { get; set; }
    bool InitializedAndNoError { get; set; }
    bool SizeInited { get; set; }
    bool HasFinalize { get; set; }
    bool HasReferences { get; set; }
    bool IsVtableInitialized { get; set; }

    ref IntPtr Name { get; }
    ref IntPtr Namespace { get; }
    unsafe ref IntPtr GcDesc { get; }

    INativeTypeStruct ByValArg { get; }
    INativeTypeStruct ThisArg { get; }

    unsafe ref Il2CppImage* Image { get; }
    unsafe ref Il2CppClass* Parent { get; }
    unsafe ref Il2CppClass* ElementClass { get; }
    unsafe ref Il2CppClass* CastClass { get; }
    unsafe ref Il2CppClass* DeclaringType { get; }
    unsafe ref Il2CppClass* Class { get; }

    unsafe ref Il2CppFieldInfo* Fields { get; }
    unsafe ref Il2CppMethodInfo** Methods { get; }

    /// <summary>
    ///     Implemented interfaces, in layouts before 6000.6
    /// </summary>
    unsafe ref Il2CppClass** ImplementedInterfaces => throw new NotSupportedException("This layout keeps one interface array");

    /// <summary>
    ///     Interfaces with their vtable offsets, in layouts before 6000.6
    /// </summary>
    unsafe ref Il2CppRuntimeInterfaceOffsetPair* InterfaceOffsets => throw new NotSupportedException("This layout keeps one interface array");

    /// <summary>
    ///     The single interface array of layouts from 6000.6
    /// </summary>
    unsafe ref Il2CppRuntimeInterfaceData* Interfaces => throw new NotSupportedException("This layout keeps two interface arrays");

    unsafe ref Il2CppClass** TypeHierarchy { get; }
}
