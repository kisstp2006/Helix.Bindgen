using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Helix.Bindgen.Model;

namespace Helix.Bindgen.Managed;

/// <summary>How a C# value crosses to C++ in a proxy.</summary>
public enum ManagedKind
{
    Void,
    Primitive,
    Bool,
    Char,
    String,
    Enum,
    // A C# class with a proxy: crosses as a GC handle.
    Proxy,
    // A C# wrapper of a native object (a type of the referenced headers): crosses as its id.
    NativeObject,
    // A struct of the referenced headers: by pointer.
    NativeStruct,
    // A native enum of the referenced headers.
    NativeEnum,
    Array,
    // A C# interface with a proxy: implemented by a C++ class, handed to C# for the call.
    Interface,
    // Action or Func of values: a C++ function with a user pointer.
    Delegate,
}

public sealed class ManagedType
{
    public ManagedKind Kind;
    /// <summary>The C# spelling (global:: qualified for named types).</summary>
    public string Cs = "";
    /// <summary>Primitives: the C++ type.</summary>
    public string Cpp = "";
    public bool ByRef;
    public ManagedType? Element;
    public TypeModel? Native;
    public ProxyType? Proxy;
    public ManagedEnum? Enum;
    /// <summary>Delegates: the arguments and the result.</summary>
    public List<ManagedType> Arguments = new();
    public ManagedType? Result;
}

public sealed class ManagedEnum
{
    public string Namespace = "";
    public string Name = "";
    public string FullName => Namespace.Length > 0 ? Namespace + "." + Name : Name;
    public string Underlying = "int";
    public string UnderlyingCpp = "::helix::i32";
    public List<(string Name, long Value)> Values = new();
}

public enum ProxyMemberKind
{
    Method,
    Constructor,
    Getter,
    Setter,
    EventAdd,
    EventRemove,
}

public sealed class ProxyParameter
{
    public string Name = "";
    /// <summary>The name in C++ (renamed where it would hide a member).</summary>
    public string CppName = "";
    public ManagedType Type = new();
    public bool Out;
}

public sealed class ProxyMember
{
    public ProxyMemberKind Kind;
    public string Name = "";
    public bool Static;
    /// <summary>Getters and setters of fields (not properties).</summary>
    public bool Field;
    public List<ProxyParameter> Parameters = new();
    public ManagedType Return = new() { Kind = ManagedKind.Void, Cs = "void", Cpp = "void" };
    public string Thunk = "";
    public int Index;
}

/// <summary>A property (or field) of a proxy: its getter and setter thunks.</summary>
public sealed class ProxyProperty
{
    public string Name = "";
    public bool Static;
    public ManagedType Type = new();
    public ProxyMember Getter = new();
    public ProxyMember? Setter;
}

/// <summary>An event of a proxy: C++ handlers (a function and a user pointer) added and removed by token.</summary>
public sealed class ProxyEvent
{
    public string Name = "";
    public bool Static;
    public ManagedType Delegate = new();
    public ProxyMember Add = new();
    public ProxyMember Remove = new();
}

public sealed class ProxyType
{
    public string Namespace = "";
    public string Name = "";
    public string FullName => Namespace.Length > 0 ? Namespace + "." + Name : Name;
    public bool Abstract;
    /// <summary>A C# interface: an abstract C++ class C++ implements; its methods are native functions C# calls.</summary>
    public bool IsInterface;
    public List<ProxyMember> Methods = new();
    public List<ProxyMember> Constructors = new();
    public List<ProxyProperty> Properties = new();
    public List<ProxyEvent> Events = new();
}

public sealed class ProxyModel
{
    public string Module = "";
    public List<ProxyType> Types = new();
    public List<ManagedEnum> Enums = new();
    public List<ProxyMember> Members = new();
    /// <summary>The C++ functions C# calls: the methods of interfaces C++ implements (the native table).</summary>
    public List<ProxyMember> NativeMembers = new();
    public List<string> Skipped = new();
    public ulong Hash;
}

