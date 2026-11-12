using System.Text;
using Helix.Bindgen.Model;

namespace Helix.Bindgen.Generators;

/// <summary>Lines with indentation.</summary>
public sealed class Code
{
    readonly StringBuilder _sb = new();
    int _indent;

    public Code Line(string text = "") => Line(text, 0);

    /// <summary>A line indented `delta` levels from the current one (access specifiers).</summary>
    public Code Line(string text, int delta)
    {
        if (text.Length > 0)
            _sb.Append(' ', Math.Max(0, _indent + delta) * 4).Append(text);
        _sb.Append('\n');
        return this;
    }

    /// <summary>A line (unless empty) and an opening brace on its own line.</summary>
    public Code Open(string text = "")
    {
        if (text.Length > 0)
            Line(text);
        Line("{");
        _indent++;
        return this;
    }

    public Code Close(string text = "}")
    {
        _indent--;
        return Line(text);
    }

    public override string ToString() => _sb.ToString();
}

/// <summary>What both generators need to know of the module: every bound member with its mapping and thunk name.</summary>
public sealed class Binding
{
    public readonly ModuleModel Module;
    public readonly TypeMap Map;
    public readonly List<Thunk> Thunks = new();
    public readonly Dictionary<TypeModel, TypeModel?> Roots = new();

    public Binding(ModuleModel module)
    {
        Module = module;
        Map = new TypeMap(module);
        foreach (TypeModel t in module.Types)
            Roots[t] = FindRoot(t);
        Validate();
        Collect();
    }

    public string Ident(string text) => new string(text.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());

    // The bound base types of a type, nearest first.
    public IEnumerable<TypeModel> BaseChain(TypeModel t)
    {
        var seen = new HashSet<TypeModel>();
        TypeModel? at = t;
        while (at != null && seen.Add(at))
        {
            yield return at;
            at = at.Bases.Select(b => Map.Find(b)).FirstOrDefault(b => b != null && b.Kind == TypeKind.Class);
        }
    }

    public TypeModel? BaseOf(TypeModel t) => t.Bases.Select(b => Map.Find(b)).FirstOrDefault(b => b != null && b.Kind == TypeKind.Class);
    public IEnumerable<TypeModel> InterfacesOf(TypeModel t) => t.Bases.Select(b => Map.Find(b)).Where(b => b != null && b.Kind == TypeKind.Interface)!;

    TypeModel? FindRoot(TypeModel t) => t.Kind == TypeKind.Class ? BaseChain(t).LastOrDefault(b => b.Tag.Has("Root")) : null;

    /// <summary>A class whose objects cross the boundary (it has a Root among its bases).</summary>
    public bool IsObjectClass(TypeModel t) => Roots.TryGetValue(t, out TypeModel? r) && r != null;

    void Validate()
    {
        foreach (TypeModel t in Module.Types.Where(t => !t.External))
        {
            if (t.Kind == TypeKind.Struct)
            {
                Map.Layout(t);
                if (t.Methods.Count > 0 || t.Properties.Count > 0 || t.Events.Count > 0 || t.Fields.Any(f => f.Static))
                    throw new BindgenException(t.Where, $"{t.Name}: structs bind their instance fields only (their methods and static members are written in C#)");
            }
            foreach (FieldModel f in t.Fields.Where(f => f.Type.Name.EndsWith("StringView", StringComparison.Ordinal)))
                throw new BindgenException(f.Where, $"{t.Name}.{f.Name}: a StringView field does not own its text (use String)");
            if (t.Kind == TypeKind.Class && !IsObjectClass(t))
            {
                bool instance = t.Fields.Any(f => !f.Static) || t.Methods.Any(m => !m.Static && !m.Tag.Has("Constructor")) ||
                                t.Properties.Any(p => !p.Static) || t.Events.Any(e => !e.Static);
                if (instance)
                    throw new BindgenException(t.Where, $"{t.Name}: instance members need a class with a Root among its bases (objects cross as ids)");
            }
        }
    }

    // ----------------------------------------------------------------------------------------------- thunks

