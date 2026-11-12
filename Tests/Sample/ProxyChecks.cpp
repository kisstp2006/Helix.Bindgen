#include <HelixBindgen_Scripts.Proxies.h>

#include <Helix/Core/Logging/Log.h>

#include <cstdio>

// C++ using the C# class Scripts.Counter through its generated proxy (run by the C# test program once it handed over
// the exports): the number of failed checks.
using namespace helix;
using namespace helix::sample;

namespace
{
    i32 gFailures = 0;
    i32 gErrors = 0;

    void Check(bool ok, const char* what)
    {
        if (!ok)
        {
            gFailures++;
            std::printf("FAILED (C++): %s\n", what);
        }
    }

    void CountErrors(const LogMessage& message, void*)
    {
        if (message.level == LogLevel::Error)
            gErrors++;
    }
}

HX_BINDINGS_EXPORT i32 SampleRunProxies()
{
    gFailures = 0;
    using Scripts::Counter;
    using Scripts::Mood;

    i32 before = Counter::Instances;
    Counter c = Counter::New(5);
    Check(bool(c) && Counter::Instances == before + 1, "a constructor and a static field");
    Check(c.Add(3) == 8 && c.Add(1, 1) == 10 && c.value == 10, "methods, an overload and a field");
    c.value = 120;
    Check(c.IsBig() && c.value.Get() == 120, "a field set from C++");
    c.Scale = 2.5f;
    f32 scale = c.Scale;
    Check(scale == 2.5f, "a property set and read");
    Counter::Rate = 4.0f;
    Check(Counter::Rate == 4.0f, "a static property");
    Check(Counter::Kind.Get() == "counter", "a static read only field");
    Check(Counter::Greet("ann") == "hello ann", "a static method with strings");
    c.Relabel("f\xC3\xBCr");
    Check(c.Label.Get() == "f\xC3\xBCr", "a string through UTF-8 and UTF-16");
    Check(c.Initial("xyz") == u'x' && c.Initial("") == u'?', "a char result");
    c.mood = Mood::Angry;
    Check(c.mood == Mood::Angry && i32(Mood::Angry) == -1, "an enum of the assembly");

    i32 values[] = {1, 2, 3, 4};
    Check(c.Sum(Span<const i32>(values)) == 10, "an array argument");
    Array<i32> digits = c.Digits();
    Check(digits.Size() == 3 && digits[0] == 1 && digits[2] == 0, "an array result");
    i32 a = 1, b = 2;
    c.Swap(a, b);
    i32 half = 0;
    Check(a == 2 && b == 1 && c.TryHalf(8, half) && half == 4, "ref and out arguments");

    Counter copy = c;
    Check(copy.SameAs(c) && c.SameAs(copy), "a copied proxy holds the same C# object");
    Counter clone = c.Clone();
    Check(bool(clone) && !clone.SameAs(c) && clone.value == 120 && clone.mood == Mood::Angry, "a proxy result");
    Counter moved = static_cast<Counter&&>(clone);
    Check(!clone && bool(moved), "a moved proxy");
    Counter none = Counter::Null();
    Check(!none && none == nullptr, "a null proxy");

    Mover* m = c.Spawn("spawned");
    Check(m != nullptr && m->name() == "spawned" && Thing::Find("spawned") == m && m->position().x == 120, "a native object made in C#");
    Check(c.Describe(m) == "spawned at 120", "a native object to C#");
    Check(c.Describe(nullptr) == "none", "a null native object to C#");
    Vector3 v = c.Twice(Vector3{1, 2, 3});
    Check(v.x == 2 && v.y == 4 && v.z == 6, "a native struct both ways");

    // Failures are logged, not thrown: a C# exception and a call on a null proxy.
    gErrors = 0;
    AddLogSink(&CountErrors, nullptr);
    c.Fail();
    Check(none.Add(1) == 0, "a null proxy's call answers the default");
    RemoveLogSink(&CountErrors, nullptr);
    Check(gErrors == 2, "failed calls are logged");

    if (m)
        m->Destroy();

    // A C# interface implemented in C++, handed to C# for the call (and not after it).
    struct Scorer final : Scripts::IScorer
    {
        i32 calls = 0;
        i32 Score(i32 value) override
        {
            calls++;
            return value * 3;
        }
        String Label(StringView prefix) override
        {
            String s(prefix);
            s.Append("triple");
            return s;
        }
        bool Accepts(char16_t c) override { return c == u'x'; }
    } scorer;
    Check(c.UseScorer(scorer, 5) == 15 && scorer.calls == 1, "C# calls a C++ implementation of its interface");
    Check(c.Describe(scorer) == "score: triple6", "strings both ways through the interface");
    c.Keep(scorer);
    gErrors = 0;
    AddLogSink(&CountErrors, nullptr);
    Check(c.UseKept() == 0, "a C++ object kept by C# after the call is not reached");
    RemoveLogSink(&CountErrors, nullptr);
    Check(gErrors == 1 && scorer.calls == 2, "the use of a kept C++ object fails in C# and is logged");

    // C# events and delegates given C++ functions.
    struct Seen
    {
        i32 last = 0;
        i32 calls = 0;
        i32 trues = 0;
    } seen;
    u64 token = c.Changed.Add({[](void* user, i32 v) {
                                   static_cast<Seen*>(user)->last = v;
                                   static_cast<Seen*>(user)->calls++;
                               },
                               &seen});
    c.value = 1;
    c.Increment(4);
    Check(token != 0 && seen.last == 5 && seen.calls == 1, "a C++ handler of a C# event");
    c.Changed.Remove(token);
    c.Increment(1);
    Check(seen.calls == 1, "a removed C++ handler is not called");
    u64 reset = Counter::Reset.Add({[](void* user) { static_cast<Seen*>(user)->calls += 10; }, &seen});
    Counter::FireReset();
    Counter::Reset.Remove(reset);
    Counter::FireReset();
    Check(seen.calls == 11, "a C++ handler of a static C# event");
    Check(c.Apply({[](void* user, i32 v) { return v * *static_cast<i32*>(user); }, &half}, 7) == 7 * half, "a C++ function as a Func");
    c.Each({[](void* user, i32, bool even) { static_cast<Seen*>(user)->trues += even ? 1 : 0; }, &seen});
    Check(seen.trues == 2, "a C++ function as an Action with a bool");
    return gFailures;
}
