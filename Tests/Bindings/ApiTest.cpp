// The generated C++ script API of the sample module.
#include <Sample/SampleApi.h>

#include <Helix/Testing/Test.h>

HX_BINDINGS_EXPORT bool HxBindings_Sample_Register();

using namespace helix;

namespace
{
    struct Fixture
    {
        Fixture() { registered = HxBindings_Sample_Register(); }
        ~Fixture()
        {
            for (const char* name : {"api", "api2"})
                while (sample::Thing* t = sample::Thing::Find(name))
                    delete t;
        }
        bool registered = false;
    };
}

HX_TEST(Api, HandlesPropertiesAndMethods)
{
    Fixture f;
    HX_REQUIRE(f.registered);
    Sample::Mover m = Sample::Mover::New("api");
    HX_REQUIRE(m && m != nullptr);
    // Properties read and written as in C#: a field, a property pair, an enum, a read only one.
    m.speed = 2.5f;
    f32 speed = m.speed;
    HX_CHECK(speed == 2.5f && String(m.name) == "api");
    m.name = "api2";
    HX_CHECK(String(m.name) == "api2" && sample::Thing::Find("api2") != nullptr);
    m.mode = Sample::Mover::Mode::Run;
    HX_CHECK(Sample::Mover::Mode(m.mode) == Sample::Mover::Mode::Run && i32(m.health) == 100);
    m.position = Sample::Vector3{1, 2, 3};
    Sample::Vector3 p = m.position;
    HX_CHECK(p.x == 1 && p.y == 2 && p.z == 3);
    // Overloads, defaults, out and ref parameters, spans and arrays.
    m.Translate(Sample::Vector3{1, 0, 0});
    m.Translate(0, 1, 0);
    p = m.position;
    HX_CHECK(p.x == 2 && p.y == 3);
    Sample::Hit hit;
    HX_CHECK(m.Raycast(Sample::Vector3{0, -1, 0}, hit) && hit.valid && hit.distance == 3.0f);
    HX_CHECK(!m.Raycast(Sample::Vector3{0, -1, 0}, hit, 1.0f) && !hit.valid);
    i32 total = 5;
    m.Accumulate(total);
    HX_CHECK(total > 5);
    const i32 values[] = {1, 2, 3};
    HX_CHECK(m.Sum(Span<const i32>(values, 3)) == 6 && m.Path(3).Size() == 3);
    HX_CHECK(StringView(m.Describe("x")).StartsWith("x") && m.Describe("x", true) != m.Describe("x"));
    // Arrays of strings both ways, and a dropped result.
    auto words = m.Words(3);
    HX_CHECK(words.Size() == 3 && StringView(words[1]).EndsWith("!"));
    HX_CHECK(m.Letters(words) == i32(StringView(words[0]).Size() * 3 + 1));
    m.Relabel("renamed");
    HX_CHECK(StringView(m.Describe("")) == "renamed");
    // Objects across: a handle in, handles out, a destroyed object is null.
    Sample::Mover other = Sample::Mover::New("api");
    m.Link(other);
    Sample::Thing nearest;
    HX_CHECK(m.TryNearest(nearest) && nearest == other);
    Sample::Thing found = Sample::Thing::Find("api");
    HX_CHECK(found == other && Sample::Thing::Count() >= 2);
    other.Destroy();
    HX_CHECK(!other && other == nullptr && !found);
}

HX_TEST(Api, StaticsAndGenerics)
{
    Fixture f;
    HX_REQUIRE(f.registered);
    // Static properties and fields, a static class.
    Sample::World::gravity = -3.0f;
    HX_CHECK(f32(Sample::World::gravity) == -3.0f && Sample::World::Version() == 3);
    Sample::World::motto = "hi";
    HX_CHECK(String(Sample::World::motto) == "hi");
    i32 before = Sample::Mover::created;
    Sample::Mover a = Sample::Mover::New("api");
    HX_CHECK(i32(Sample::Mover::created) == before + 1);
    // Generic members as templates over the handle types.
    Sample::Mover first = Sample::Mover::FindFirst<Sample::Mover>();
    HX_CHECK(first);
    HX_CHECK(Sample::Mover::FindAll<Sample::Mover>().Size() >= 1);
    Array<Sample::Mover> all;
    Sample::Mover::FindAll<Sample::Mover>(all);
    Sample::Mover again;
    HX_CHECK(!all.Empty() && Sample::Mover::TryFind<Sample::Mover>(again) && again);
    // A failed call answers the default and logs it: a member of a destroyed object.
    a.Destroy();
    for (const Sample::Mover& m : all)
        if (m)
            m.Destroy();
    HX_CHECK(f32(a.speed) == 0.0f);
}
