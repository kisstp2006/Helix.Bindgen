namespace Sample
{
    // The C# side of a C++ class bound by hand: its objects by native id.
    public sealed class Badge
    {
        public readonly ulong id;

        Badge(ulong id) => this.id = id;

        public static void Register() => global::Helix.Interop.Handles.Register(typeof(Badge), (id, type) => new Badge(id));
    }
}
