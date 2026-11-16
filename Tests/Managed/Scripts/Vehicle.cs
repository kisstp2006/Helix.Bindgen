namespace Sample
{
    // A C# class written by hand between Thing's generated class and Car's (HX_CLASS(CSharpBase)): it takes the
    // wrapper's id (and has the constructor without arguments the generated constructors call) and declares what Car's
    // partial class implements.
    public abstract class Vehicle : Thing
    {
        protected Vehicle() { }
        protected internal Vehicle(global::Helix.Interop.ObjectId id) : base(id) { }

        public abstract int Wheels { get; }
    }

    public partial class Car
    {
        public override int Wheels => WheelCount();
    }
}