/// <summary>
/// Reads a compiled C# assembly (System.Reflection.Metadata) and builds the proxy model of the public types asked
/// for: their public constructors, methods, properties and fields of the types C++ can hold (primitives, strings,
/// enums, arrays of primitives, the proxied classes, and the native types of the referenced headers). Members of other
/// types, generic ones and events are listed as skipped.
/// </summary>
public sealed class AssemblyReader
{
    readonly MetadataReader _md;
    readonly Dictionary<string, TypeDefinition> _defs = new();
    readonly Dictionary<string, TypeModel> _natives = new();
    readonly ProxyModel _model = new();
    readonly Dictionary<string, ProxyType> _proxies = new();
    readonly Dictionary<string, ManagedEnum> _enums = new();

    AssemblyReader(MetadataReader md, ModuleModel references)
    {
        _md = md;
        foreach (TypeDefinitionHandle h in md.TypeDefinitions)
        {
            TypeDefinition d = md.GetTypeDefinition(h);
            _defs[FullName(d)] = d;
        }
        foreach (TypeModel t in references.Types)
            _natives[t.CsFullName] = t;
    }

    public static ProxyModel Read(string path, string module, IEnumerable<string> types, ModuleModel references)
    {
        using FileStream fs = File.OpenRead(path);
        using var pe = new PEReader(fs);
        var reader = new AssemblyReader(pe.GetMetadataReader(), references);
        reader._model.Module = module;
        reader.Build(types.ToList());
        return reader._model;
    }

    string FullName(TypeDefinition d)
    {
        string name = _md.GetString(d.Name);
        if (d.IsNested)
            return FullName(_md.GetTypeDefinition(d.GetDeclaringType())) + "." + name;
        string ns = _md.GetString(d.Namespace);
        return ns.Length > 0 ? ns + "." + name : name;
    }

    void Build(List<string> names)
    {
        // Every asked type first, so members can name each other.
        foreach (string n in names)
        {
            if (!_defs.TryGetValue(n, out TypeDefinition d))
                throw new ArgumentException($"{n} is not a type of the assembly");
            if ((d.Attributes & TypeAttributes.VisibilityMask) != TypeAttributes.Public)
                throw new ArgumentException($"{n}: proxies are made for public classes and interfaces");
            var p = new ProxyType
            {
                Namespace = _md.GetString(d.Namespace),
                Name = _md.GetString(d.Name),
                Abstract = (d.Attributes & TypeAttributes.Abstract) != 0,
                IsInterface = (d.Attributes & TypeAttributes.Interface) != 0,
            };
            _proxies[n] = p;
            _model.Types.Add(p);
        }
        foreach (ProxyType p in _model.Types)
            Members(p, _defs[p.FullName]);
        var used = new Dictionary<string, int>();
        foreach (ProxyMember m in _model.Members)
        {
            used.TryGetValue(m.Thunk, out int k);
            used[m.Thunk] = k + 1;
            if (k > 0)
                m.Thunk += "_" + k;
        }
        for (int i = 0; i < _model.Members.Count; i++)
            _model.Members[i].Index = i + 2;
        for (int i = 0; i < _model.NativeMembers.Count; i++)
            _model.NativeMembers[i].Index = i;
        ulong h = 14695981039346656037ul;
        foreach (ProxyMember m in _model.Members.Concat(_model.NativeMembers))
            foreach (char c in m.Thunk + "|" + m.Kind + "|" + Shape(m.Return) + string.Concat(m.Parameters.Select(p => "," + Shape(p.Type))) + ";")
                h = (h ^ c) * 1099511628211ul;
        _model.Hash = h;
    }

    static string Shape(ManagedType t) => t.Kind + (t.ByRef ? "&" : "") + (t.Element != null ? "[" + Shape(t.Element) + "]" : "") + t.Cs;

    string Ident(ProxyType p) => p.FullName.Replace('.', '_');

    // The kinds C++ implementations of C# interfaces and C++ handlers take and give.
    static bool IsValue(ManagedType t) => t.Kind is ManagedKind.Primitive or ManagedKind.Bool or ManagedKind.Char or ManagedKind.Enum or ManagedKind.NativeEnum;

