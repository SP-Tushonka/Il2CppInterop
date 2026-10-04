using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using Il2CppInterop.Runtime.Runtime;

namespace Il2CppInterop.Runtime.InteropTypes.Arrays;

public abstract class Il2CppArrayBase : Il2CppObjectBase, IEnumerable
{
    protected Il2CppArrayBase(IntPtr pointer) : base(pointer)
    {
    }

    /// <summary>
    /// The pointer to the first element in the array.
    /// </summary>
    private protected unsafe IntPtr ArrayStartPointer => IntPtr.Add(Pointer, sizeof(Il2CppObject) /* base */ + sizeof(void*) /* bounds */ + sizeof(nuint) /* max_length */);

    public int Length => (int)IL2CPP.il2cpp_array_length(Pointer);

    public abstract IEnumerator GetEnumerator();

    private protected static bool ThrowImmutableLength()
    {
        throw new NotSupportedException("Arrays have immutable length");
    }

    private protected void ThrowIfIndexOutOfRange(int index)
    {
        if ((uint)index >= (uint)Length)
            throw new ArgumentOutOfRangeException(nameof(index),
                "Array index may not be negative or above length of the array");
    }
}
public abstract class Il2CppArrayBase<T> : Il2CppArrayBase, IList<T>, IReadOnlyList<T>
{
    protected Il2CppArrayBase(IntPtr pointer) : base(pointer)
    {
    }

    public sealed override IEnumerator<T> GetEnumerator()
    {
        return new IndexEnumerator(this);
    }

    void ICollection<T>.Add(T item)
    {
        ThrowImmutableLength();
    }

    void ICollection<T>.Clear()
    {
        ThrowImmutableLength();
    }

    public bool Contains(T item)
    {
        return IndexOf(item) != -1;
    }

    public void CopyTo(T[] array, int arrayIndex)
    {
        if (array == null) throw new ArgumentNullException(nameof(array));
        if (arrayIndex < 0) throw new ArgumentOutOfRangeException(nameof(arrayIndex));
        if (array.Length - arrayIndex < Length)
            throw new ArgumentException(
                $"Not enough space in target array: need {Length} slots, have {array.Length - arrayIndex}");

        CopyToSpan(array.AsSpan(arrayIndex));
    }

    // Struct arrays copy their memory in one go, the others wrap element by element
    private protected virtual void CopyToSpan(Span<T> destination)
    {
        var length = Length;
        for (var i = 0; i < length; i++)
            destination[i] = this[i];
    }

    bool ICollection<T>.Remove(T item)
    {
        return ThrowImmutableLength();
    }

    public new int Length => base.Length;// For binary compatibility
    public int Count => Length;
    bool ICollection<T>.IsReadOnly => false;

    public int IndexOf(T item)
    {
        // object.Equals boxed both sides of every struct comparison
        var comparer = EqualityComparer<T>.Default;
        var length = Length;
        for (var i = 0; i < length; i++)
            if (comparer.Equals(item, this[i]))
                return i;

        return -1;
    }

    void IList<T>.Insert(int index, T item)
    {
        ThrowImmutableLength();
    }

    void IList<T>.RemoveAt(int index)
    {
        ThrowImmutableLength();
    }

    public abstract T this[int index] { get; set; }

    protected static void StaticCtorBody(Type ownType)
    {
        var nativeClassPtr = Il2CppClassPointerStore<T>.NativeClassPtr;
        if (nativeClassPtr == IntPtr.Zero)
            return;

        var targetClassType = IL2CPP.il2cpp_array_class_get(nativeClassPtr, 1);
        if (targetClassType == IntPtr.Zero)
            return;

        Il2CppClassPointerStore.SetNativeClassPointer(ownType, targetClassType);
        Il2CppClassPointerStore.SetNativeClassPointer(typeof(Il2CppArrayBase<T>), targetClassType);
        Il2CppClassPointerStore<Il2CppArrayBase<T>>.CreatedTypeRedirect = ownType;
    }

    [return: NotNullIfNotNull(nameof(il2CppArray))]
    public static implicit operator T[]?(Il2CppArrayBase<T>? il2CppArray)
    {
        if (il2CppArray == null)
            return null;

        var arr = new T[il2CppArray.Length];
        il2CppArray.CopyToSpan(arr);
        return arr;
    }

    public static Il2CppArrayBase<T>? WrapNativeGenericArrayPointer(IntPtr pointer)
    {
        if (pointer == IntPtr.Zero) return null;

        // A fresh wrapper per read took a GC handle and a finalizer each time, about two microseconds
        return Il2CppObjectPool.GetOrCreate(pointer, GenericArrayFactory.Create);
    }

    // The array types constrain T in ways this class cannot, so their constructor is compiled once per T
    private static class GenericArrayFactory
    {
        public static readonly Func<IntPtr, Il2CppArrayBase<T>> Create = Build();

        private static Func<IntPtr, Il2CppArrayBase<T>> Build()
        {
            if (typeof(T) == typeof(string))
                return pointer => (Il2CppArrayBase<T>)(object)new Il2CppStringArray(pointer);

            Type arrayType;
            if (typeof(T).IsValueType)
                arrayType = typeof(Il2CppStructArray<>).MakeGenericType(typeof(T));
            else if (typeof(Il2CppObjectBase).IsAssignableFrom(typeof(T)) || typeof(T).IsInterface)
                arrayType = typeof(Il2CppReferenceArray<>).MakeGenericType(typeof(T));
            else
                return _ => throw new ArgumentException(
                    $"{typeof(T)} is not a value type, not a string and not an IL2CPP object; it can't be used in IL2CPP arrays");

            var pointerParameter = Expression.Parameter(typeof(IntPtr));
            var constructor = arrayType.GetConstructor([typeof(IntPtr)])!;
            return Expression.Lambda<Func<IntPtr, Il2CppArrayBase<T>>>(
                Expression.Convert(Expression.New(constructor, pointerParameter), typeof(Il2CppArrayBase<T>)),
                pointerParameter).Compile();
        }
    }

    private class IndexEnumerator : IEnumerator<T>
    {
        private Il2CppArrayBase<T> myArray;
        private int myIndex = -1;

        public IndexEnumerator(Il2CppArrayBase<T> array)
        {
            myArray = array;
        }

        public void Dispose()
        {
            myArray = null!;
        }

        public bool MoveNext()
        {
            return ++myIndex < myArray.Count;
        }

        public void Reset()
        {
            myIndex = -1;
        }

        object? IEnumerator.Current => Current;
        public T Current => myArray[myIndex];
    }
}