    void Collect()
    {
        foreach (TypeModel t in Module.Types.Where(t => !t.External && t.Kind is TypeKind.Class or TypeKind.Interface))
        {
            var used = new Dictionary<string, int>();
            string Name(string member)
            {
                string baseName = $"Hx_{Ident(Module.Name)}_{t.Ident}_{member}";
                used.TryGetValue(baseName, out int n);
                used[baseName] = n + 1;
                return n == 0 ? baseName : $"{baseName}_{n}";
            }
            if (t.Kind == TypeKind.Interface)
                continue;
            // Private fields (SerializeField) are serialized, not seen by C#; Hidden members are not in C#.
            foreach (FieldModel f in t.Fields.Where(f => !f.Tag.Has("Private") && !f.Tag.Has("Hidden")))
            {
                Mapping m = Map.Map(f.Type, f.Where, $"{t.Name}.{f.Name}");
                Thunks.Add(new Thunk { Kind = ThunkKind.FieldGet, Owner = t, Field = f, Type = m, Name = Name("get_" + f.Name), Static = f.Static });
                if (!f.Type.Const && !f.Tag.Has("ReadOnly"))
                    Thunks.Add(new Thunk { Kind = ThunkKind.FieldSet, Owner = t, Field = f, Type = m, Name = Name("set_" + f.Name), Static = f.Static });
            }
            foreach (PropertyModel p in t.Properties.Where(p => !p.Getter.Tag.Has("Hidden")))
            {
                Mapping m = Map.Map(p.Getter.Return, p.Getter.Where, $"{t.Name}.{p.CsName}");
                Thunks.Add(new Thunk { Kind = ThunkKind.Method, Owner = t, Method = p.Getter, Type = m, Name = Name("get_" + p.CsName), Static = p.Static, Property = p });
                if (p.Setter != null)
                    Thunks.Add(new Thunk { Kind = ThunkKind.Method, Owner = t, Method = p.Setter, Type = Map.Map(p.Setter.Return, p.Setter.Where, p.Setter.Name),
                                           Name = Name("set_" + p.CsName), Static = p.Static, Property = p, IsSetter = true });
            }
            foreach (MethodModel mm in t.Methods.Where(m => !m.Tag.Has("Hidden")))
                Thunks.Add(new Thunk { Kind = ThunkKind.Method, Owner = t, Method = mm, Type = Map.Map(mm.Return, mm.Where, $"{t.Name}.{mm.Name}"),
                                       Name = Name(mm.Name), Static = mm.Static || mm.Tag.Has("Constructor") });
            foreach (EventModel e in t.Events.Where(e => !e.Tag.Has("Hidden")))
            {
                Thunks.Add(new Thunk { Kind = ThunkKind.EventAdd, Owner = t, Event = e, Name = Name("add_" + e.CsName), Static = e.Static });
                Thunks.Add(new Thunk { Kind = ThunkKind.EventRemove, Owner = t, Event = e, Name = Name("remove_" + e.CsName), Static = e.Static });
            }
        }
        for (int i = 0; i < Thunks.Count; i++)
            Thunks[i].Index = i;
        // Both sides carry the hash of the table's shape: a C# assembly and a native module of different builds do
        // not meet.
        ulong h = 14695981039346656037ul;
        foreach (Thunk th in Thunks)
            foreach (char c in th.Name + "|" + th.Kind + "|" + Shape(th) + ";")
                h = (h ^ c) * 1099511628211ul;
        Hash = h;
    }

    public ulong Hash { get; private set; }

    /// <summary>The module's own types with bases and outer types first: the order of registration and of the type ids.</summary>
    public List<TypeModel> Ordered()
    {
        var result = new List<TypeModel>();
        var seen = new HashSet<TypeModel>();
        void Visit(TypeModel t)
        {
            if (!seen.Add(t))
                return;
            foreach (string b in t.Bases)
            {
                TypeModel? bm = Map.Find(b);
                if (bm != null && !bm.External)
                    Visit(bm);
            }
            if (t.Outer != null && !t.Outer.External)
                Visit(t.Outer);
            result.Add(t);
        }
        foreach (TypeModel t in Module.Types.Where(t => !t.External))
            Visit(t);
        return result;
    }

    string Shape(Thunk th)
    {
        string Of(Mapping m) => m.Kind + (m.ByRef ? "&" : "") + (m.Element != null ? "[" + Of(m.Element) + "]" : "");
        var parts = new List<string> { Of(th.Type) };
        if (th.Method != null)
            parts.AddRange(th.Method.Parameters.Select(p => Of(Map.Map(p.Type, th.Method.Where, p.Name))));
        if (th.Event != null)
            parts.AddRange(th.Event.Arguments.Select(a => Of(Map.Map(a, th.Event.Where, th.Event.CsName))));
        return string.Join(",", parts);
    }

    /// <summary>The C++ type of the type's ids: its Root class.</summary>
    public TypeModel RootOf(TypeModel t) => Roots.TryGetValue(t, out TypeModel? r) && r != null
        ? r
        : throw new BindgenException(t.Where, $"{t.Name} has no Root class among its bases");
}

public enum ThunkKind
{
    FieldGet,
    FieldSet,
    Method,
    EventAdd,
    EventRemove,
}

public sealed class Thunk
{
    public ThunkKind Kind;
    public int Index;
    public string Name = "";
    public TypeModel Owner = new();
    public FieldModel? Field;
    public MethodModel? Method;
    public PropertyModel? Property;
    public EventModel? Event;
    public bool IsSetter;
    public bool Static;
    /// <summary>The field's type, or the method's return type.</summary>
    public Mapping Type = new();
    public bool Leaf => Method?.Leaf == true || Property?.Getter.Leaf == true || Field?.Tag.Has("Leaf") == true;
}