    void InterfaceMembers(ProxyType p, TypeDefinition d)
    {
        var provider = new SignatureProvider(this);
        foreach (MethodDefinitionHandle mh in d.GetMethods())
        {
            MethodDefinition m = _md.GetMethodDefinition(mh);
            string name = _md.GetString(m.Name);
            if ((m.Attributes & MethodAttributes.SpecialName) != 0 || (m.Attributes & MethodAttributes.Abstract) == 0)
            {
                if ((m.Attributes & MethodAttributes.SpecialName) != 0)
                    Skip(p, name, "an interface property or event (C++ implements interface methods)");
                continue;
            }
            MethodSignature<Sig> sig = m.DecodeSignature(provider, null);
            ManagedType? r = Classify(sig.ReturnType, out string? why);
            var member = new ProxyMember { Kind = ProxyMemberKind.Method, Name = name, Thunk = $"HxNative_{Ident(p)}_{name}" };
            bool ok = r != null && (r.Kind == ManagedKind.Void || IsValue(r) || r.Kind == ManagedKind.String);
            if (r != null)
                member.Return = r;
            var names = m.GetParameters().Select(h => _md.GetParameter(h)).Where(x => x.SequenceNumber > 0).OrderBy(x => x.SequenceNumber).ToList();
            for (int i = 0; ok && i < sig.ParameterTypes.Length; i++)
            {
                ManagedType? pt = Classify(sig.ParameterTypes[i], out why);
                ok = pt != null && !pt.ByRef && (IsValue(pt) || pt.Kind == ManagedKind.String);
                if (ok)
                    member.Parameters.Add(new ProxyParameter { Name = i < names.Count ? _md.GetString(names[i].Name) : $"arg{i}", Type = pt! });
            }
            if (!ok)
            {
                Skip(p, name, why ?? "an interface method C++ implements takes and gives values and strings");
                continue;
            }
            p.Methods.Add(member);
            _model.NativeMembers.Add(member);
        }
    }

    void Events(ProxyType p, TypeDefinition d, string prefix, HashSet<MethodDefinitionHandle> accessors)
    {
        foreach (EventDefinitionHandle eh in d.GetEvents())
        {
            EventDefinition ev = _md.GetEventDefinition(eh);
            EventAccessors acc = ev.GetAccessors();
            accessors.Add(acc.Adder);
            accessors.Add(acc.Remover);
            string name = _md.GetString(ev.Name);
            MethodDefinition adder = _md.GetMethodDefinition(acc.Adder);
            if (!IsPublic(adder))
                continue;
            ManagedType? type = Classify(DecodeType(ev.Type), out string? why);
            if (type == null || type.Kind != ManagedKind.Delegate)
            {
                Skip(p, name, why ?? "an event of a delegate type other than Action or Func of values");
                continue;
            }
            bool isStatic = (adder.Attributes & MethodAttributes.Static) != 0;
            var e = new ProxyEvent
            {
                Name = name,
                Static = isStatic,
                Delegate = type,
                Add = Add(new ProxyMember { Kind = ProxyMemberKind.EventAdd, Name = name, Static = isStatic, Thunk = prefix + "add_" + name,
                                            Parameters = { new ProxyParameter { Name = "handler", Type = type } },
                                            Return = new ManagedType { Kind = ManagedKind.Primitive, Cs = "ulong", Cpp = "::helix::u64" } }),
                Remove = Add(new ProxyMember { Kind = ProxyMemberKind.EventRemove, Name = name, Static = isStatic, Thunk = prefix + "remove_" + name,
                                               Parameters = { new ProxyParameter { Name = "token", Type = new ManagedType { Kind = ManagedKind.Primitive, Cs = "ulong", Cpp = "::helix::u64" } } } }),
            };
            p.Events.Add(e);
        }
    }

    Sig DecodeType(EntityHandle h)
    {
        var provider = new SignatureProvider(this);
        return h.Kind switch
        {
            HandleKind.TypeDefinition => provider.GetTypeFromDefinition(_md, (TypeDefinitionHandle)h, 0),
            HandleKind.TypeReference => provider.GetTypeFromReference(_md, (TypeReferenceHandle)h, 0),
            HandleKind.TypeSpecification => _md.GetTypeSpecification((TypeSpecificationHandle)h).DecodeSignature(provider, null),
            _ => new Sig { Unsupported = "a type Bindgen does not read" },
        };
    }

