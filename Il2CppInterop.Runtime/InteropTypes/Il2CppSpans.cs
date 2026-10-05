using System;

namespace Il2CppInterop.Runtime.InteropTypes;

/// <summary>
///     Views the memory of a boxed il2cpp Span or ReadOnlySpan as a CLR span, for rebuilt code that pins or indexes it
/// </summary>
public static unsafe class Il2CppSpans
{
    private static int s_pointerOffset = -1;
    private static int s_lengthOffset;

    /// <summary>
    ///     Get a CLR span over the memory of an il2cpp span
    /// </summary>
    /// <param name="span">Boxed il2cpp Span or ReadOnlySpan</param>
    /// <returns>Span over the same memory, empty for null</returns>
    public static Span<T> ToSpan<T>(Il2CppObjectBase? span) where T : unmanaged
    {
        var pointer = IL2CPP.Il2CppObjectBaseToPtr(span);
        if (pointer == IntPtr.Zero)
            return default;

        ResolveOffsets(pointer);
        // ByReference<T> wraps a single pointer, so the field holds the address itself
        return new Span<T>(*(void**)(pointer + s_pointerOffset), *(int*)(pointer + s_lengthOffset));
    }

    /// <summary>
    ///     Get a read only CLR span over the memory of an il2cpp span
    /// </summary>
    /// <param name="span">Boxed il2cpp Span or ReadOnlySpan</param>
    /// <returns>Span over the same memory, empty for null</returns>
    public static ReadOnlySpan<T> ToReadOnlySpan<T>(Il2CppObjectBase? span) where T : unmanaged => ToSpan<T>(span);

    /// <summary>
    ///     Read the field offsets from the first span seen. Every Span and ReadOnlySpan instantiation shares one layout.
    /// </summary>
    /// <param name="span">Boxed il2cpp span</param>
    private static void ResolveOffsets(IntPtr span)
    {
        if (s_pointerOffset >= 0)
            return;

        var klass = IL2CPP.il2cpp_object_get_class(span);
        s_lengthOffset = (int)IL2CPP.il2cpp_field_get_offset(IL2CPP.GetIl2CppField(klass, "_length"));
        s_pointerOffset = (int)IL2CPP.il2cpp_field_get_offset(IL2CPP.GetIl2CppField(klass, "_pointer"));
    }
}
