# Class injection

Managed classes can be injected into Il2Cpp domain. Currently this is fairly limited,
but functional enough for GC integration and implementing custom MonoBehaviors.

## How-to

### Simple case (no need to create new instances from managed code)

The simple case is useful for injecting classes that will be instantiated by IL2CPP (for example MonoBehaviours).

* Create a class that inherits an IL2CPP class.
* Add methods and fields normally.
* Call `ClassInjector.RegisterTypeInIl2Cpp<T>()` before first use of class to be injected

Example:

```c#
public class MyMonoBehaviour : MonoBehaviour
{
    void Awake()
    {
        // Normal Awake
    }
}

// Example use:

var go = new GameObject();
// This is OK; class is instantiated by IL2CPP
go.AddComponent<MyMonoBehaviour>();
```

Notes:

* **Do not instantiate the class manually**, e.g. `new MyMonoBehaviour()`. Instead use IL2CPP methods that will
  instantiate the class for you.
* If you have a pointer that you want to convert to `MyMonoBehaviour*`,
  use `new Il2CppObjectBase(pointer).Cast<MyMonoBehaiour>();`.

### Extended case (need to create new instances from managed code)

* Your class must inherit from an IL2CPP class.
* You must include a constructor that takes `IntPtr` and passes it to base class constructor. It will be called when
  objects of your class are created from IL2CPP side.
* To create your object from managed side, call base class `IntPtr` constructor with result
  of `ClassInjector.DerivedConstructorPointer<T>()`, where T is your class type, and
  call `ClassInjector.DerivedConstructorBody(this)` in constructor body.
* Call `ClassInjector.RegisterTypeInIl2Cpp<T>()` before first use of class to be injected

Example:

```c#
public class MyClass : SomeIL2CPPClass
{
    // Used by IL2CPP when creating new instances of this class
    public MyClass(IntPtr ptr) : base(ptr) { }
    
    // Used by managed code when creating new instances of this class
    public MyClass() : base(ClassInjector.DerivedConstructorPointer<MyClass>())
    {
        ClassInjector.DerivedConstructorBody(this);
    }
    
    // Any other methods
}


// Example use:

// Creates a new instance of MyClass in IL2CPP
var myInstance = new MyClass();

// If you have a pointer that you want to convert to MyClass, you can use the IntPtr constructor for convenience
var someInstance = new MyClass(pointer);
```

### Running the base class constructor

`DerivedConstructorPointer<T>()` only allocates the il2cpp object, no constructor of the base class runs, so
fields the game initialises in its constructor stay empty. `ClassInjector.InvokeBaseConstructor(this, args...)`
runs the base constructor whose parameters accept the given arguments. Call it after `DerivedConstructorBody`,
a constructor may call virtual methods your class overrides. Arguments can be wrappers, strings, primitives,
enums and blittable structs.

```c#
public class MyController : Player.FirearmController
{
    public MyController(IntPtr ptr) : base(ptr) { }

    public MyController(Player player) : base(ClassInjector.DerivedConstructorPointer<MyController>())
    {
        ClassInjector.DerivedConstructorBody(this);
        ClassInjector.InvokeBaseConstructor(this, player);
    }
}
```

`InvokeBaseConstructor<TBase>(this, args...)` picks a specific ancestor instead of the direct base. The base
may be a closed generic type such as `List<Il2CppSystem.Object>`, only open generic definitions are rejected.

### Sealed base classes

The generator emits wrappers of sealed il2cpp classes unsealed (`GeneratorOptions.UnsealClasses`), and the injector
registers a type deriving from one with a warning instead of refusing. What you get is a real il2cpp subclass:
`is`, `Cast` and reflection agree it derives from the base, and calls that reach it through an ancestor's virtual
slot or an interface see your overrides. What you do not get is the game noticing through the sealed type itself.
il2cpp compiles calls on a sealed class as direct calls and casts to it as an exact class comparison, so a method
the game invokes on a variable typed as the sealed class runs the base body, and `is SealedClass` in game code is
false for your object.

### Struct parameters and returns

Methods of injected classes can take and return struct wrappers (`MongoID`, `Nullable<T>` and friends). Win64 passes
wrappers of 1, 2, 4 or 8 bytes in registers and everything else through a pointer, returns follow the same rule with
a caller allocated buffer for the larger ones. The trampolines handle both, so an `IEnumerator<MongoID>` implemented
in managed code can be consumed by the game.

## Fine-tuning

* `[HideFromIl2Cpp]` can be used to prevent a method from being exposed to il2cpp

## Caveats

* Injected class instances are handled by IL2CPP garbage collection. This means that an object may be collected even if
  it's referenced from managed domain. Attempting to use that object afterwards will result
  in `ObjectCollectedException`. Conversely, managed representation of injected object will not be garbage collected as
  long as it's referenced from IL2CPP domain.
* It might be possible to create a cross-domain reference loop that will prevent objects from being garbage collected.
  Avoid doing anything that will result in injected class instances (indirectly) storing references to itself. The
  simplest example of how to leak memory is this:

```c#
class Injected: Il2CppSystem.Object {
    Il2CppSystem.Collections.Generic.List<Il2CppSystem.Object> list = new ...;
    public Injected() {
        list.Add(this); // reference to itself through an IL2CPP list. This will prevent both this and list from being garbage collected, ever.
    }
}
```

## Fields injection

> TODO: Describe how field injection works based on [#24](https://github.com/BepInEx/Il2CppAssemblyUnhollower/pull/24)

## Current limitations

* Not all members are exposed to Il2Cpp side - no properties, events or static methods will be visible to
  Il2Cpp reflection. Fields are exported, but the feature is fairly limited.
* Only a limited set of types is supported for method signatures
 