    void Members(ProxyType p, TypeDefinition d)
    {
        if (p.IsInterface)
        {
            InterfaceMembers(p, d);
            return;
        }
        string prefix = $"Hx_{Ident(p)}_";
        var provider = new SignatureProvider(this);
        var accessors = new HashSet<MethodDefinitionHandle>();
        Events(p, d, prefix, accessors);
        foreach (PropertyDefinitionHandle ph in d.GetProperties())
        {
            PropertyDefinition prop = _md.GetPropertyDefinition(ph);
            PropertyAccessors acc = prop.GetAccessors();
            if (!acc.Getter.IsNil)
                accessors.Add(acc.Getter);
            if (!acc.Setter.IsNil)
                accessors.Add(acc.Setter);
            string name = _md.GetString(prop.Name);
            if (acc.Getter.IsNil || !IsPublic(_md.GetMethodDefinition(acc.Getter)))
                continue;
            MethodDefinition getter = _md.GetMethodDefinition(acc.Getter);
            MethodSignature<Sig> sig = getter.DecodeSignature(provider, null);
            if (sig.ParameterTypes.Length != 0)
            {
                Skip(p, name, "an indexer");
                continue;
            }
            ManagedType? type = Classify(sig.ReturnType, out string? why);
            if (type == null || type.Kind is ManagedKind.Interface or ManagedKind.Delegate)
            {
                Skip(p, name, why ?? "an interface or delegate property (they go from C++ to C# as arguments)");
                continue;
            }
            bool isStatic = (getter.Attributes & MethodAttributes.Static) != 0;
            var property = new ProxyProperty
            {
                Name = name,
                Static = isStatic,
                Type = type,
                Getter = Add(new ProxyMember { Kind = ProxyMemberKind.Getter, Name = name, Static = isStatic, Return = type, Thunk = prefix + "get_" + name }),
            };
            if (!acc.Setter.IsNil && IsPublic(_md.GetMethodDefinition(acc.Setter)))
                property.Setter = Add(new ProxyMember
                {
                    Kind = ProxyMemberKind.Setter,
                    Name = name,
                    Static = isStatic,
                    Parameters = { new ProxyParameter { Name = "value", Type = type } },
                    Thunk = prefix + "set_" + name,
                });
            p.Properties.Add(property);
        }
        foreach (FieldDefinitionHandle fh in d.GetFields())
        {
            FieldDefinition f = _md.GetFieldDefinition(fh);
            FieldAttributes a = f.Attributes;
            if ((a & FieldAttributes.FieldAccessMask) != FieldAttributes.Public || (a & FieldAttributes.Literal) != 0)
                continue;
            string name = _md.GetString(f.Name);
            ManagedType? type = Classify(f.DecodeSignature(provider, null), out string? why);
            if (type == null || type.Kind is ManagedKind.Interface or ManagedKind.Delegate)
            {
                Skip(p, name, why ?? "an interface or delegate property (they go from C++ to C# as arguments)");
                continue;
            }
            bool isStatic = (a & FieldAttributes.Static) != 0;
            var property = new ProxyProperty
            {
                Name = name,
                Static = isStatic,
                Type = type,
                Getter = Add(new ProxyMember { Kind = ProxyMemberKind.Getter, Name = name, Static = isStatic, Field = true, Return = type, Thunk = prefix + "get_" + name }),
            };
            if ((a & FieldAttributes.InitOnly) == 0)
                property.Setter = Add(new ProxyMember
                {
                    Kind = ProxyMemberKind.Setter,
                    Name = name,
                    Static = isStatic,
                    Field = true,
                    Parameters = { new ProxyParameter { Name = "value", Type = type } },
                    Thunk = prefix + "set_" + name,
                });
            p.Properties.Add(property);
        }
        foreach (MethodDefinitionHandle mh in d.GetMethods())
        {
            if (accessors.Contains(mh))
                continue;
            MethodDefinition m = _md.GetMethodDefinition(mh);
            string name = _md.GetString(m.Name);
            if (!IsPublic(m))
                continue;
            bool ctor = name == ".ctor";
            if (name == ".cctor" || (!ctor && (m.Attributes & MethodAttributes.SpecialName) != 0))
                continue;
            if (ctor && p.Abstract)
                continue;
            if (m.GetGenericParameters().Count > 0)
            {
                Skip(p, name, "a generic method");
                continue;
            }
            MethodSignature<Sig> sig = m.DecodeSignature(provider, null);
            var member = new ProxyMember
            {
                Kind = ctor ? ProxyMemberKind.Constructor : ProxyMemberKind.Method,
                Name = ctor ? "New" : name,
                Static = ctor || (m.Attributes & MethodAttributes.Static) != 0,
                Thunk = prefix + (ctor ? "New" : name),
            };
            string? reason = null;
            if (!ctor)
            {
                ManagedType? r = Classify(sig.ReturnType, out reason);
                if (r == null)
                {
                    Skip(p, name, reason!);
                    continue;
                }
                member.Return = r;
            }
            else
                member.Return = new ManagedType { Kind = ManagedKind.Proxy, Proxy = p, Cs = "global::" + p.FullName };
            var names = m.GetParameters().Select(h => _md.GetParameter(h)).Where(x => x.SequenceNumber > 0).OrderBy(x => x.SequenceNumber).ToList();
            bool ok = true;
            if (member.Return.Kind is ManagedKind.Interface or ManagedKind.Delegate)
            {
                Skip(p, name, "an interface or delegate result (they go from C++ to C#)");
                continue;
            }
            for (int i = 0; i < sig.ParameterTypes.Length; i++)
            {
                ManagedType? pt = Classify(sig.ParameterTypes[i], out reason);
                if (pt == null || (pt.ByRef && pt.Kind is not (ManagedKind.Primitive or ManagedKind.Bool or ManagedKind.Char or ManagedKind.Enum or ManagedKind.NativeEnum or ManagedKind.NativeStruct)))
                {
                    reason ??= "a ref or out argument of a type that is not a value";
                    ok = false;
                    break;
                }
                Parameter? pd = i < names.Count ? names[i] : null;
                member.Parameters.Add(new ProxyParameter
                {
                    Name = pd != null ? _md.GetString(pd.Value.Name) : $"arg{i}",
                    Type = pt,
                    Out = pd != null && (pd.Value.Attributes & ParameterAttributes.Out) != 0,
                });
            }
            if (!ok)
            {
                Skip(p, name, reason!);
                continue;
            }
            Add(member);
            (ctor ? p.Constructors : p.Methods).Add(member);
        }
    }

