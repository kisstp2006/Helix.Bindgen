# Helix.Bindgen

The binding generator of the Helix engine, between C++ and C#, both ways. It reads tagged C++ headers and writes
the module's registration in the scripting core (Helix.Scripting) and the C# API over a native function table; it
reads compiled C# assemblies and writes typed C++ proxies of C# classes over a managed function table. It is a C#
tool run with the .NET SDK of the build host, so cross builds (the Web, Android) run it too.

## Contents

| Path | Content |
|---|---|
| `Source/HelixBindgen/Cpp/` | `Lexer`, `Parser`: the tagged subset of C++ (namespaces, tagged classes, structs, enums, interfaces and members; everything else skipped by balancing brackets; errors with file and line) |
| `Source/HelixBindgen/Model/` | The binding model (types, members, signatures) and `TypeMap`: how each C++ type crosses (a table, not code), struct layouts |
| `Source/HelixBindgen/Generators/` | `CppGenerator` (layout checks, registration with invokers, extern "C" thunks, the function table), `CsGenerator` (structs, enums, partial classes, events, the loader) |
| `Source/HelixBindgen/Managed/` | `AssemblyReader` (System.Reflection.Metadata), `ProxyGenerator` (C++ proxies and their C# exports) |
| `Source/Runtime/HelixInterop.cs` | What generated C# uses: strings, arrays and objects across, type ids, wrappers, event handlers, errors |
| `CMake/HelixBindgen.cmake` | `helix_bindgen()` |
| `Tests/` | A sample module with every shape, its C++ registration tests, and a C# program calling it both ways |

## Tags

The tags are in `Helix/Scripting/Bindings/Tags.h` and expand to nothing:

```cpp
HX_CLASS(Root) class Thing { HX_PROPERTY() StringView name() const; void SetName(StringView name); ... };
HX_CLASS() class Mover : public Thing, public IDamageable
{
public:
    HX_FUNCTION(Constructor) static Mover* Create(StringView name);
    HX_FIELD() f32 speed = 1;
    HX_FUNCTION(Out = "hit") bool Raycast(Vector3 direction, Hit& hit, f32 maxDistance = 100.0f);
    HX_FUNCTION(Generic = "T : Thing", Lists) static Array<Thing*> FindAll(scripting::TypeId type);
    HX_EVENT() Event<i32, const Vector3&> moved;
private:
    HX_FIELD(SerializeField) i32 secret_ = 7;
};
```

- Types: `HX_CLASS`, `HX_STRUCT` (plain fields, the same layout in C#, checked by static asserts and at load),
  `HX_ENUM`, `HX_INTERFACE`; nested types; `Namespace`, `Attributes` (C# attributes as text), `Sealed`, `Abstract`,
  `Internal` (an internal C# type), `Static`, and `Root` on the class whose ids identify every object below it (`ObjectHandles<Root>` gives ids, objects
  and dynamic types); `CSharpBase = "Namespace.Class"` puts a C# class written by hand between the C++ base's C# class
  and the generated one (it takes the wrapper's id in a `protected internal` constructor and has one without
  arguments; its abstract members are implemented in the generated class's partial part).
  `Owned` on a root whose objects nothing else owns: what a C# constructor makes is deleted after its C# object is
  collected (the finalizer queues it, `scripting::ReleaseCollected` deletes on the main thread), and C++ code holding
  one keeps it alive with `scripting::RetainWrapper` (a strong handle let go with `ReleaseWrapper`).
- Members: `HX_FIELD` (a C# property on classes, a field on structs; `ReadOnly`, `NonSerialized`, private ones with
  `SerializeField` are registered for serialization but not in C#), `HX_PROPERTY` on a getter (the setter `Set<Name>`
  is paired, or `Setter`; `SetterThrows` for a C# setter that throws NotImplementedException), `HX_FUNCTION`
  (overloads, default values, `Out`, references as `ref`, `Params` on a last array parameter C# takes as `params`, `Constructor` on a static factory, `Generic` on a function
  taking a type id, `Lists` for `List<T>` overloads of array results, `NativeArrays` for an internal overload taking
  `Helix.Collections.NativeArray<T>` for each span of plain values (its memory as it is), `Leaf` for calls without a GC transition, `Void`
  to drop a result the member does not have), `HX_EVENT` on an `Event<...>` or `Query<...>` (or a function returning
  one by reference; `Delegate` names the C# delegate type, `Action` and `Func` otherwise; `ApiName` its name in the
  C++ script API when C# written by hand has its C# name, on events, properties and methods), `Hidden`, `Name`.
- Values: the primitives, `bool`, `String` / `StringView`, enums, structs, objects of root classes (by pointer or
  reference; ids across), `Array<T>` and `Span<const T>` of values, strings and objects, `scripting::TypeId`
  (`System.Type`).
  A type the table does not know is an error with its place.

## Calls

- C# to C++: each member is an extern "C" thunk in a table the C# loader takes from the native module (with its count
  and a hash of its shape, so builds of different headers do not meet). A thunk answers a status (the object is gone,
  a null argument, ...) that C# turns into an exception; results go out through a pointer. Strings go in as UTF-16
  and out as UTF-8 in a per thread buffer; arrays are copied; objects are ids with their dynamic type, wrapped once per
  object. Event handlers are C# delegates held by GC handles; exceptions thrown in them are reported, never thrown
  into native code. A thunk of more than six parameters takes them as one array of 8 byte slots (the C# side and the
  C++ script API fill it), so every call fits the 12 integer slots the Web's interpreter passes.
- C++ to C#: `helix_bindgen(... ASSEMBLY ... PROXIES ...)` writes a value class per C# class (it holds a GC handle;
  copies hold their own; no pointers), with its constructors (`New`), methods, statics, and its properties and fields
  as members read and written as in C# (`counter.Scale = 2.5f; f32 s = counter.Scale;`). Failed calls are logged and
  answer the default value.
- Objects: one C# wrapper per native object, its GC handle in the root class's wrapper slot (weak for plain
  wrappers, strong for objects of C# classes deriving from C++ ones, so their state lives with the native object,
  weak again for owned roots);
  the root class has the reference's equality: a destroyed object is `== null` and false, `is null` sees only the
  C# reference; members of destroyed objects throw (through `Native.MakeException`).
- C# classes deriving from C++ classes (a class with a tagged C++ constructor): its tagged virtual functions are
  C# virtual methods; the object is made as a generated C++ subclass whose overrides call the C# overrides the C#
  class has (found once per class); base calls reach the C++ implementation; pure virtual functions are overridden
  in C# (`new` of the class itself throws). A virtual getter tagged `HX_PROPERTY` without a setter is a C# virtual
  property C# classes override the same way (with a setter it is a plain property).
- A module's classes deriving from another module's (a game module's from the engine's): their C# wrappers derive
  from the other module's C# classes (wrapper constructors are protected where a class can be derived) and use that
  module's root wrapper functions (`Objects.RootOf`).
- Interfaces: a C++ interface (HX_INTERFACE) taken from C# is a native object that implements it or any C# object
  that does, through a generated adapter; a C# interface given to `PROXIES` is an abstract C++ class, and a C++
  object of it handed to C# arrives as a forwarding object, borrowed for the call.
- Events and delegates: C++ events reach C# delegates; C# events of proxied classes take C++ handlers
  (`counter.Changed.Add({function, user})`, `Remove(token)`), and Action / Func arguments take C++ functions.
- Registration: every type of the module in the scripting core with fields by offset or by accessor, methods,
  properties and events with invokers on Variants, so reflection, serialization and tools reach both languages alike.
  The types of the modules a module references (`REFERENCES`) are found by name, so a module in a binary of its own
  (a game module loaded at run time) knows them.
- The C++ script API (`helix_bindgen(... API)`, `<Namespace>/<Module>Api.h`): per class a value handle in its C#
  namespace that holds the object's id and calls the module's function table (the thunks C# calls): properties as
  members read and written as in C# (`m.speed = 2.5f; f32 s = m.speed;`, their types cannot be copied, so `auto` does
  not take them), methods with overloads and defaults, statics, constructors as `New`, generic members as templates
  (`GetComponent<T>()` gives a handle, or an `ObjectRef` for a C++ script class), out and ref parameters as
  references, the reference's null semantics (`if (go)`, `go == nullptr` once destroyed); value types and enums are
  aliases of the C++ types; the API headers of several modules go together (the engine's first; a class below a
  script base class is a handle of the nearest handle above it); a class tagged `ScriptBase` becomes the C++ class scripts derive from, with the members
  of its chain (and the API's names for the classes of that chain). Events take a `Callback` and answer a token for
  `Remove`. A failed call is logged and answers the default. A member tagged `Internal` is internal in C# (for C#
  written by hand next to it) and still in the C++ API. A member tagged `Protected` (or `ProtectedInternal`) is protected in C#: C#
  classes deriving from the class override it with the same access, and the native dispatch reaches it through an
  internal forwarder of the class.
  A nested type is named by its class in C# and the API (two `Direction` enums of two classes stay apart); types are
  found by their qualified C++ name, so two modules' classes of one name do not meet. A property whose name is a C++
  keyword (`template`) takes a trailing underscore in the API.
- Handle classes: a class of a game module deriving from another module's API handle by the API's name
  (`class Gauge : public ::HelixEngine::UI::Element`) is not bound: it is registered as a `NativeHandleClass` with
  every tag of the class, a constructor that adopts the object's id, and its tagged fields and properties (set
  through their setters) with setters by kind (bool, i32, i64, f32, f64, String, Color); the handle's module makes
  its objects (the UI's element types: `HX_CLASS(HxmlElement)`, `HX_FIELD(HxmlAttribute)`).
- Scripts: a class deriving from a class tagged `ScriptBase` (the engine's GameBehaviour or ScriptableObject) is a
  script class: its message methods (public or not, no virtual needed), its methods without arguments or with one
  bool, and its methods without arguments that answer a pointer (the pointee's name goes with them: the engine takes
  a pipeline asset's `RenderPipeline* CreatePipeline()` so) are registered as a `NativeScriptClass` with function
  pointers and the name of its script base; the tag argument `Order = <n>` is its DefaultExecutionOrder, `RequireComponent = "engine::RectTransform, ..."` the component types
  adding it adds first (its bases' too). Its base
  is the nearest script class (of this module or another one) or script base. A script class with tagged virtual
  functions also carries its generated C++ subclass, which the engine makes the objects of C# script classes below
  it as; a class the compiler finds abstract has no construct function (only the C# classes below it are made), and
  one that declares a pure virtual function has a protected C# constructor (others the public one C# scripts have).
  Generated C# of another assembly reaches the objects' ids through the interop interface `INativeObject`.
- Script fields: each script class gets a transfer function over Helix.Serialization's `DynamicVisitor` with its
  serialized `HX_FIELD` members (its module's script bases' first): public ones and private ones with
  `SerializeField`, not static, const, `ReadOnly` or `NonSerialized`; numbers, bools, enums, `String`, `Array<T>`, the
  module's `HX_STRUCT`s (their `TransferTraits` are generated; a member `Transfer` wins), types with `TransferTraits`
  of the headers the module includes, and object references: pointers to objects of root classes and value handles
  of the C++ script API (`HelixEngine::GameObject other;`), checked against the field's type when read. Fields C#
  does not reach through the bindings (value handles, reference wrappers such as `PPtr<T>`, arrays of them and of
  strings, structs tagged `HX_STRUCT(SerializedOnly)`, which are not bound and transfer through their own `Transfer`)
  are serialized and left out of C#.

Release, Ryzen 7 3750H, the sample module (its id lookup is a list search):

| C# calling C++ | Result |
|---|---|
| A field read (a property in C#) | 5.5 ns |
| A `Leaf` method with a struct argument and a float result | 10 ns |
| A string property read (UTF-8 out, a C# string made) | 50 ns |

## Use

```cmake
include(External/Helix.Bindgen/CMake/HelixBindgen.cmake)
helix_bindgen(my_module MODULE MyGame NAMESPACE MyGame HEADERS Source/Player.h INCLUDE_ROOTS Source CS_OUTPUT my_cs)
# A game module: the engine's headers as references, their types in the engine's C# namespace.
helix_bindgen(my_game MODULE MyGame KIND NativeGame HEADERS Source/Scripts.h REFERENCES ${engine_headers}
              REFERENCE_NAMESPACE HelixEngine)
helix_bindgen(my_module MODULE MyGame.Scripts ASSEMBLY ${scripts_dll} PROXIES MyGame.Inventory REFERENCES Source/Player.h
              CS_OUTPUT my_exports)
```

## Build

```
git clone https://github.com/kisstp2006/Helix.Bindgen
git -C Helix.Bindgen submodule update --init
cmake --preset windows-debug
cmake --build --preset windows-debug
ctest --preset windows-debug
```

The .NET SDK is found through `HELIX_DOTNET_ROOT`, `DOTNET_ROOT` or the path.

## License

GPL-3.0, see `LICENSE`.
