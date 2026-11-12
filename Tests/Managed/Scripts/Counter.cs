using System;
using Sample;

namespace Scripts
{
    public enum Mood : short
    {
        Calm = 0,
        Happy = 2,
        Angry = -1,
    }

    // A C# class that C++ uses through its generated proxy: constructors, fields, properties, statics, overloads,
    // strings, chars, enums, arrays, ref and out arguments, other proxies, native objects and structs, an exception.
    public class Counter
    {
        public static int Instances;
        public static readonly string Kind = "counter";

        public int value;
        public Mood mood;

        public Counter() => Instances++;

        public Counter(int start)
        {
            value = start;
            Instances++;
        }

        public float Scale { get; set; } = 1;
        public string Label { get; private set; } = "none";
        public static float Rate { get; set; } = 0.5f;

        public int Add(int amount) => value += amount;
        public int Add(int a, int b) => value += a + b;
        public bool IsBig() => value > 100;
        public static string Greet(string name) => "hello " + name;
        public void Relabel(string label) => Label = label;
        public char Initial(string text) => string.IsNullOrEmpty(text) ? '?' : text[0];
        public int Sum(int[] values)
        {
            int total = 0;
            foreach (int v in values)
                total += v;
            return total;
        }
        public int[] Digits()
        {
            string s = Math.Abs(value).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var d = new int[s.Length];
            for (int i = 0; i < s.Length; i++)
                d[i] = s[i] - '0';
            return d;
        }
        public void Swap(ref int a, ref int b) => (a, b) = (b, a);
        public bool TryHalf(int n, out int half)
        {
            half = n / 2;
            return n % 2 == 0;
        }
        public Counter Clone() => new Counter(value) { mood = mood, Scale = Scale };
        public bool SameAs(Counter other) => ReferenceEquals(this, other);
        public static Counter Null() => null;

        // Native objects and structs of the sample module.
        public string Describe(Mover mover) => mover == null ? "none" : mover.name + " at " + mover.position.x.ToString(System.Globalization.CultureInfo.InvariantCulture);
        public Mover Spawn(string name) => new Mover(name) { position = new Vector3 { x = value } };
        public Vector3 Twice(Vector3 v) => new Vector3 { x = v.x * 2, y = v.y * 2, z = v.z * 2 };
        public void Fail() => throw new InvalidOperationException("thrown in C#");

        // Members a proxy cannot have (listed by Bindgen, not generated).
        public object Anything() => null;
        public T Generic<T>() => default;
        public event Action Changed
        {
            add { }
            remove { }
        }
    }
}
