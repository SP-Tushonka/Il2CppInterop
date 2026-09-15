using System;
using System.Collections.Concurrent;
using Il2CppInterop.Runtime.Runtime;

namespace Il2CppInterop.Runtime.InteropTypes;

// Equality, hashing and ToString follow the il2cpp object, so two wrappers of one object are equal, wrappers work as
// dictionary keys and a struct wrapper compares by value the way its il2cpp Equals decides.
public partial class Il2CppObjectBase
{
    private static IntPtr ourObjectEquals;
    private static IntPtr ourObjectGetHashCode;
    private static IntPtr ourObjectToString;
    private static bool ourObjectMethodsResolved;

    private static readonly ConcurrentDictionary<IntPtr, ClassSemantics> ourClassSemantics = new();

    private sealed class ClassSemantics
    {
        public bool Injected;
        public bool EqualsOverridden;
        public bool GetHashCodeOverridden;
    }

    public override bool Equals(object? obj)
    {
        if (ReferenceEquals(this, obj))
            return true;
        if (obj is not Il2CppObjectBase other)
            return false;
        if (myPointer == other.myPointer)
            return true;
        if (WasCollected || other.WasCollected)
            return false;

        var semantics = SemanticsOf(this);
        if (semantics.Injected || !semantics.EqualsOverridden)
            return false;

        var method = IL2CPP.il2cpp_object_get_virtual_method(myPointer, ourObjectEquals);
        unsafe
        {
            var args = stackalloc IntPtr[1];
            args[0] = other.myPointer;
            var result = Invoke(method, (void**)args);
            return result != IntPtr.Zero && *(bool*)IL2CPP.il2cpp_object_unbox(result);
        }
    }

    // Reference identity by il2cpp pointer, so two wrappers of one object compare equal the way references do in C#
    public static bool operator ==(Il2CppObjectBase? left, Il2CppObjectBase? right)
    {
        if (ReferenceEquals(left, right))
            return true;
        if (left is null || right is null)
            return false;
        return left.myPointer == right.myPointer;
    }

    public static bool operator !=(Il2CppObjectBase? left, Il2CppObjectBase? right)
    {
        return !(left == right);
    }

    public override int GetHashCode()
    {
        if (WasCollected)
            return myPointer.GetHashCode();

        var semantics = SemanticsOf(this);
        if (semantics.Injected || !semantics.GetHashCodeOverridden)
            return myPointer.GetHashCode();

        var method = IL2CPP.il2cpp_object_get_virtual_method(myPointer, ourObjectGetHashCode);
        unsafe
        {
            var result = Invoke(method, null);
            return result == IntPtr.Zero ? myPointer.GetHashCode() : *(int*)IL2CPP.il2cpp_object_unbox(result);
        }
    }

    public override string ToString()
    {
        if (WasCollected)
            return GetType().Name + " (collected)";

        var semantics = SemanticsOf(this);
        if (semantics.Injected)
            return base.ToString()!;

        try
        {
            var method = IL2CPP.il2cpp_object_get_virtual_method(myPointer, ourObjectToString);
            unsafe
            {
                var result = Invoke(method, null);
                return result == IntPtr.Zero ? "" : IL2CPP.Il2CppStringToManaged(result)!;
            }
        }
        catch (Exception)
        {
            return base.ToString()!;
        }
    }

    // this is unboxed when the resolved method belongs to a value type, il2cpp_runtime_invoke passes it through as is
    private unsafe IntPtr Invoke(IntPtr method, void** args)
    {
        var self = IL2CPP.il2cpp_class_is_valuetype(IL2CPP.il2cpp_method_get_class(method)) ? IL2CPP.il2cpp_object_unbox(myPointer) : myPointer;
        var exception = IntPtr.Zero;
        var result = IL2CPP.il2cpp_runtime_invoke(method, self, args, ref exception);
        Il2CppException.RaiseExceptionIfNecessary(exception);
        return result;
    }

    // An injected object's own overrides already run, and its il2cpp slots would only call back into them
    private static ClassSemantics SemanticsOf(Il2CppObjectBase instance)
    {
        ResolveObjectMethods();
        return ourClassSemantics.GetOrAdd(instance.ObjectClass, static (klass, pointer) => new ClassSemantics
        {
            Injected = RuntimeSpecificsStore.IsInjected(klass),
            EqualsOverridden = IL2CPP.il2cpp_object_get_virtual_method(pointer, ourObjectEquals) != ourObjectEquals,
            GetHashCodeOverridden = IL2CPP.il2cpp_object_get_virtual_method(pointer, ourObjectGetHashCode) != ourObjectGetHashCode,
        }, instance.myPointer);
    }

    private static void ResolveObjectMethods()
    {
        if (ourObjectMethodsResolved)
            return;
        var objectClass = IL2CPP.GetIl2CppClass("mscorlib.dll", "System", "Object");
        ourObjectEquals = IL2CPP.il2cpp_class_get_method_from_name(objectClass, "Equals", 1);
        ourObjectGetHashCode = IL2CPP.il2cpp_class_get_method_from_name(objectClass, "GetHashCode", 0);
        ourObjectToString = IL2CPP.il2cpp_class_get_method_from_name(objectClass, "ToString", 0);
        ourObjectMethodsResolved = true;
    }
}
