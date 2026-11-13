#pragma once

#include <Helix/Core/Containers/Array.h>
#include <Helix/Core/Strings/String.h>
#include <Helix/Scripting/Bindings/Interop.h>
#include <Helix/Scripting/Bindings/Tags.h>
#include <Helix/Scripting/Types/Type.h>

#include <Sample/SampleEvents.h>

// A module of tagged types with every shape the bindings reproduce: structs and enums (nested too), a root class
// whose objects cross as ids, a derived class with fields, properties, overloads, defaults, out and ref arguments,
// strings, arrays, objects, type ids, generic methods, list overloads, events and queries, an interface, a private
// serialized field, a constructor, and a class of static members only.
namespace helix::sample
{
    HX_STRUCT()
    struct Vector3
    {
        HX_FIELD() f32 x = 0;
        HX_FIELD() f32 y = 0;
        HX_FIELD() f32 z = 0;
    };

    HX_STRUCT()
    struct Hit
    {
        HX_FIELD() Vector3 point;
        HX_FIELD() f32 distance = 0;
        HX_FIELD() bool valid = false;
        HX_FIELD() i32 index = -1;
    };

    HX_ENUM()
    enum class Space : i32
    {
        World = 0,
        Self = 1,
    };

    HX_ENUM(Attributes = "[System.Flags]")
    enum class Layers : u8
    {
        None = 0,
        Ground = 1,
        Water = 1 << 1,
        Both = Ground | Water,
    };

    HX_INTERFACE()
    class IDamageable
    {
    public:
        virtual ~IDamageable() = default;
        HX_FUNCTION() virtual void Damage(i32 amount) = 0;
    };

    HX_CLASS(Root)
    class Thing
    {
    public:
        explicit Thing(StringView name);
        virtual ~Thing();
        Thing(const Thing&) = delete;
        Thing& operator=(const Thing&) = delete;

        HX_PROPERTY() StringView name() const { return name_; }
        void SetName(StringView name) { name_ = String(name); }

        HX_FUNCTION() static i32 Count();
        HX_FUNCTION() static Thing* Find(StringView name);
        HX_FUNCTION() void Destroy();

        // Virtual functions C# classes override, called from C++.
        HX_FUNCTION() virtual i32 Weight() const { return 1; }
        HX_FUNCTION() virtual String Greeting(StringView to) const;
        HX_FUNCTION() static i32 TotalWeight();
        HX_FUNCTION() static String GreetAll(StringView to);

        u64 Id() const { return id_; }
        u64& Wrapper() { return wrapper_; }
        virtual scripting::TypeId HelixType() const;

    private:
        String name_;
        u64 id_ = 0;
        u64 wrapper_ = 0;
    };

    HX_CLASS()
    class Mover : public Thing, public IDamageable
    {
    public:
        HX_ENUM()
        enum class Mode : i32
        {
            Walk,
            Run,
        };

        HX_FUNCTION(Constructor) explicit Mover(StringView name) : Thing(name) { created++; }

        static Mover* Create(StringView name) { return new Mover(name); }

        HX_FIELD() f32 speed = 1;
        HX_FIELD() Mode mode = Mode::Walk;
        HX_FIELD() Layers layers = Layers::Ground;
        HX_FIELD() Thing* target = nullptr;
        HX_FIELD() Array<i32> scores;
        HX_FIELD(ReadOnly) i32 health = 100;
        HX_FIELD() static inline i32 created = 0;

        HX_PROPERTY() Vector3 position() const { return position_; }
        void SetPosition(Vector3 value) { position_ = value; }
        HX_PROPERTY(ReadOnly) Hit lastHit() const { return lastHit_; }
        // Settable in C# by its signature, not by its object.
        HX_PROPERTY(SetterThrows) i32 laps() const { return 2; }

