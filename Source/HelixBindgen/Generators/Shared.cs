using System.Text;
using Helix.Bindgen.Model;

namespace Helix.Bindgen.Generators;

/// <summary>What the generators share about the C ABI of thunks.</summary>
public static class Shared
{
    /// <summary>
    /// The most parameters a thunk takes as they are; one with more takes them as one array of 8 byte slots (each
    /// value in its first bytes). Mono's WebAssembly interpreter calls native functions with at most 12 integer
    /// slots, a 64 bit value taking two: six parameters fit whatever their types.
    /// </summary>
    public const int MaxThunkParameters = 6;
}

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
        {
            Roots[t] = FindRoot(t);
            foreach (MethodModel m in t.Methods)
                OwnerOf[m] = t;
        }
        Validate();
        CollectBridge();
        Collect();
    }

    public readonly Dictionary<MethodModel, TypeModel> OwnerOf = new();

    // ----------------------------------------------------------------------------------------------- the bridge

    /// <summary>The functions the C# side hands the native module: the handle release, then the C# virtual
    /// functions (one per first declaration) and the C# implementations of the module's interfaces.</summary>
    public readonly List<ManagedEntry> Managed = new();
    public readonly Dictionary<TypeModel, List<Overridable>> Overridables = new();
    readonly Dictionary<MethodModel, ManagedEntry> _virtualExports = new();

    public static string Key(MethodModel m) => m.Name + "(" + string.Join(",", m.Parameters.Select(p => p.Type.Spelling())) + ")" + (m.Const ? "const" : "");

    /// <summary>A class C# may derive from: objects cross as ids, not sealed, with a tagged C++ constructor, or a script
    /// class with tagged virtual functions (its engine makes the objects of C# script classes deriving from it).</summary>
    public bool IsDerivable(TypeModel t) =>
        !t.External && t.Kind == TypeKind.Class && IsObjectClass(t) && !t.Tag.Has("Sealed") &&
        (t.Methods.Any(m => m.RealConstructor && !m.Tag.Has("Hidden")) || (IsScriptClass(t) && ChainVirtuals(t).Count > 0));

    /// <summary>The class declares a tagged pure virtual function itself (it is abstract in C++).</summary>
    public bool DeclaresPure(TypeModel t) => t.Methods.Any(m => m.Virtual && m.Abstract && !m.Tag.Has("Hidden"));

    /// <summary>Tagged pure virtual functions without a nearer C++ implementation (the class is abstract in C++).</summary>
    public bool IsAbstractCpp(TypeModel t) => t.Tag.Has("Abstract") || ChainVirtuals(t).Any(o => o.Pure);

    // The tagged virtual functions of a class and its bases: the nearest declaration of each signature (whose C++
    // implementation a base call reaches) and the first one (where C# declares the method virtual).
    public List<Overridable> ChainVirtuals(TypeModel t)
    {
        var list = new List<Overridable>();
        var byKey = new Dictionary<string, Overridable>();
        foreach (TypeModel c in BaseChain(t))
            foreach ((MethodModel m, PropertyModel? p) in c.Methods.Where(m => m.Virtual && !m.Static && !m.Tag.Has("Hidden")).Select(m => (m, (PropertyModel?)null))
                         .Concat(c.Properties.Where(IsVirtualProperty).Select(p => (p.Getter, (PropertyModel?)p))))
            {
                string key = Key(m);
                if (byKey.TryGetValue(key, out Overridable? o))
                {
                    o.First = m;
                    o.Declarer = c;
                    continue;
                }
                o = new Overridable { Nearest = m, NearestOwner = c, First = m, Declarer = c, Pure = m.Abstract, Property = p };
                byKey[key] = o;
                list.Add(o);
            }
        for (int i = 0; i < list.Count; i++)
            list[i].Bit = i;
        return list;
    }

    /// <summary>A property C# classes override: a virtual getter tagged HX_PROPERTY without a setter (one with a setter is
    /// a plain C# property, its getter virtual for C++ only).</summary>
    public static bool IsVirtualProperty(PropertyModel p) => p.Getter.Virtual && !p.Static && p.Setter == null && !p.Getter.Tag.Has("Hidden");

    /// <summary>Where C# declares a virtual function first: the overridable of the method's own class.</summary>
    public bool IsFirstDeclaration(TypeModel owner, MethodModel m) => ChainVirtuals(owner).Any(o => o.First == m);

    /// <summary>A value handle of the C++ script API: a bound object class by value, spelled with its C# namespace
    /// (HelixEngine::GameObject). Fields of script classes hold them; they are serialized, not bound to C#.</summary>
    public TypeModel? ValueHandle(TypeRef type)
    {
        if (type.Pointers != 0 || type.Reference || type.Arguments.Count > 0)
            return null;
        string name = type.Name.StartsWith("::", StringComparison.Ordinal) ? type.Name[2..] : type.Name;
        TypeModel? t = Map.Find(name);
        if (t == null || t.Kind != TypeKind.Class || !IsObjectClass(t))
            return null;
        return name == t.CsFullName.Replace(".", "::") ? t : null;
    }

    /// <summary>A value handle or an Array of them.</summary>
    public bool HoldsValueHandle(TypeRef type) =>
        ValueHandle(type) != null || (type.Name.Split("::")[^1] == "Array" && type.Arguments.Count == 1 && ValueHandle(type.Arguments[0]) != null);

    /// <summary>A wrapper of a reference to an object (a class template of one object class with Get() and a constructor
    /// from the object's pointer, an engine's PPtr<T>): the object class (null for other types).</summary>
    public TypeModel? ReferenceWrapper(TypeRef type)
    {
        if (type.Arguments.Count != 1 || type.Pointers != 0 || type.Reference || type.Name.Split("::")[^1] == "Array")
            return null;
        TypeRef a = type.Arguments[0];
        if (a.Arguments.Count != 0 || a.Pointers != 0)
            return null;
        try
        {
            Mapping m = Map.Map(new TypeRef { Name = a.Name, Pointers = 1 }, new Location(), a.Name);
            return m.Kind == MapKind.Object ? m.Bound : null;
        }
        catch (BindgenException)
        {
            return null;
        }
    }

    /// <summary>A field type files hold that C# does not reach through the bindings: value handles, reference wrappers,
    /// arrays of them and of strings.</summary>
    public bool SerializedOnly(TypeRef type) =>
        HoldsValueHandle(type) || ReferenceWrapper(type) != null ||
        (type.Name.Split("::")[^1] == "Array" && type.Arguments.Count == 1 && ReferenceWrapper(type.Arguments[0]) != null) ||
        (type.Name.Split("::")[^1] == "Array" && type.Arguments.Count == 1 && type.Arguments[0].Name.Split("::")[^1] == "String" && type.Arguments[0].Pointers == 0);

    /// <summary>A field files hold: not static, const or ReadOnly, not NonSerialized; a private one (C++ private, or
    /// private in C#) with SerializeField.</summary>
    public static bool IsSerialized(FieldModel f) =>
        !f.Static && !f.Type.Const && !f.Tag.Has("ReadOnly") && !f.Tag.Has("NonSerialized") && (!f.Tag.Has("Private") || f.Tag.Has("SerializeField"));

    /// <summary>A class of the module deriving from a script base class (HX_CLASS(ScriptBase)), made by its engine.</summary>
    public bool IsScriptClass(TypeModel t) => !t.External && IsScriptClassOfAnyModule(t);

    /// <summary>A script class of this module or of another one (a base the engine registered as a class).</summary>
    public bool IsScriptClassOfAnyModule(TypeModel t) =>
        t.Kind == TypeKind.Class && IsObjectClass(t) && !t.Tag.Has("ScriptBase") && !t.Tag.Has("Abstract") &&
        BaseChain(t).Skip(1).Any(b => b.Tag.Has("ScriptBase"));

    /// <summary>The methods of a script class an engine may call: no arguments or one bool, returning nothing, of the
    /// class and its bases below the script base (the nearest of a name and signature).</summary>
    public List<(MethodModel Method, TypeModel Owner)> ScriptMethods(TypeModel t)
    {
        var list = new List<(MethodModel, TypeModel)>();
        var seen = new HashSet<string>();
        foreach (TypeModel c in BaseChain(t))
        {
            // Another module's script class: the engine gives its messages to the classes deriving from it.
            if (c.Tag.Has("ScriptBase") || c.External)
                break;
            foreach (MethodModel m in c.Methods.Concat(c.Untagged))
            {
                if (m.Static || m.RealConstructor || m.Return.Reference)
                    continue;
                // A pointer result: a factory without arguments (the engine checks the pointee's name).
                if (m.Return.Name != "void" || m.Return.Pointers != 0)
                {
                    if (m.Return.Pointers == 1 && m.Parameters.Count == 0 && seen.Add(m.Name + "/0"))
                        list.Add((m, c));
                    continue;
                }
                bool boolArg = m.Parameters.Count == 1 && m.Parameters[0].Type.Name == "bool" && m.Parameters[0].Type.Pointers == 0 && !m.Parameters[0].Type.Reference;
                if (m.Parameters.Count != 0 && !boolArg)
                    continue;
                if (seen.Add(m.Name + "/" + m.Parameters.Count))
                    list.Add((m, c));
            }
        }
        return list;
    }

    public IEnumerable<TypeModel> ImplementersOf(TypeModel i) =>
        Module.Types.Where(t => !t.External && t.Kind == TypeKind.Class && IsObjectClass(t) && InterfacesOf(t).Contains(i));

    void CollectBridge()
    {
        Managed.Add(new ManagedEntry { Kind = ExportKind.Release, Name = "HxReleaseHandle" });
        foreach (TypeModel t in Module.Types.Where(IsDerivable))
        {
            List<Overridable> list = ChainVirtuals(t);
            if (list.Count > 64)
                throw new BindgenException(t.Where, $"{t.Name}: more than 64 virtual functions C# could override");
            Overridables[t] = list;
            foreach (Overridable o in list)
            {
                if (!_virtualExports.TryGetValue(o.First, out ManagedEntry? e))
                {
                    e = new ManagedEntry { Kind = ExportKind.Virtual, Method = o.First, Owner = o.Declarer, Name = $"HxVirtual_{o.Declarer.Ident}_{o.First.Name}",
                                           Property = o.Property };
                    _virtualExports[o.First] = e;
                    Managed.Add(e);
                }
                o.Export = e;
            }
        }
        foreach (TypeModel i in Module.Types.Where(t => !t.External && t.Kind == TypeKind.Interface))
            foreach (MethodModel m in i.Methods.Where(m => !m.Static && !m.Tag.Has("Hidden")))
                Managed.Add(new ManagedEntry { Kind = ExportKind.Interface, Method = m, Owner = i, Name = $"HxInterface_{i.Ident}_{m.Name}" });
        var used = new Dictionary<string, int>();
        for (int k = 0; k < Managed.Count; k++)
        {
            ManagedEntry e = Managed[k];
            e.Index = k;
            used.TryGetValue(e.Name, out int n);
            used[e.Name] = n + 1;
            if (n > 0)
                e.Name += "_" + n;
        }
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
            foreach (FieldModel f in t.Fields.Where(f => !f.Tag.Has("Private") && !f.Tag.Has("Hidden") && !SerializedOnly(f.Type)))
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
            {
                // A C++ class that is abstract is made only as the subclass of a C# class.
                if (mm.RealConstructor && IsAbstractCpp(t))
                    continue;
                Thunks.Add(new Thunk { Kind = ThunkKind.Method, Owner = t, Method = mm, Type = Map.Map(mm.Return, mm.Where, $"{t.Name}.{mm.Name}"),
                                       Name = Name(mm.RealConstructor ? "New" : mm.Name), Static = mm.Static || mm.Tag.Has("Constructor") });
            }
            if (Roots[t] == t)
            {
                Thunks.Add(new Thunk { Kind = ThunkKind.GetWrapper, Owner = t, Name = Name("HxGetWrapper") });
                Thunks.Add(new Thunk { Kind = ThunkKind.SetWrapper, Owner = t, Name = Name("HxSetWrapper") });
                Thunks.Add(new Thunk { Kind = ThunkKind.Alive, Owner = t, Name = Name("HxAlive") });
            }
            if (Overridables.TryGetValue(t, out List<Overridable>? virtuals))
            {
                foreach (MethodModel c in t.Methods.Where(m => m.RealConstructor && !m.Tag.Has("Hidden")))
                    Thunks.Add(new Thunk { Kind = ThunkKind.NewDerived, Owner = t, Method = c, Type = Map.Map(c.Return, c.Where, t.Name), Name = Name("NewDerived"), Static = true });
                foreach (Overridable o in virtuals)
                    Thunks.Add(new Thunk { Kind = ThunkKind.BaseCall, Owner = t, Method = o.Nearest, Virtual = o,
                                           Type = Map.Map(o.Nearest.Return, o.Nearest.Where, o.Nearest.Name), Name = Name("base_" + o.Nearest.Name) });
            }
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
        foreach (ManagedEntry e in Managed)
            foreach (char c in e.Name + "|" + e.Kind + "|" + (e.Method != null ? Key(e.Method) : "") + ";")
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
    // The root class's wrapper slot and whether an id names a live object.
    GetWrapper,
    SetWrapper,
    Alive,
    // An object of a C# class that derives from the class, and the C++ implementation of a virtual function for
    // the C# class's base calls.
    NewDerived,
    BaseCall,
}

public enum ExportKind
{
    Release,
    Virtual,
    Interface,
}

/// <summary>A function the C# side exports to the native module.</summary>
public sealed class ManagedEntry
{
    public ExportKind Kind;
    public string Name = "";
    public int Index;
    public MethodModel? Method;
    public TypeModel? Owner;
    /// <summary>A virtual property's getter: C# reads the property.</summary>
    public PropertyModel? Property;
}

/// <summary>A virtual function a C# class deriving from a class may override.</summary>
public sealed class Overridable
{
    public MethodModel Nearest = new();
    public TypeModel NearestOwner = new();
    public MethodModel First = new();
    public TypeModel Declarer = new();
    public bool Pure;
    public int Bit;
    /// <summary>A virtual getter tagged HX_PROPERTY: C# overrides the property.</summary>
    public PropertyModel? Property;
    public ManagedEntry Export = new();
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
    public Overridable? Virtual;
    /// <summary>The field's type, or the method's return type.</summary>
    public Mapping Type = new();
    public bool Leaf => Method?.Leaf == true || Property?.Getter.Leaf == true || Field?.Tag.Has("Leaf") == true;
}
