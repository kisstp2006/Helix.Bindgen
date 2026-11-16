using Sample;

namespace Scripts
{
    // A C# class below an owned root: its native object goes when the C# object is collected (unless C++ holds it).
    public class Fare : Ticket
    {
        public int extra;

        public Fare(int number, int extra) : base(number) => this.extra = extra;

        public override int Price() => base.Price() + extra;
    }
}
