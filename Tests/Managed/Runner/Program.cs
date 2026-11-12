using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Helix.Interop;
using Helix.Interop.Generated;
using Sample;

namespace Helix.Bindgen.Tests
{
    // C# calling the sample module through its generated bindings: every shape of member, both directions of events,
    // object identity and the errors of calls on destroyed objects. The argument is the path of the native library.
    static class Program
    {
        static int s_checks;
        static int s_failures;

        static void Check(bool ok, string what)
        {
            s_checks++;
            if (!ok)
            {
                s_failures++;
                Console.Error.WriteLine("FAILED: " + what);
            }
        }

        static void Throws<T>(Action action, string what) where T : Exception
        {
            try
            {
                action();
                Check(false, what + " (no exception)");
            }
            catch (T)
            {
                Check(true, what);
            }
        }

        static bool Same(Vector3 a, float x, float y, float z) => a.x == x && a.y == y && a.z == z;

        static int Main(string[] args)
        {
            IntPtr library = NativeLibrary.Load(args[0]);
            Func<string, IntPtr> export = name => NativeLibrary.TryGetExport(library, name, out IntPtr p) ? p : IntPtr.Zero;
            HxModule_Sample.Load(export);

            Types();
            ObjectsAndFields();
            CallsAndArguments();
            ArraysAndGenerics();
            Events();
            Statics();
            Timings();
            Destroyed();
            Proxies(export);

            Console.WriteLine($"{s_checks - s_failures} of {s_checks} checks passed");
            return s_failures == 0 ? 0 : 1;
        }

        static void Types()
        {
            Check(Interop.Types.FromNative(Interop.Types.Of<Mover>()) == typeof(Mover), "native type ids map back to C# types");
            Check(Interop.Types.Of<Thing>() != Interop.Types.Of<Mover>() && Interop.Types.Of<Vector3>() != 0, "every type has its own id");
            Throws<ArgumentException>(() => Interop.Types.Of(typeof(Program)), "a type of no module has no id");
        }

        static void ObjectsAndFields()
        {
            var m = new Mover("runner");
            Check(Thing.Count() == 1 && Mover.created == 1, "the constructor made one native object");
            Check(m.name == "runner", "a string property");
            m.name = "f\u00fcr \u4e16\u754c \U0001F600";
            Check(m.name == "f\u00fcr \u4e16\u754c \U0001F600", "strings cross as UTF-16 in and UTF-8 out");
            Check(ReferenceEquals(Thing.Find(m.name), m), "an object found natively is the same wrapper");
            Check(Thing.Find("nobody") == null, "a null object");
            m.name = "runner";

            Check(m.speed == 1, "a field's default");
            m.speed = 2.5f;
            m.mode = Mover.Mode.Run;
            m.layers = Layers.Both;
            m.scores = new[] { 3, 1, 4 };
            Check(m.speed == 2.5f && m.mode == Mover.Mode.Run && m.layers == (Layers.Ground | Layers.Water), "fields of numbers and enums");
            int[] scores = m.scores;
            Check(scores.Length == 3 && scores[0] == 3 && scores[2] == 4, "an array field");
            Check(m.health == 100, "a read only field");

            m.position = new Vector3 { x = 1, y = 2, z = 3 };
            Check(Same(m.position, 1, 2, 3), "a struct property");
            Check(!m.lastHit.valid && m.lastHit.index == -1, "a read only struct property");
            Check(m.target == null, "an object field");
        }

        static Mover s_runner;
        static Mover s_other;

        static void CallsAndArguments()
        {
            Mover m = s_runner = (Mover)Thing.Find("runner");
            m.position = default;
            m.Translate(new Vector3 { x = 1 });
            m.Translate(0, 1, 0);
            m.Translate(new Vector3 { z = 2 }, Space.Self);
            Check(Same(m.position, 1, 1, 5), "overloads (a struct, three floats, a struct and an enum)");
            Check(m.Distance(new Vector3 { x = 1, y = 1, z = 1 }) == 4, "a leaf call");

            m.position = new Vector3 { y = 10 };
            bool hit = m.Raycast(new Vector3 { y = -1 }, out Hit h);
            Check(hit && h.valid && h.distance == 10 && h.point.y == 0 && h.index == 1, "an out struct and a default argument");
            Check(!m.Raycast(new Vector3 { y = -1 }, out h, 5) && !h.valid, "the default replaced");
            Check(m.lastHit.valid && m.lastHit.distance == 10, "the struct the call kept");

            int total = 1;
            m.Accumulate(ref total);
            Check(total == 101, "a ref argument");

            Check(m.Describe("hi ") == "hi runner" && m.Describe("hey ", true) == "hey runner!", "string results and a bool default");
            Check(m.Sum(new[] { 1, 2, 3, 4 }) == 10 && m.Sum(Array.Empty<int>()) == 0 && m.Sum(null) == 0, "a span argument");

            IDamageable d = m;
            d.Damage(10);
            Check(m.health == 90, "a member through an interface");

            s_other = new Mover("other");
            Check(m.TryNearest(out Thing near) && ReferenceEquals(near, s_other), "an out object is the same wrapper");
            Throws<ArgumentNullException>(() => m.Link(null), "a null object for a reference");
            m.Link(s_other);
            Check(ReferenceEquals(m.target, s_other), "an object argument and field");
            Check(m.CountAlive(new Thing[] { m, null, s_other }) == 2, "an array of objects");
            Check(m.KindOf() == typeof(Mover), "a type id result");
        }

