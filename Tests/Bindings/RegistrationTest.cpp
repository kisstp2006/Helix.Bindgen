#include <Sample/Sample.h>

#include <Helix/Scripting/Types/Invoke.h>
#include <Helix/Scripting/Types/TypeBuilder.h>
#include <Helix/Testing/Test.h>

#include <bit>

// The generated C++ of the sample module: its types in the scripting core, members by reflection (fields by offset
// and by accessor, the private serialized field, overloads, properties, statics, events) and the function table.
HX_BINDINGS_EXPORT bool HxBindings_Sample_Register();
HX_BINDINGS_EXPORT const void* const* HxBindings_Sample_Functions(helix::u32* count, helix::u64* hash);
HX_BINDINGS_EXPORT const helix::u32* HxBindings_Sample_Types(helix::u32* count);

using namespace helix;
using namespace helix::scripting;
using namespace helix::sample;

namespace
{
    // Every test starts from a registered module and no objects.
    struct Fixture
    {
        Fixture() { registered = HxBindings_Sample_Register(); }
        ~Fixture()
        {
            while (Thing* t = Thing::Find("probe"))
                delete t;
            while (Thing* t = Thing::Find("other"))
                delete t;
        }
        bool registered = false;
    };
}

HX_TEST(Bindings, TypesAreRegistered)
{
    Fixture f;
    HX_REQUIRE(f.registered);
    HX_CHECK(HxBindings_Sample_Register());
    TypeId mover = FindType("Sample.Mover");
    HX_REQUIRE(mover.IsValid());
    HX_CHECK(mover == TypeOf<Mover>());
    HX_CHECK(GetType(mover)->base == TypeOf<Thing>() && IsAssignableFrom(TypeOf<IDamageable>(), mover));
    HX_CHECK(GetType(TypeOf<IDamageable>())->kind == TypeKind::Interface && (GetType(TypeOf<IDamageable>())->flags & TypeAbstract));
    HX_CHECK(FindType("Sample.Mover.Mode") == TypeOf<Mover::Mode>());
    const TypeInfo* v3 = GetType(TypeOf<Vector3>());
    HX_CHECK(v3->kind == TypeKind::Struct && v3->size == 12 && (v3->flags & TypePlain) && Fields(TypeOf<Vector3>()).Size() == 3);
    Span<const EnumValueInfo> layers = EnumValues(TypeOf<Layers>());
    HX_CHECK(layers.Size() == 4 && layers[3].name == StringView("Both") && layers[3].value == 3);
    HX_CHECK(GetType(TypeOf<Layers>())->element == TypeOf<u8>());
    u32 count = 0;
    const u32* ids = HxBindings_Sample_Types(&count);
    HX_CHECK(count == 9);
    bool all = true;
    for (u32 i = 0; i < count; i++)
        all = all && GetType(TypeId{ids[i]}) != nullptr;
    HX_CHECK(all);
}

HX_TEST(Bindings, FieldsByOffsetAndByAccessor)
{
    Fixture f;
    HX_REQUIRE(f.registered);
    TypeId mover = TypeOf<Mover>();
    Mover* m = Mover::Create("probe");
    Mover* other = Mover::Create("other");
    String error;
    Variant v;

    const FieldInfo* speed = FindField(mover, "speed");
    HX_REQUIRE(speed != nullptr);
    HX_CHECK(speed->offset != kNoOffset && (speed->flags & MemberSerialized));
    HX_CHECK(SetField(*speed, m, Variant::From(3.5f), error) && m->speed == 3.5f);

    // The private serialized field: by offset, not public.
    const FieldInfo* secret = FindField(mover, "secret_");
    HX_REQUIRE(secret != nullptr);
    HX_CHECK(!(secret->flags & MemberPublic) && (secret->flags & MemberSerialized) && secret->offset != kNoOffset);
    HX_CHECK(GetField(*secret, m, v, error) && v.AsInt() == 7);
    HX_CHECK(SetField(*secret, m, Variant::From(i32(9)), error) && m->Secret() == 9);

    // An object field by accessors: the dynamic type comes back.
    const FieldInfo* target = FindField(mover, "target");
    HX_REQUIRE(target != nullptr);
    HX_CHECK(target->offset == kNoOffset);
    HX_CHECK(SetField(*target, m, Variant::Object(TypeOf<Thing>(), static_cast<Thing*>(other)), error) && m->target == other);
    HX_CHECK(GetField(*target, m, v, error) && v.AsObject() == other && v.Type() == mover);

    const FieldInfo* scores = FindField(mover, "scores");
    HX_REQUIRE(scores != nullptr);
    Array<Variant> items;
    items.Push(Variant::From(i32(4)));
    items.Push(Variant::From(i32(2)));
    HX_CHECK(SetField(*scores, m, Variant::Items(ArrayOf(TypeOf<i32>()), items), error) && m->scores.Size() == 2 && m->scores[1] == 2);
    HX_CHECK(GetField(*scores, m, v, error) && v.AsItems().Size() == 2);

    const FieldInfo* health = FindField(mover, "health");
    HX_REQUIRE(health != nullptr);
    HX_CHECK((health->flags & MemberReadOnly) && !(health->flags & MemberSerialized));

    const FieldInfo* created = FindField(mover, "created");
    HX_REQUIRE(created != nullptr);
    HX_CHECK((created->flags & MemberStatic) && GetField(*created, nullptr, v, error) && v.AsInt() == Mover::created);
}

