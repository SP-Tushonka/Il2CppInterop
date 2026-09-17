# SPT additions

What this fork generates and does at runtime beyond upstream Il2CppInterop. Each generator addition lives in
`Il2CppInterop.Generator/Passes/SPT` and has a switch on `GeneratorOptions`.

## Unsealed classes (`UnsealClasses`)

Wrappers of sealed il2cpp classes are emitted unsealed so injected types can derive from them, the Unity types
copied in by unstripping included. Static classes, delegates, enums and value types keep the seal. See
[Class-Injection.md](Class-Injection.md) for what the game does and does not see through a sealed base.

## System interfaces on il2cpp collections (`BridgeSystemInterfaces`)

The generated `Il2CppSystem.Collections.Generic.IEnumerable<T>`, `IEnumerator<T>`, `IReadOnlyCollection<T>`,
`IReadOnlyList<T>`, the non generic `IEnumerable` and `IEnumerator`, and `Il2CppSystem.IDisposable` extend their
`System` counterparts. Default interface bodies forward to the il2cpp members, so every il2cpp collection is a .NET
collection as far as the compiler and the BCL are concerned:

```csharp
Il2CppSystem.Collections.Generic.List<Item> items = ...;
var ids = items.Where(item => item != null).Select(item => item.TemplateId).ToList();
foreach (var item in inventory.GetAllItemByTemplate(id)) ...
IReadOnlyList<Item> view = items;
using var enumerator = items.GetEnumerator();
```

`ICollection<T>` and `IList<T>` are bridged as well, `CopyTo` walks the il2cpp enumerator into the managed array, so
LINQ's `ToArray` and `ToList` take the collection path. `IDictionary<K,V>` is not bridged, it would need an adapter
for the System `KeyValuePair`.

## Most derived wrappers

`Il2CppObjectPool.Get<T>` looks up the generated wrapper of the object's own il2cpp class by name and creates that
when it fits `T`. `is`, `as`, pattern switches and `GetType()` therefore see the real type:

```csharp
switch (action)
{
    case CutsceneActionExitAfterCutscene exit: ...
    case CutsceneActionOpenDoors doors: ...
}
```

Objects of generic instantiations, arrays and classes without a generated wrapper come back as `T`, so `TryCast`
stays the way to reach those.

## Equality, hashing and ToString

`Il2CppObjectBase` overrides `Equals`, `GetHashCode` and `ToString` to follow the il2cpp object. Two wrappers of one
object are equal and hash alike, a class that overrides `Equals` in il2cpp compares through that override, a wrapped
struct compares by value, and `ToString` through `object` reaches the il2cpp `ToString`. Reference types without an
override compare by pointer, at no il2cpp cost. Injected types keep their own C# overrides. `==` and `!=` on
`Il2CppObjectBase` are reference identity by il2cpp pointer, types that define their own operators in il2cpp,
`UnityEngine.Object` among them, still win overload resolution.

## Base calls from injected overrides

A generated virtual method dispatches through il2cpp, which on an injected object lands back in the managed override,
so `base.Method()` used to recurse until the stack ran out. Wrappers now go through `IL2CPP.ResolveVirtualMethod`,
which runs the wrapper's own il2cpp method for an object of an injected class and dispatches normally for every other
object. The CLR has already chosen the override, so a wrapper body is reached on an injected object only for a base
call or for a method the managed class does not override. Abstract il2cpp methods still dispatch, there is no body to
run. A managed method declared `new` rather than `override` now reaches the base body through a base typed reference,
where it used to reach the hiding method.

## Boxing (`ValueTypeHelpers`)

Every blittable struct converts implicitly to `Il2CppSystem.Object` and back by cast, with the il2cpp class checked:

```csharp
Il2CppSystem.Object boxed = new Vector3(1, 2, 3);
var vector = (Vector3)boxed;
```

## Events (`GenerateEvents`)

il2cpp events are C# events typed by the System delegate, so `+=` takes a lambda. The accessors convert the delegate
and call the generated `add_X`/`remove_X` methods, which keep their names and their il2cpp delegate parameter for
patches. `DelegateSupport.ConvertDelegate` hands out one il2cpp delegate per managed delegate, so `-=` removes what
`+=` added:

```csharp
time.OnMinute += () => ...;
Action handler = Tick;
time.OnMinute += handler;
time.OnMinute -= handler;
```

C# refuses an event whose type is not a CLR delegate, which rules out the il2cpp delegate wrapper as the event type.
Lambdas still do not bind to ordinary il2cpp delegate parameters, `new Action(...)` stays there. A sibling overload per
method taking the System delegate was tried and dropped, every such method then has two overloads and Harmony's
`AccessTools.Method(type, name)` throws `AmbiguousMatchException`.
A field like event names its backing field after the event, that field's property moves to `<Name>Field`. Events
whose delegate has no System counterpart (more than 8 parameters, byref parameters) stay as `add_`/`remove_` methods.

## Wrapped value types (`ValueTypeHelpers`)

A non blittable struct is a class over a box, so assignment shares the box. Every such wrapper gets `Clone()`, a
copy in a fresh box, and a constructor taking every instance field in declaration order when the struct does not
already declare one with that shape:

```csharp
var id = new MongoID(timeStamp, counter, stringId);
var copy = id.Clone();
```

`Il2CppSystem.Nullable<T>` stays a class, `T?` in its place would demand `T : struct` on every instantiation. It
gains an implicit conversion from `T`, and `Il2CppSystem.NullableExtensions` copies to and from `System.Nullable<T>`:

```csharp
Il2CppSystem.Nullable<EGameMode> mode = EGameMode.Pve;
EGameMode? managed = mode.ToNullable();
var back = managed.ToIl2CppNullable();
```

Assemblies carrying extension methods also get the assembly level `ExtensionAttribute`, without it the C# compiler
never offers `root.GetAllItems()`.