        static void ArraysAndGenerics()
        {
            Mover m = s_runner;
            m.position = new Vector3 { x = 5 };
            Vector3[] path = m.Path(3);
            Check(path.Length == 3 && path[2].x == 7, "an array of structs");

            Thing[] neighbours = m.Neighbours();
            Check(neighbours.Length == 1 && ReferenceEquals(neighbours[0], s_other), "an array of objects returned");
            var list = new List<Thing> { m };
            m.Neighbours(list);
            Check(list.Count == 1 && ReferenceEquals(list[0], s_other), "the list overload fills the list");

            Mover first = Mover.FindFirst<Mover>();
            Check(first != null, "a generic method takes typeof(T)");
            Check(Mover.FindFirst(typeof(Mover)) == first, "and the form taking a Type");
            Mover[] all = Mover.FindAll<Mover>();
            Check(all.Length == 2, "a generic array result");
            var movers = new List<Mover>();
            Mover.FindAll<Mover>(movers);
            Check(movers.Count == 2, "a generic list overload");
            Check(Mover.TryFind<Mover>(out Mover found) && found != null, "a generic out argument");
        }

        static void Events()
        {
            Mover m = s_runner;
            int steps = 0;
            Vector3 at = default;
            Action<int, Vector3> moved = (s, p) =>
            {
                steps += s;
                at = p;
            };
            m.moved += moved;
            m.Move(2);
            Check(steps == 2 && Same(at, m.position.x, m.position.y, m.position.z), "an event with a struct argument");
            m.moved -= moved;
            m.Move(1);
            Check(steps == 2, "a removed handler is not called");

            m.allowRename += name => name != "forbidden";
            Check(!m.Rename("forbidden") && m.name == "runner", "a query says no");
            Check(m.Rename("runner"), "a query says yes");

            int reported = 0;
            Action<Exception> report = Native.ReportException;
            Native.ReportException = e => reported++;
            Action<int, Vector3> throwing = (s, p) => throw new InvalidOperationException("from a handler");
            m.moved += throwing;
            m.Move(1);
            m.moved -= throwing;
            Native.ReportException = report;
            Check(reported == 1, "an exception in a handler is reported, not thrown into native code");
        }

        static void Statics()
        {
            Check(World.Version() == 3, "a static method");
            World.gravity = -1.5f;
            Check(World.gravity == -1.5f, "a static property");
            Check(World.motto == "hello", "a static field");
            World.motto = "bye";
            Check(World.motto == "bye", "a static field set");
            Check(World.Quit(), "a static query without handlers");
            Func<bool> stay = () => false;
            World.wantsToQuit += stay;
            Check(!World.Quit(), "a static query with a handler");
            World.wantsToQuit -= stay;
        }

        // What a call costs (printed, not checked: the machine decides).
        static void Timings()
        {
            Mover m = s_runner;
            const int n = 2_000_000;
            var to = new Vector3 { x = 1 };
            float sink = 0;
            for (int i = 0; i < 1000; i++)
                sink += m.Distance(to) + m.speed;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < n; i++)
                sink += m.Distance(to);
            double leaf = watch.Elapsed.TotalMilliseconds * 1e6 / n;
            watch.Restart();
            for (int i = 0; i < n; i++)
                sink += m.speed;
            double field = watch.Elapsed.TotalMilliseconds * 1e6 / n;
            watch.Restart();
            for (int i = 0; i < n / 10; i++)
                sink += m.name.Length;
            double text = watch.Elapsed.TotalMilliseconds * 1e6 / (n / 10);
            Console.WriteLine($"a leaf call {leaf:F1} ns, a field read {field:F1} ns, a string property read {text:F1} ns ({sink > 0})");
        }

        // C++ calling C#: the native checks of the proxies, once their exports are handed over.
        static unsafe void Proxies(Func<string, IntPtr> export)
        {
            HxProxies_HelixBindgen_Scripts.Register(export);
            var run = (delegate* unmanaged[Cdecl]<int>)export("SampleRunProxies");
            int instances = Scripts.Counter.Instances;
            int reported = 0;
            Action<Exception> report = Native.ReportException;
            Native.ReportException = e => reported++;
            int failures = run();
            Native.ReportException = report;
            Check(failures == 0, $"the C++ checks of the proxies ({failures} failed)");
            Check(reported == 1, "the exception C++ caused in C# is reported on the C# side");
            Check(Scripts.Counter.Instances == instances + 2 && Scripts.Counter.Rate == 4.0f, "C++ changed C# state");
            Check(Thing.Count() == 0, "the native object C# made for C++ is gone");
        }

        static void Destroyed()
        {
            string gone = null;
            Action<Thing> destroyed = t => gone = t.name;
            Mover.destroyed += destroyed;
            Mover other = s_other;
            other.Destroy();
            Mover.destroyed -= destroyed;
            Check(gone == "other" && Thing.Count() == 1, "a static event with an object, called before the object goes");
            Throws<NullReferenceException>(() => _ = other.name, "a destroyed object's members throw");
            s_runner.Destroy();
            Check(Thing.Count() == 0, "every object destroyed");
        }
    }
}
