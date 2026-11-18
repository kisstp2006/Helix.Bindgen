namespace Helix.Bindgen.Model;

/// <summary>How a value crosses the boundary.</summary>
public enum MapKind
{
    Void,
    Primitive,
    Bool,
    String,
    Struct,
    Enum,
    Object,
    Array,
    TypeId,
    // A C++ interface (HX_INTERFACE): a native object that implements it, or a C# object through an adapter.
    Interface,
    // A class bound by hand (HX_HANDLE): a field of it crosses as its Id(), C# answers the C# object of that id
    // (Helix.Interop.Handles).
    Handle,
}

/// <summary>What a C++ type is on both sides: its kind, its C# type, and for arrays the element's mapping.</summary>
public sealed class Mapping
{
    public MapKind Kind;
    public TypeRef Cpp = new();
    /// <summary>The C++ type without qualifiers, as generated code names it.</summary>
    public string CppBare = "";
    public string Cs = "";
    public int Size;
    public int Align;
    public Mapping? Element;
    public TypeModel? Bound;
    /// <summary>A non-const reference or pointer to a value the call writes (out / ref in C#).</summary>
    public bool ByRef;

    public bool IsBlittable => Kind is MapKind.Primitive or MapKind.Bool or MapKind.Enum or MapKind.Struct;
}

/// <summary>
/// The table of known C++ types (primitives, strings, arrays, events, type ids) and the module's tagged types; every
/// member type goes through Map, which reports a type it does not know with the member's place.
/// </summary>
public sealed class TypeMap
{
    sealed record Primitive(string Cs, int Size);

    // C++ spellings of the primitives (Helix's aliases and the standard ones) and their C# types.
    static readonly Dictionary<string, Primitive> Primitives = new()
    {
        ["i8"] = new("sbyte", 1), ["int8_t"] = new("sbyte", 1), ["std::int8_t"] = new("sbyte", 1), ["signed char"] = new("sbyte", 1),
        ["u8"] = new("byte", 1), ["uint8_t"] = new("byte", 1), ["std::uint8_t"] = new("byte", 1),
        ["i16"] = new("short", 2), ["int16_t"] = new("short", 2), ["std::int16_t"] = new("short", 2), ["short"] = new("short", 2),
        ["u16"] = new("ushort", 2), ["uint16_t"] = new("ushort", 2), ["std::uint16_t"] = new("ushort", 2), ["char16_t"] = new("char", 2),
        ["i32"] = new("int", 4), ["int32_t"] = new("int", 4), ["std::int32_t"] = new("int", 4), ["int"] = new("int", 4),
        ["u32"] = new("uint", 4), ["uint32_t"] = new("uint", 4), ["std::uint32_t"] = new("uint", 4),
        ["i64"] = new("long", 8), ["int64_t"] = new("long", 8), ["std::int64_t"] = new("long", 8),
        ["u64"] = new("ulong", 8), ["uint64_t"] = new("ulong", 8), ["std::uint64_t"] = new("ulong", 8),
        ["f32"] = new("float", 4), ["float"] = new("float", 4),
        ["f64"] = new("double", 8), ["double"] = new("double", 8),
    };

    static readonly HashSet<string> Strings = new() { "String", "StringView" };
    static readonly HashSet<string> Arrays = new() { "Array", "Span" };
    static readonly HashSet<string> TypeIds = new() { "TypeId", "scripting::TypeId" };

    readonly Dictionary<string, TypeModel> _byName = new();
    readonly Dictionary<string, string> _handles;

    public TypeMap(ModuleModel module)
    {
        _handles = module.Handles;
        QualifyNested(module);
        foreach (TypeModel t in module.Types)
        {
            _byName[t.Name] = t;
            _byName[t.CppName] = t;
            // Qualified as a member names it without the helix:: prefix (two classes of one name in nested namespaces).
            _byName[StripHelix(t.CppName)] = t;
            if (t.Outer != null)
                _byName[t.Outer.Name + "::" + t.Name] = t;
            // The C++ script API names a class by its C# namespace (HelixEngine::GameBehaviour).
            _byName.TryAdd(t.CsFullName.Replace(".", "::"), t);
        }
    }

