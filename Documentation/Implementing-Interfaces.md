# Interfaces

Il2cpp interfaces are generated as real interfaces. A game class that implements one declares it, so a
`Player` can be passed where an `IPlayer` is expected, `is` and `as` work and your own generic
constraints can name them.

Every interface method carries a default implementation that dispatches through il2cpp, so calling a
member on an interface typed value works the same as calling it on a class wrapper. When the runtime has
to produce an interface typed value without a concrete wrapper, for example for a method return, an array
element or `obj.Cast<IFoo>()`, it instantiates the nested `IFoo.Il2CppProxy` class. Every generated
interface extends `IIl2CppObjectBase`, which exposes `Pointer`, `ObjectClass`, `WasCollected`, `Cast<T>()`
and `TryCast<T>()`.

## Implementing an interface with an injected type

Declare the interface on your class and implement its members. Interface members have default bodies, so
the compiler will not point out members you forgot. Any member you leave out keeps its il2cpp vtable slot
empty, `RegisterTypeInIl2Cpp` logs a warning for each one and a call from the game into it crashes.

```csharp
public class MyGame : LocalGame, IBotGame
{
    public MyGame(IntPtr pointer) : base(pointer) { }

    public BotsController BotsController => ...;
}

ClassInjector.RegisterTypeInIl2Cpp<MyGame>();
```

`RegisterTypeInIl2Cpp` registers the il2cpp interfaces your class declares on top of its base class by
itself. `RegisterTypeOptions.Interfaces`, `InterfacesResolver` and `Il2CppImplementsAttribute` still
override that list. Interface methods are matched through the runtime's interface map, so explicit
implementations (`void IFoo.Bar()`) work. Interfaces handed over as raw class pointers fall back to
matching by name, parameter count and genericness.

Explicit implementations in the game (`void IDisposable.Dispose()` in the original code) are emitted as explicit
implementations again: private, bound to the interface method, reachable through the interface only and dispatched
through the object's vtable. Compiler generated members such as an iterator's `MoveNext` keep their plain public
name so they stay easy to patch.

Only explicit implementations dispatch through the interface. A member the game implements implicitly, such as
`List<T>.Count`, is bound to the class method, so a subclass that re-implements that interface is not reached through
a base typed wrapper. Use the subclass wrapper or il2cpp reflection in that case.

Known caveats:

* Blittable structs (`Vector3` and friends) are plain CLR structs and do not declare their interfaces.
* Interface constraints on game generics are dropped, as before.
* An interface typed value coming from the game is a proxy, not the concrete wrapper. Use
  `Cast<Player>()` when you need class members.
