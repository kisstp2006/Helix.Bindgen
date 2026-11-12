using Sample;

namespace Scripts
{
    // C# classes deriving from C++ classes: overrides C++ calls, base calls that reach the C++ implementation, and
    // state that lives in the C# object while the native object does.
    public class Runner : Mover
    {
        public int hits;
        public string log = "";

        public Runner(string name) : base(name) { }

        public override int Weight() => base.Weight() * 2 + 1;

        public override void OnHit(int damage)
        {
            hits += damage;
            base.OnHit(damage);
        }

        public override string Greeting(string to) => "yo " + to;

        public override void Damage(int amount)
        {
            log += "D" + amount;
            base.Damage(amount);
        }
    }

    // No overrides: C++ calls stay in C++.
    public class Plain : Mover
    {
        public Plain() : base("plain") { }
    }

    // A class that is abstract in C++.
    public class Circle : Shape
    {
        public float radius;

        public Circle(string name, float radius) : base(name) => this.radius = radius;

        public override float Area() => 3 * radius * radius;
    }

    // A C++ interface implemented by a C# object that is no native object.
    public class Shield : IDamageable
    {
        public int absorbed;

        public void Damage(int amount) => absorbed += amount;
    }
}