    // A member's unqualified type that names a type nested in its class, an outer class or one of their bases is that
    // nested type (two classes may each nest a Direction): the name is qualified by its class, as C++ looks it up.
    static void QualifyNested(ModuleModel module)
    {
        var byName = new Dictionary<string, TypeModel>();
        var nested = new Dictionary<string, List<TypeModel>>();
        foreach (TypeModel t in module.Types)
        {
            if (t.Outer == null)
                byName.TryAdd(t.Name, t);
            byName.TryAdd(t.CppName, t);
            if (t.Outer != null)
            {
                if (!nested.TryGetValue(t.Name, out var list))
                    nested[t.Name] = list = new List<TypeModel>();
                list.Add(t);
            }
        }
        if (nested.Count == 0)
            return;
        foreach (TypeModel t in module.Types)
        {
            if (t.External)
                continue;
            var scopes = new List<TypeModel>();
            void AddScope(TypeModel? s)
            {
                if (s == null || scopes.Contains(s))
                    return;
                scopes.Add(s);
                foreach (string b in s.Bases)
                    AddScope(byName.TryGetValue(StripHelix(b), out TypeModel? bt) ? bt : byName.GetValueOrDefault(StripHelix(b).Split("::")[^1]));
                AddScope(s.Outer);
            }
            AddScope(t);
            void Qualify(TypeRef r)
            {
                foreach (TypeRef a in r.Arguments)
                    Qualify(a);
                if (r.Name.Contains("::") || !nested.TryGetValue(r.Name, out var candidates))
                    return;
                foreach (TypeModel s in scopes)
                    foreach (TypeModel c in candidates)
                        if (c.Outer == s)
                        {
                            r.Name = s.Name + "::" + r.Name;
                            return;
                        }
            }
            foreach (FieldModel f in t.Fields)
                Qualify(f.Type);
            foreach (MethodModel m in t.Methods.Concat(t.Untagged).Concat(t.Properties.SelectMany(p => p.Setter != null ? new[] { p.Getter, p.Setter } : new[] { p.Getter })))
            {
                Qualify(m.Return);
                foreach (ParameterModel p in m.Parameters)
                    Qualify(p.Type);
            }
            foreach (EventModel e in t.Events)
                foreach (TypeRef a in e.Arguments)
                    Qualify(a);
        }
    }

    public TypeModel? Find(string cppName)
    {
        string name = StripHelix(cppName);
        if (_byName.TryGetValue(name, out TypeModel? t))
            return t;
        string last = name.Split("::")[^1];
        return _byName.TryGetValue(last, out t) ? t : null;
    }

    // helix:: and helix::<lib>:: prefixes name the same types as the bare names.
    static string StripHelix(string name)
    {
        if (name.StartsWith("helix::", StringComparison.Ordinal))
            name = name["helix::".Length..];
        return name;
    }