        HX_FUNCTION() void Translate(Vector3 translation);
        HX_FUNCTION() void Translate(f32 x, f32 y, f32 z);
        HX_FUNCTION() void Translate(const Vector3& translation, Space relativeTo);
        HX_FUNCTION(Leaf) f32 Distance(const Vector3& to) const;
        HX_FUNCTION(Out = "hit") bool Raycast(Vector3 direction, Hit& hit, f32 maxDistance = 100.0f);
        HX_FUNCTION() void Accumulate(i32& total) const;
        HX_FUNCTION(Out = "nearest") bool TryNearest(Thing*& nearest) const;
        HX_FUNCTION() i32 Sum(Span<const i32> values) const;
        HX_FUNCTION() Array<Vector3> Path(i32 steps) const;
        HX_FUNCTION() String Describe(StringView prefix, bool loud = false) const;
        HX_FUNCTION() Array<String> Words(i32 count) const;
        HX_FUNCTION() i32 Letters(Span<const String> words) const;
        // The result is a native status the bound member does not have.
        HX_FUNCTION(Void) bool Relabel(StringView value);
        HX_FUNCTION() void Damage(i32 amount) override;
        HX_FUNCTION() i32 Weight() const override { return 10; }
        HX_FUNCTION() virtual void OnHit(i32 damage) { health -= damage; }
        HX_FUNCTION() void TakeHit(i32 damage) { OnHit(damage); }
        // Interfaces from C#: a native object that implements one, or a C# object.
        HX_FUNCTION() static void Hurt(IDamageable& target, i32 amount) { target.Damage(amount); }
        HX_FUNCTION() static bool HurtIfAny(IDamageable* target, i32 amount);
        HX_FUNCTION() void Link(Thing& other);
        HX_FUNCTION() i32 CountAlive(const Array<Thing*>& things) const;
        HX_FUNCTION() scripting::TypeId KindOf() const;
        HX_FUNCTION(Lists) Array<Thing*> Neighbours() const;
        HX_FUNCTION(Generic = "T : Thing") static Thing* FindFirst(scripting::TypeId type);
        HX_FUNCTION(Generic = "T : Thing", Lists) static Array<Thing*> FindAll(scripting::TypeId type);
        HX_FUNCTION(Out = "found", Generic = "T : Thing") static bool TryFind(scripting::TypeId type, Thing*& found);
        HX_FUNCTION() void Move(i32 steps, Space space = Space::World);
        HX_FUNCTION() bool Rename(StringView name);
        HX_FUNCTION(Hidden) i32 Secret() const { return secret_; }

        HX_EVENT() Event<i32, const Vector3&> moved;
        HX_EVENT() Query<StringView> allowRename;
        HX_EVENT() static Event<Thing*>& destroyed();

        scripting::TypeId HelixType() const override;

    private:
        Vector3 position_;
        Hit lastHit_;
        HX_FIELD(SerializeField) i32 secret_ = 7;
    };

    // Abstract in C++: made only as the subclass of a C# class that overrides Area.
    HX_CLASS()
    class Shape : public Thing
    {
    public:
        HX_FUNCTION(Constructor) explicit Shape(StringView name) : Thing(name) {}
        HX_FUNCTION() virtual f32 Area() const = 0;
        HX_FUNCTION() static f32 TotalArea();

        scripting::TypeId HelixType() const override;
    };

    HX_CLASS(Static)
    class World
    {
    public:
        HX_FUNCTION() static i32 Version() { return 3; }
        HX_PROPERTY() static f32 gravity();
        static void SetGravity(f32 value);
        HX_FIELD() static inline String motto = String("hello");
        HX_EVENT() static Query<>& wantsToQuit();
        HX_FUNCTION() static bool Quit();
    };
}

template <>
struct helix::scripting::ObjectHandles<helix::sample::Thing>
{
    static u64 ToId(const sample::Thing* thing) { return thing->Id(); }
    static sample::Thing* FromId(u64 id);
    static TypeId TypeOf(const sample::Thing* thing) { return thing->HelixType(); }
    static u64& WrapperOf(sample::Thing* thing) { return thing->Wrapper(); }
};
