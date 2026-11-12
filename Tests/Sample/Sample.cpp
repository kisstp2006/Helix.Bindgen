#include <Sample/Sample.h>

#include <cmath>

namespace helix::sample
{
    namespace
    {
        // Every live thing; ids are never reused.
        Array<Thing*>& Things()
        {
            static Array<Thing*> things;
            return things;
        }

        u64 gNextId = 0;
        f32 gGravity = -9.81f;
    }

    Thing::Thing(StringView name) : name_(name), id_(++gNextId) { Things().Push(this); }

    Thing::~Thing()
    {
        scripting::ReleaseWrapper(wrapper_);
        Array<Thing*>& things = Things();
        for (usize i = 0; i < things.Size(); i++)
            if (things[i] == this)
            {
                things.RemoveAt(i);
                break;
            }
    }

    i32 Thing::Count() { return i32(Things().Size()); }

    Thing* Thing::Find(StringView name)
    {
        for (Thing* t : Things())
            if (t->name() == name)
                return t;
        return nullptr;
    }

    void Thing::Destroy()
    {
        Mover::destroyed().Invoke(this);
        delete this;
    }

    scripting::TypeId Thing::HelixType() const { return scripting::TypeOf<Thing>(); }

    String Thing::Greeting(StringView to) const
    {
        String s("hi ");
        s.Append(to);
        return s;
    }

    i32 Thing::TotalWeight()
    {
        i32 total = 0;
        for (Thing* t : Things())
            total += t->Weight();
        return total;
    }

    String Thing::GreetAll(StringView to)
    {
        String all;
        for (Thing* t : Things())
        {
            all.Append(t->Greeting(to));
            all.Append(";");
        }
        return all;
    }

    bool Mover::HurtIfAny(IDamageable* target, i32 amount)
    {
        if (!target)
            return false;
        target->Damage(amount);
        return true;
    }

    scripting::TypeId Shape::HelixType() const { return scripting::TypeOf<Shape>(); }

    f32 Shape::TotalArea()
    {
        f32 total = 0;
        for (Thing* t : Things())
            if (scripting::IsAssignableFrom(scripting::TypeOf<Shape>(), t->HelixType()))
                total += static_cast<Shape*>(t)->Area();
        return total;
    }

    void Mover::Translate(Vector3 translation) { Translate(translation, Space::World); }

    void Mover::Translate(f32 x, f32 y, f32 z) { Translate(Vector3{x, y, z}); }

    void Mover::Translate(const Vector3& translation, Space relativeTo)
    {
        f32 scale = relativeTo == Space::Self ? speed : 1.0f;
        position_.x += translation.x * scale;
        position_.y += translation.y * scale;
        position_.z += translation.z * scale;
    }

    f32 Mover::Distance(const Vector3& to) const
    {
        f32 dx = to.x - position_.x, dy = to.y - position_.y, dz = to.z - position_.z;
        return std::sqrt(dx * dx + dy * dy + dz * dz);
    }

    // A hit on the ground plane (y = 0) along the direction.
    bool Mover::Raycast(Vector3 direction, Hit& hit, f32 maxDistance)
    {
        hit = Hit{};
        if (direction.y >= 0 || position_.y <= 0)
            return false;
        f32 t = -position_.y / direction.y;
        f32 length = std::sqrt(direction.x * direction.x + direction.y * direction.y + direction.z * direction.z);
        if (t * length > maxDistance)
            return false;
        hit.point = Vector3{position_.x + direction.x * t, 0, position_.z + direction.z * t};
        hit.distance = t * length;
        hit.valid = true;
        hit.index = 1;
        lastHit_ = hit;
        return true;
    }

    void Mover::Accumulate(i32& total) const { total += health; }

    bool Mover::TryNearest(Thing*& nearest) const
    {
        nearest = nullptr;
        for (Thing* t : Things())
            if (t != this)
            {
                nearest = t;
                return true;
            }
        return false;
    }

    i32 Mover::Sum(Span<const i32> values) const
    {
        i32 total = 0;
        for (i32 v : values)
            total += v;
        return total;
    }

    Array<Vector3> Mover::Path(i32 steps) const
    {
        Array<Vector3> path;
        for (i32 i = 0; i < steps; i++)
            path.Push(Vector3{position_.x + f32(i), position_.y, position_.z});
        return path;
    }

    String Mover::Describe(StringView prefix, bool loud) const
    {
        String s(prefix);
        s.Append(name());
        if (loud)
            s.Append("!");
        return s;
    }

    void Mover::Damage(i32 amount) { health -= amount; }

    void Mover::Link(Thing& other) { target = &other; }

    i32 Mover::CountAlive(const Array<Thing*>& things) const
    {
        i32 n = 0;
        for (Thing* t : things)
            n += t != nullptr;
        return n;
    }

    scripting::TypeId Mover::KindOf() const { return HelixType(); }

    Array<Thing*> Mover::Neighbours() const
    {
        Array<Thing*> list;
        for (Thing* t : Things())
            if (t != this)
                list.Push(t);
        return list;
    }

    Thing* Mover::FindFirst(scripting::TypeId type)
    {
        for (Thing* t : Things())
            if (scripting::IsAssignableFrom(type, t->HelixType()))
                return t;
        return nullptr;
    }

    Array<Thing*> Mover::FindAll(scripting::TypeId type)
    {
        Array<Thing*> list;
        for (Thing* t : Things())
            if (scripting::IsAssignableFrom(type, t->HelixType()))
                list.Push(t);
        return list;
    }

    bool Mover::TryFind(scripting::TypeId type, Thing*& found)
    {
        found = FindFirst(type);
        return found != nullptr;
    }

    void Mover::Move(i32 steps, Space space)
    {
        Translate(Vector3{f32(steps), 0, 0}, space);
        moved.Invoke(steps, position_);
    }

    bool Mover::Rename(StringView name)
    {
        if (!allowRename.Ask(name))
            return false;
        SetName(name);
        return true;
    }

    Event<Thing*>& Mover::destroyed()
    {
        static Event<Thing*> e;
        return e;
    }

    scripting::TypeId Mover::HelixType() const { return scripting::TypeOf<Mover>(); }

    f32 World::gravity() { return gGravity; }

    void World::SetGravity(f32 value) { gGravity = value; }

    Query<>& World::wantsToQuit()
    {
        static Query<> q;
        return q;
    }

    bool World::Quit() { return wantsToQuit().Ask(); }
}

helix::sample::Thing* helix::scripting::ObjectHandles<helix::sample::Thing>::FromId(u64 id)
{
    for (sample::Thing* t : sample::Things())
        if (t->Id() == id)
            return t;
    return nullptr;
}