    public Mapping Map(TypeRef type, Location where, string what)
    {
        var m = new Mapping { Cpp = type };
        string name = StripHelix(type.Name);
        string last = name.Split("::")[^1];
        // A non-const reference (or a pointer to a value) is written by the call.
        bool writes = (type.Reference && !type.Const) && !Arrays.Contains(last) && !Strings.Contains(last);
        m.ByRef = writes;
        if (name == "void" && type.Pointers == 0)
        {
            m.Kind = MapKind.Void;
            m.Cs = "void";
            m.CppBare = "void";
            return m;
        }
        if (name == "bool")
        {
            m.Kind = MapKind.Bool;
            m.Cs = "bool";
            m.CppBare = "bool";
            m.Size = m.Align = 1;
            return m;
        }
        if (Primitives.TryGetValue(name, out Primitive? p) && type.Pointers == 0)
        {
            m.Kind = MapKind.Primitive;
            m.Cs = p.Cs;
            m.CppBare = type.Name;
            m.Size = m.Align = p.Size;
            return m;
        }
        if (Strings.Contains(last))
        {
            m.Kind = MapKind.String;
            m.Cs = "string";
            m.CppBare = "helix::String";
            return m;
        }
        if (TypeIds.Contains(name) || last == "TypeId")
        {
            m.Kind = MapKind.TypeId;
            m.Cs = "System.Type";
            m.CppBare = "helix::scripting::TypeId";
            return m;
        }
        if (Arrays.Contains(last))
        {
            if (type.Arguments.Count != 1)
                throw new BindgenException(where, $"{what}: {type.Spelling()} needs one item type");
            Mapping e = Map(type.Arguments[0], where, what);
            if (!(e.IsBlittable || e.Kind == MapKind.Object || e.Kind == MapKind.String))
                throw new BindgenException(where, $"{what}: arrays of {e.Cs} are not bound (plain values, strings and objects are)");
            m.Kind = MapKind.Array;
            m.Element = e;
            m.Cs = e.Cs + "[]";
            m.CppBare = type.Bare().Spelling();
            return m;
        }
        if ((_handles.TryGetValue(name, out string? handle) || _handles.TryGetValue(last, out handle)) && type.Arguments.Count == 0)
        {
            if (type.Pointers != 0 || type.Reference)
                throw new BindgenException(where, $"{what}: a {type.Spelling()} (HX_HANDLE) is bound as a field by value");
            m.Kind = MapKind.Handle;
            m.Cs = "global::" + handle;
            m.CppBare = type.Name;
            m.Size = m.Align = 8;
            return m;
        }
        TypeModel? bound = Find(type.Name);
        if (bound == null)
            throw new BindgenException(where, $"{what}: Bindgen does not know {type.Spelling()} (tag it, or use a type of the table)");
        m.Bound = bound;
        m.CppBare = bound.CppName;
        m.Cs = bound.CsFullName;
        switch (bound.Kind)
        {
            case TypeKind.Enum:
                m.Kind = MapKind.Enum;
                (m.Size, m.Align) = EnumSize(bound);
                if (type.Pointers > 0)
                    throw new BindgenException(where, $"{what}: a pointer to an enum");
                return m;
            case TypeKind.Struct:
                m.Kind = MapKind.Struct;
                (m.Size, m.Align) = Layout(bound);
                if (type.Pointers > 1)
                    throw new BindgenException(where, $"{what}: a pointer to a pointer");
                m.ByRef |= type.Pointers == 1 && !type.Const;
                return m;
            case TypeKind.Interface:
                if (!((type.Pointers == 1 && !type.Reference) || (type.Pointers == 0 && type.Reference)))
                    throw new BindgenException(where, $"{what}: an interface {bound.Name} crosses as a pointer or a reference");
                m.Kind = MapKind.Interface;
                m.ByRef = false;
                return m;
            default:
                if (type.Pointers == 0 && !type.Reference)
                    throw new BindgenException(where, $"{what}: objects of {bound.Name} cross by pointer or reference, not by value");
                m.Kind = MapKind.Object;
                // T*& is an object the call writes.
                m.ByRef = type.Reference && type.Pointers == 1 && !type.Const;
                return m;
        }
    }

    public (int Size, int Align) EnumSize(TypeModel e)
    {
        if (e.Underlying == null)
            return (4, 4);
        return Primitives.TryGetValue(StripHelix(e.Underlying.Name), out Primitive? p) ? (p.Size, p.Size) : (4, 4);
    }

    public string EnumCs(TypeModel e) => e.Underlying != null && Primitives.TryGetValue(StripHelix(e.Underlying.Name), out Primitive? p) ? p.Cs : "int";

    readonly Dictionary<TypeModel, (int, int)> _layouts = new();

    /// <summary>A struct's size and alignment with natural alignment of its fields in order (what both compilers do).</summary>
    public (int Size, int Align) Layout(TypeModel s)
    {
        if (_layouts.TryGetValue(s, out var known))
            return known;
        int offset = 0, align = 1;
        foreach (FieldModel f in s.Fields.Where(f => !f.Static))
        {
            Mapping m = Map(f.Type, f.Where, $"{s.Name}.{f.Name}");
            if (!m.IsBlittable)
                throw new BindgenException(f.Where, $"{s.Name}.{f.Name}: a struct holds plain values only ({m.Cs} is not)");
            offset = (offset + m.Align - 1) / m.Align * m.Align;
            offset += m.Size;
            align = Math.Max(align, m.Align);
        }
        int size = Math.Max(1, (offset + align - 1) / align * align);
        _layouts[s] = (size, align);
        return (size, align);
    }

    public int FieldOffset(TypeModel s, FieldModel field)
    {
        int offset = 0;
        foreach (FieldModel f in s.Fields.Where(f => !f.Static))
        {
            Mapping m = Map(f.Type, f.Where, f.Name);
            offset = (offset + m.Align - 1) / m.Align * m.Align;
            if (f == field)
                return offset;
            offset += m.Size;
        }
        return -1;
    }
}