HX_TEST(Bindings, MethodsPropertiesAndStatics)
{
    Fixture f;
    HX_REQUIRE(f.registered);
    TypeId mover = TypeOf<Mover>();
    Mover* m = Mover::Create("probe");
    String error;
    Variant r;

    Variant one[] = {Variant::From(Vector3{1, 2, 3})};
    HX_CHECK(Invoke(mover, "Translate", m, Span<const Variant>(one), r, error));
    Variant three[] = {Variant::From(1.0f), Variant::From(0.0f), Variant::From(0.0f)};
    HX_CHECK(Invoke(mover, "Translate", m, Span<const Variant>(three), r, error));
    HX_CHECK(m->position().x == 2 && m->position().y == 2 && m->position().z == 3);

    Variant describe[] = {Variant::Text("a "), Variant::Bool(true)};
    HX_CHECK(Invoke(mover, "Describe", m, Span<const Variant>(describe), r, error) && r.AsText() == StringView("a probe!"));

    const PropertyInfo* position = FindProperty(mover, "position");
    HX_REQUIRE(position != nullptr);
    HX_CHECK(SetProperty(*position, m, Variant::From(Vector3{7, 8, 9}), error) && m->position().z == 9);
    HX_CHECK(GetProperty(*position, m, r, error));
    Vector3 p;
    HX_CHECK(r.Get(p) && p.x == 7);
    const PropertyInfo* name = FindProperty(mover, "name");
    HX_CHECK(name != nullptr && GetProperty(*name, m, r, error) && r.AsText() == StringView("probe"));
    const PropertyInfo* lastHit = FindProperty(mover, "lastHit");
    HX_CHECK(lastHit != nullptr && lastHit->setter == kNoMember);

    HX_CHECK(Invoke(TypeOf<World>(), "Version", nullptr, Span<const Variant>(), r, error) && r.AsInt() == 3);
    Variant type[] = {Variant::OfType(mover)};
    HX_CHECK(Invoke(mover, "FindFirst", nullptr, Span<const Variant>(type), r, error) && r.AsObject() == m);
    Variant create[] = {Variant::Text("other")};
    HX_CHECK(Invoke(mover, "Create", nullptr, Span<const Variant>(create), r, error) && r.AsObject() != nullptr && r.Type() == mover);
    HX_CHECK(Thing::Count() == 2);
    // The reference argument must not be null.
    Variant link[] = {Variant::Object(TypeOf<Thing>(), nullptr)};
    HX_CHECK(!Invoke(mover, "Link", m, Span<const Variant>(link), r, error) && !error.Empty());
}

HX_TEST(Bindings, EventsByReflection)
{
    Fixture f;
    HX_REQUIRE(f.registered);
    TypeId mover = TypeOf<Mover>();
    Mover* m = Mover::Create("probe");
    String error;
    struct Counter
    {
        i32 steps = 0;
        f32 x = 0;
    } counter;
    using Callback = Event<i32, const Vector3&>::Callback;
    Callback cb = [](void* user, i32 steps, const Vector3& at) {
        static_cast<Counter*>(user)->steps += steps;
        static_cast<Counter*>(user)->x = at.x;
    };
    EventHandler handler{std::bit_cast<void*>(cb), &counter};
    Variant h = Variant::From(handler);
    HX_CHECK(AddHandler(mover, "moved", m, h, error));
    m->Move(2);
    HX_CHECK(counter.steps == 2 && counter.x == 2);
    Variant args[] = {Variant::From(i32(5)), Variant::From(Vector3{9, 0, 0})};
    HX_CHECK(RaiseEvent(mover, "moved", m, Span<const Variant>(args), error) && counter.steps == 7 && counter.x == 9);
    HX_CHECK(RemoveHandler(mover, "moved", m, h, error) && m->moved.Size() == 0);
    m->Move(1);
    HX_CHECK(counter.steps == 7);
}

HX_TEST(Bindings, FunctionTable)
{
    Fixture f;
    HX_REQUIRE(f.registered);
    u32 count = 0;
    u64 hash = 0;
    const void* const* table = HxBindings_Sample_Functions(&count, &hash);
    HX_CHECK(count > 40 && hash != 0 && table[count] == nullptr);
    HX_CHECK(NativeFunctions("Sample").Size() == count && NativeFunctions("Sample").Data() == table);
    bool filled = true;
    for (u32 i = 0; i < count; i++)
        filled = filled && table[i] != nullptr;
    HX_CHECK(filled);
}
