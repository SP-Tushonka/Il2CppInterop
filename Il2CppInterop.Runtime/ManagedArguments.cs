using System;
using Il2CppInterop.Runtime.Injection;
using Il2CppInterop.Runtime.InteropTypes;

namespace Il2CppInterop.Runtime;

/// <summary>
///     Conversions the generated argument extensions call, so a System.Type or a managed IEnumerator can be passed where
///     the game takes the il2cpp type
/// </summary>
public static class ManagedArguments
{
    /// <summary>
    ///     Get the il2cpp type for a managed one. An injectable class is registered on first use
    /// </summary>
    /// <param name="type">Managed type, an interop wrapper or an injected class</param>
    /// <returns>The il2cpp type, null for null</returns>
    public static Il2CppSystem.Type? ToIl2CppType(Type? type) => type == null ? null : Il2CppType.From(type);

    /// <summary>
    ///     Wrap a managed enumerator, such as a coroutine iterator, as an il2cpp IEnumerator object
    /// </summary>
    /// <param name="enumerator">Managed enumerator</param>
    /// <returns>Pointer to the il2cpp object, zero for null</returns>
    public static IntPtr ToIl2CppEnumerator(System.Collections.IEnumerator? enumerator) =>
        enumerator == null ? IntPtr.Zero : new ManagedEnumerator(enumerator).Pointer;

    /// <summary>
    ///     Convert a value a managed enumerator yields into an il2cpp object
    /// </summary>
    /// <param name="value">Yielded value</param>
    /// <returns>il2cpp object for it</returns>
    internal static Il2CppSystem.Object? ToIl2CppObject(object? value)
    {
        switch (value)
        {
            case null:
                return null;
            case Il2CppSystem.Object il2cppObject:
                return il2cppObject;
            // Interface proxies and other wrappers that are not an Il2CppSystem.Object in C#
            case Il2CppObjectBase wrapper:
                return new Il2CppSystem.Object(wrapper.Pointer);
            case System.Collections.IEnumerator nested:
                return new ManagedEnumerator(nested);
            case string text:
                return new Il2CppSystem.Object(IL2CPP.ManagedStringToIl2Cpp(text));
        }

        var klass = Il2CppClassPointerStore.GetNativeClassPointer(value.GetType());
        var box = value switch
        {
            bool flag => Box(klass, (byte)(flag ? 1 : 0)),
            char letter => Box(klass, (ushort)letter),
            byte number => Box(klass, number),
            sbyte number => Box(klass, number),
            short number => Box(klass, number),
            ushort number => Box(klass, number),
            int number => Box(klass, number),
            uint number => Box(klass, number),
            long number => Box(klass, number),
            ulong number => Box(klass, number),
            float number => Box(klass, number),
            double number => Box(klass, number),
            _ => throw new NotSupportedException($"A managed enumerator yielded {value.GetType()}, which has no il2cpp counterpart")
        };
        return new Il2CppSystem.Object(box);
    }

    private static unsafe IntPtr Box<T>(IntPtr klass, T value) where T : unmanaged => IL2CPP.il2cpp_value_box(klass, (IntPtr)(&value));
}

/// <summary>
///     il2cpp IEnumerator over a managed one, which is what StartCoroutine needs for a C# iterator
/// </summary>
public class ManagedEnumerator : Il2CppSystem.Object
{
    private readonly System.Collections.IEnumerator? enumerator;

    // The interface is looked up by name, the reference mscorlib this runtime builds against has it as a class
    static ManagedEnumerator()
    {
        var il2cppEnumerator = Type.GetType("Il2CppSystem.Collections.IEnumerator, Il2Cppmscorlib", true)!;
        ClassInjector.RegisterTypeInIl2Cpp<ManagedEnumerator>(new RegisterTypeOptions { Interfaces = new[] { il2cppEnumerator } });
    }

    public ManagedEnumerator(IntPtr pointer) : base(pointer) { }

    public ManagedEnumerator(System.Collections.IEnumerator enumerator) : base(ClassInjector.DerivedConstructorPointer<ManagedEnumerator>())
    {
        this.enumerator = enumerator ?? throw new ArgumentNullException(nameof(enumerator));
        ClassInjector.DerivedConstructorBody(this);
    }

    public Il2CppSystem.Object? Current => ManagedArguments.ToIl2CppObject(enumerator!.Current);

    public bool MoveNext() => enumerator!.MoveNext();

    public void Reset() => enumerator!.Reset();
}
