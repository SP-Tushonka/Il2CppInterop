using System;

namespace Il2CppInterop.Runtime.InteropTypes;

// Every generated il2cpp interface extends this, so interface typed values keep the pointer and cast members
public interface IIl2CppObjectBase
{
    IntPtr Pointer { get; }
    IntPtr ObjectClass { get; }
    bool WasCollected { get; }
    T Cast<T>() where T : class;
    T? TryCast<T>() where T : class;
}