    ProxyMember Add(ProxyMember m)
    {
        _model.Members.Add(m);
        return m;
    }

    void Skip(ProxyType p, string member, string why) => _model.Skipped.Add($"{p.FullName}.{member}: {why}");

    static bool IsPublic(MethodDefinition m) => (m.Attributes & MethodAttributes.MemberAccessMask) == MethodAttributes.Public;

    // ------------------------------------------------------------------------------------------- types

    static readonly Dictionary<PrimitiveTypeCode, (string Cs, string Cpp)> Primitives = new()
    {
        [PrimitiveTypeCode.SByte] = ("sbyte", "::helix::i8"),
        [PrimitiveTypeCode.Byte] = ("byte", "::helix::u8"),
        [PrimitiveTypeCode.Int16] = ("short", "::helix::i16"),
        [PrimitiveTypeCode.UInt16] = ("ushort", "::helix::u16"),
        [PrimitiveTypeCode.Int32] = ("int", "::helix::i32"),
        [PrimitiveTypeCode.UInt32] = ("uint", "::helix::u32"),
        [PrimitiveTypeCode.Int64] = ("long", "::helix::i64"),
        [PrimitiveTypeCode.UInt64] = ("ulong", "::helix::u64"),
        [PrimitiveTypeCode.Single] = ("float", "::helix::f32"),
        [PrimitiveTypeCode.Double] = ("double", "::helix::f64"),
    };

    ManagedType? Classify(Sig s, out string? why)
    {
        why = null;
        if (s.Unsupported != null)
        {
            why = s.Unsupported;
            return null;
        }
        if (s.ByRef)
        {
            ManagedType? inner = Classify(s.Element!, out why);
            if (inner != null)
                inner.ByRef = true;
            return inner;
        }
        if (s.Array)
        {
            ManagedType? e = Classify(s.Element!, out why);
            if (e == null)
                return null;
            if (e.Kind is not (ManagedKind.Primitive or ManagedKind.Bool or ManagedKind.Char))
            {
                why = $"an array of {e.Cs} (arrays of numbers cross)";
                return null;
            }
            return new ManagedType { Kind = ManagedKind.Array, Element = e, Cs = e.Cs + "[]" };
        }
        if (s.Primitive is PrimitiveTypeCode code)
        {
            switch (code)
            {
                case PrimitiveTypeCode.Void:
                    return new ManagedType { Kind = ManagedKind.Void, Cs = "void", Cpp = "void" };
                case PrimitiveTypeCode.Boolean:
                    return new ManagedType { Kind = ManagedKind.Bool, Cs = "bool", Cpp = "bool" };
                case PrimitiveTypeCode.Char:
                    return new ManagedType { Kind = ManagedKind.Char, Cs = "char", Cpp = "char16_t" };
                case PrimitiveTypeCode.String:
                    return new ManagedType { Kind = ManagedKind.String, Cs = "string", Cpp = "::helix::String" };
            }
            if (Primitives.TryGetValue(code, out var p))
                return new ManagedType { Kind = ManagedKind.Primitive, Cs = p.Cs, Cpp = p.Cpp };
            why = $"{code} has no C++ counterpart";
            return null;
        }
        string name = s.Name!;
        if (name == "System.Action" || s.Arguments.Count > 0)
            return DelegateOf(s, out why);
        if (_proxies.TryGetValue(name, out ProxyType? proxy))
            return new ManagedType { Kind = proxy.IsInterface ? ManagedKind.Interface : ManagedKind.Proxy, Proxy = proxy, Cs = "global::" + name };
        if (_natives.TryGetValue(name, out TypeModel? native))
        {
            ManagedKind kind = native.Kind switch
            {
                Model.TypeKind.Struct => ManagedKind.NativeStruct,
                Model.TypeKind.Enum => ManagedKind.NativeEnum,
                Model.TypeKind.Class => ManagedKind.NativeObject,
                _ => ManagedKind.Void,
            };
            if (kind == ManagedKind.Void)
            {
                why = $"{name} (a native interface)";
                return null;
            }
            return new ManagedType { Kind = kind, Native = native, Cs = "global::" + name, Cpp = "::" + native.CppName };
        }
        if (_defs.TryGetValue(name, out TypeDefinition d) && IsEnum(d))
            return new ManagedType { Kind = ManagedKind.Enum, Enum = EnumOf(name, d), Cs = "global::" + name };
        why = $"{name} has no proxy (ask for it) or native type";
        return null;
    }

    // System.Action, Action<...> and Func<...> of values.
    ManagedType? DelegateOf(Sig s, out string? why)
    {
        why = null;
        bool func = s.Name!.StartsWith("System.Func`", StringComparison.Ordinal);
        var t = new ManagedType { Kind = ManagedKind.Delegate };
        var types = new List<ManagedType>();
        foreach (Sig a in s.Arguments)
        {
            ManagedType? m = Classify(a, out why);
            if (m == null || !IsValue(m))
            {
                why ??= $"a delegate of {m?.Cs} (delegates of values cross)";
                return null;
            }
            types.Add(m);
        }
        if (func)
        {
            t.Result = types[^1];
            types.RemoveAt(types.Count - 1);
        }
        else
            t.Result = new ManagedType { Kind = ManagedKind.Void, Cs = "void", Cpp = "void" };
        t.Arguments = types;
        string all = string.Join(", ", s.Arguments.Select((_, i) => i < types.Count ? types[i].Cs : t.Result.Cs));
        t.Cs = s.Arguments.Count == 0 ? "global::System.Action" : $"global::System.{(func ? "Func" : "Action")}<{all}>";
        return t;
    }

    bool IsEnum(TypeDefinition d)
    {
        if (d.BaseType.IsNil || d.BaseType.Kind != HandleKind.TypeReference)
            return false;
        TypeReference r = _md.GetTypeReference((TypeReferenceHandle)d.BaseType);
        return _md.GetString(r.Namespace) == "System" && _md.GetString(r.Name) == "Enum";
    }

    ManagedEnum EnumOf(string name, TypeDefinition d)
    {
        if (_enums.TryGetValue(name, out ManagedEnum? known))
            return known;
        var e = new ManagedEnum { Namespace = _md.GetString(d.Namespace), Name = _md.GetString(d.Name) };
        var provider = new SignatureProvider(this);
        foreach (FieldDefinitionHandle fh in d.GetFields())
        {
            FieldDefinition f = _md.GetFieldDefinition(fh);
            if ((f.Attributes & FieldAttributes.Static) == 0)
            {
                // value__: the underlying type.
                Sig u = f.DecodeSignature(provider, null);
                if (u.Primitive is PrimitiveTypeCode code && Primitives.TryGetValue(code, out var p))
                    (e.Underlying, e.UnderlyingCpp) = p;
                continue;
            }
            if ((f.Attributes & FieldAttributes.Literal) == 0)
                continue;
            Constant c = _md.GetConstant(f.GetDefaultValue());
            BlobReader b = _md.GetBlobReader(c.Value);
            long v = c.TypeCode switch
            {
                ConstantTypeCode.SByte => b.ReadSByte(),
                ConstantTypeCode.Byte => b.ReadByte(),
                ConstantTypeCode.Int16 => b.ReadInt16(),
                ConstantTypeCode.UInt16 => b.ReadUInt16(),
                ConstantTypeCode.Int32 => b.ReadInt32(),
                ConstantTypeCode.UInt32 => b.ReadUInt32(),
                ConstantTypeCode.Int64 => b.ReadInt64(),
                ConstantTypeCode.UInt64 => (long)b.ReadUInt64(),
                _ => 0,
            };
            e.Values.Add((_md.GetString(f.Name), v));
        }
        _enums[name] = e;
        _model.Enums.Add(e);
        return e;
    }

    // ------------------------------------------------------------------------------------------- signatures

    public sealed class Sig
    {
        public PrimitiveTypeCode? Primitive;
        public string? Name;
        public Sig? Element;
        public bool Array;
        public bool ByRef;
        public string? Unsupported;
        /// <summary>Action and Func instantiations: their type arguments.</summary>
        public List<Sig> Arguments = new();
    }

    sealed class SignatureProvider(AssemblyReader r) : ISignatureTypeProvider<Sig, object?>
    {
        static Sig No(string why) => new() { Unsupported = why };

        public Sig GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode switch
        {
            PrimitiveTypeCode.Object => No("object"),
            PrimitiveTypeCode.IntPtr or PrimitiveTypeCode.UIntPtr => No("a native sized integer"),
            PrimitiveTypeCode.TypedReference => No("a typed reference"),
            _ => new Sig { Primitive = typeCode },
        };

        public Sig GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) =>
            new() { Name = r.FullName(reader.GetTypeDefinition(handle)) };

        public Sig GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
        {
            TypeReference t = reader.GetTypeReference(handle);
            string name = reader.GetString(t.Name);
            if (t.ResolutionScope.Kind == HandleKind.TypeReference)
            {
                Sig outer = GetTypeFromReference(reader, (TypeReferenceHandle)t.ResolutionScope, rawTypeKind);
                return new Sig { Name = outer.Name + "." + name };
            }
            string ns = reader.GetString(t.Namespace);
            return new Sig { Name = ns.Length > 0 ? ns + "." + name : name };
        }

        public Sig GetSZArrayType(Sig elementType) => elementType.Unsupported != null ? elementType : new Sig { Array = true, Element = elementType };
        public Sig GetByReferenceType(Sig elementType) => elementType.Unsupported != null ? elementType : new Sig { ByRef = true, Element = elementType };
        public Sig GetArrayType(Sig elementType, ArrayShape shape) => No("a multi dimensional array");
        public Sig GetPointerType(Sig elementType) => No("a pointer");
        public Sig GetPinnedType(Sig elementType) => elementType;
        public Sig GetModifiedType(Sig modifier, Sig unmodifiedType, bool isRequired) => unmodifiedType;
        public Sig GetFunctionPointerType(MethodSignature<Sig> signature) => No("a function pointer");
        public Sig GetGenericInstantiation(Sig genericType, ImmutableArray<Sig> typeArguments)
        {
            string name = genericType.Name ?? "";
            if (!name.StartsWith("System.Action`", StringComparison.Ordinal) && !name.StartsWith("System.Func`", StringComparison.Ordinal))
                return No("a generic type");
            Sig? bad = typeArguments.FirstOrDefault(a => a.Unsupported != null);
            return bad ?? new Sig { Name = name, Arguments = typeArguments.ToList() };
        }
        public Sig GetGenericMethodParameter(object? genericContext, int index) => No("a generic parameter");
        public Sig GetGenericTypeParameter(object? genericContext, int index) => No("a generic parameter");
        public Sig GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind) =>
            No("a type specification");
    }
}
