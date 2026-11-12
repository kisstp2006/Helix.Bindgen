using Helix.Bindgen.Generators;
using Helix.Bindgen.Model;

namespace Helix.Bindgen.Managed;

/// <summary>
/// C++ calling C#: typed C++ proxy classes of C# classes (values holding a GC handle, without pointers; properties
/// and fields as property members read and written as in C#), their definitions calling through the function table
/// that C# hands over, and the C# exports (UnmanagedCallersOnly) that table holds.
/// </summary>
public sealed class ProxyGenerator
{
    readonly ProxyModel _m;
    readonly TypeMap _map;

    ProxyGenerator(ProxyModel model, ModuleModel references)
    {
        _m = model;
        _map = new TypeMap(references);
    }

    public static (string Header, string Source, string Exports) Generate(ProxyModel model, ModuleModel references, IEnumerable<string> includes, string headerInclude)
    {
        var g = new ProxyGenerator(model, references);
        g.RenameParameters();
        return (g.Header(includes.ToList()), g.Source(headerInclude), g.Exports());
    }

    // C++ parameters named like a property member of their class (or a local of the definitions) get a '_'.
    void RenameParameters()
    {
        var locals = new HashSet<string> { "self", "status", "r", "items", "proxy", "copy", "other", "handle" };
        foreach (ProxyType p in _m.Types)
        {
            var taken = new HashSet<string>(p.Properties.Select(x => x.Name));
            taken.UnionWith(locals);
            foreach (ProxyMember m in p.Methods.Concat(p.Constructors).Concat(p.Properties.Select(x => x.Setter).OfType<ProxyMember>()))
                foreach (ProxyParameter pa in m.Parameters)
                {
                    pa.CppName = pa.Name;
                    while (taken.Contains(pa.CppName) || p.Methods.Any(x => x.Name == pa.CppName))
                        pa.CppName += "_";
                }
        }
    }

    string ModuleIdent => new string(_m.Module.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
    static string CppNs(string ns) => ns.Replace(".", "::");
    static string Q(ProxyType p) => "::" + (p.Namespace.Length > 0 ? CppNs(p.Namespace) + "::" : "") + p.Name;
    static string Q(ManagedEnum e) => "::" + (e.Namespace.Length > 0 ? CppNs(e.Namespace) + "::" : "") + e.Name;

    string Root(TypeModel t)
    {
        var seen = new HashSet<TypeModel>();
        TypeModel? at = t, root = null;
        while (at != null && seen.Add(at))
        {
            if (at.Tag.Has("Root"))
                root = at;
            at = at.Bases.Select(b => _map.Find(b)).FirstOrDefault(b => b != null && b.Kind == Model.TypeKind.Class);
        }
        return root != null ? "::" + root.CppName : throw new BindgenException(t.Where, $"{t.Name} has no Root class among its bases");
    }

    // ------------------------------------------------------------------------------------------- C++ types

    string CppIn(ManagedType t) => (t.ByRef ? CppValue(t) + "&" : t.Kind switch
    {
        ManagedKind.String => "::helix::StringView",
        ManagedKind.NativeStruct => $"const {t.Cpp}&",
        ManagedKind.Proxy => $"const {Q(t.Proxy!)}&",
        ManagedKind.Array => $"::helix::Span<const {CppValue(t.Element!)}>",
        _ => CppValue(t),
    });

    string CppValue(ManagedType t) => t.Kind switch
    {
        ManagedKind.Enum => Q(t.Enum!),
        ManagedKind.Proxy => Q(t.Proxy!),
        ManagedKind.NativeObject => t.Cpp + "*",
        ManagedKind.Array => $"::helix::Array<{CppValue(t.Element!)}>",
        _ => t.Cpp,
    };

    string CppOut(ManagedType t) => t.Kind switch
    {
        ManagedKind.Bool => "::helix::u8",
        ManagedKind.String => "::helix::scripting::InteropString16",
        ManagedKind.Proxy => "::helix::u64",
        ManagedKind.NativeObject => "::helix::scripting::InteropObject",
        ManagedKind.Array => "::helix::scripting::InteropArray",
        _ => CppValue(t),
    };

    // A C++ argument as C ABI values: their types and expressions (and a line to prepare them).
    (List<string> Abi, List<string> Args, string? Prep) CppArg(ManagedType t, string n)
    {
        if (t.ByRef)
            return (new() { CppValue(t) + "*" }, new() { "&" + n }, null);
        return t.Kind switch
        {
            ManagedKind.Bool => (new() { "::helix::u8" }, new() { $"::helix::u8({n} ? 1 : 0)" }, null),
            ManagedKind.String => (new() { "const char*", "::helix::i32" }, new() { $"{n}.Data()", $"::helix::i32({n}.Size())" }, null),
            ManagedKind.NativeStruct => (new() { $"const {t.Cpp}*" }, new() { "&" + n }, null),
            ManagedKind.Proxy => (new() { "::helix::u64" }, new() { $"{n}.Handle()" }, null),
            ManagedKind.NativeObject => (new() { "::helix::u64", "::helix::u32" }, new() { $"{n}_o.id", $"{n}_o.type" },
                                         $"::helix::scripting::InteropObject {n}_o = ::helix::scripting::PassObject<{Root(t.Native!)}>({n});"),
            ManagedKind.Array => (new() { $"const {CppValue(t.Element!)}*", "::helix::i32" }, new() { $"{n}.Data()", $"::helix::i32({n}.Size())" }, null),
            _ => (new() { CppValue(t) }, new() { n }, null),
        };
    }

    string CppResult(ManagedType t, string r) => t.Kind switch
    {
        ManagedKind.Bool => $"{r} != 0",
        ManagedKind.String => $"::helix::scripting::StringFromUtf16({r}.data, {r}.size)",
        ManagedKind.Proxy => $"{Q(t.Proxy!)}::Adopt({r})",
        ManagedKind.NativeObject => $"::helix::scripting::ObjectFromId<{Root(t.Native!)}, {t.Cpp}>({r}.id)",
        _ => r,
    };

    // ------------------------------------------------------------------------------------------- the header

    string Header(List<string> includes)
    {
        var c = new Code();
        c.Line("// Generated by Helix.Bindgen. Do not edit.");
        c.Line("#pragma once");
        c.Line();
        c.Line("#include <Helix/Core/Containers/Array.h>");
        c.Line("#include <Helix/Core/Strings/String.h>");
        c.Line("#include <Helix/Scripting/Bindings/Interop.h>");
        foreach (string inc in includes)
            c.Line($"#include {inc}");
        c.Line();
        c.Line("#include <cstddef>");
        c.Line();
        foreach (IGrouping<string, ManagedEnum> g in _m.Enums.GroupBy(e => e.Namespace))
        {
            Namespace(c, g.Key, () =>
            {
                foreach (ManagedEnum e in g)
                {
                    c.Open($"enum class {e.Name} : {e.UnderlyingCpp}");
                    foreach ((string name, long value) in e.Values)
                        c.Line($"{name} = {value},");
                    c.Close("};");
                }
            });
        }
        foreach (IGrouping<string, ProxyType> g in _m.Types.GroupBy(t => t.Namespace))
            Namespace(c, g.Key, () =>
            {
                foreach (ProxyType p in g)
                    c.Line($"class {p.Name};");
            });
        c.Line();
        foreach (ProxyType p in _m.Types)
            Namespace(c, p.Namespace, () => ProxyClass(c, p));
        return c.ToString();
    }

    static void Namespace(Code c, string ns, Action body)
    {
        if (ns.Length > 0)
            c.Open($"namespace {CppNs(ns)}");
        body();
        if (ns.Length > 0)
            c.Close();
    }

    string Params(ProxyMember m) => string.Join(", ", m.Parameters.Select(p => $"{CppIn(p.Type)} {p.CppName}"));

    string Ret(ProxyMember m) => m.Return.Kind == ManagedKind.Void ? "void" : CppValue(m.Return);

    void ProxyClass(Code c, ProxyType p)
    {
        c.Line($"// The C# class {p.FullName}: a value holding a GC handle of the object (empty for null). Copies hold");
        c.Line("// handles of their own; the object lives while one does.");
        c.Open($"class {p.Name}");
        c.Line("public:", -1);
        c.Line($"{p.Name}() = default;");
        c.Line($"{p.Name}(std::nullptr_t) {{}}");
        c.Line($"{p.Name}(const {p.Name}& other);");
        c.Line($"{p.Name}({p.Name}&& other) noexcept : handle_(other.handle_) {{ other.handle_ = 0; }}");
        c.Line($"{p.Name}& operator=(const {p.Name}& other);");
        c.Line($"{p.Name}& operator=({p.Name}&& other) noexcept;");
        c.Line($"~{p.Name}();");
        c.Line();
        c.Line("explicit operator bool() const { return handle_ != 0; }");
        c.Line("bool operator==(std::nullptr_t) const { return handle_ == 0; }");
        c.Line("// Takes over a GC handle the bindings made.");
        c.Line($"static {p.Name} Adopt(::helix::u64 handle);");
        c.Line("::helix::u64 Handle() const { return handle_; }");
        c.Line();
        foreach (ProxyMember m in p.Constructors)
            c.Line($"static {p.Name} New({Params(m)});");
        foreach (ProxyMember m in p.Methods)
            c.Line($"{(m.Static ? "static " : "")}{Ret(m)} {m.Name}({Params(m)}){(m.Static ? "" : " const")};");
        foreach (ProxyProperty prop in p.Properties)
        {
            string t = CppValue(prop.Type);
            string s = $"HxProperty_{prop.Name}";
            c.Line();
            c.Open($"struct {s}");
            c.Line($"{s}() = default;");
            c.Line($"{s}(const {s}&) = default;");
            c.Line($"operator {t}() const {{ return Get(); }}");
            c.Line($"{t} Get() const;");
            if (prop.Setter != null)
            {
                c.Line($"{s}& operator=({CppIn(prop.Type)} {prop.Setter.Parameters[0].CppName});");
                c.Line($"{s}& operator=(const {s}& other) {{ return *this = other.Get(); }}");
            }
            else
                c.Line($"{s}& operator=(const {s}&) = delete;");
            c.Close("};");
            c.Line(prop.Static ? $"static inline {s} {prop.Name}{{}};" : $"HX_NO_UNIQUE_ADDRESS {s} {prop.Name};");
        }
        c.Line();
        c.Line("private:", -1);
        c.Line("::helix::u64 handle_ = 0;");
        c.Close("};");
    }

    // ------------------------------------------------------------------------------------------- the source

    string Source(string headerInclude)
    {
        var c = new Code();
        c.Line("// Generated by Helix.Bindgen. Do not edit.");
        c.Line($"#include {headerInclude}");
        c.Line();
        c.Line("#include <Helix/Scripting/Types/TypeBuilder.h>");
        c.Line();
        c.Line("#include <bit>");
        c.Line();
        c.Open("namespace");
        c.Line("void* const* gTable = nullptr;");
        c.Line();
        c.Line("template <class F>");
        c.Line("F Fn(::helix::u32 index) { return std::bit_cast<F>(gTable[index]); }");
        c.Line();
        c.Line("// What a failed call answers.");
        c.Line("template <class T>");
        c.Line("T Default() { return T(); }");
        c.Line();
        c.Open("bool Ready(const char* what)");
        c.Line("if (gTable)");
        c.Line("    return true;");
        c.Line("::helix::scripting::ReportBindingFailure(what, ::helix::scripting::InteropNotLoaded);");
        c.Line("return false;");
        c.Close();
        c.Close();
        c.Line();
        c.Line("// The exports of the C# side (from its generated Register), checked against this build's shape.");
        c.Open($"HX_BINDINGS_EXPORT bool HxProxies_{ModuleIdent}_SetTable(void* const* table, ::helix::u32 count, ::helix::u64 hash)");
        c.Line($"if (count != {_m.Members.Count + 2}u || hash != 0x{_m.Hash:x16}ull)");
        c.Line("    return false;");
        c.Line("gTable = table;");
        c.Line($"::helix::scripting::RegisterManagedFunctions(\"{_m.Module}\", table, count);");
        c.Line("return true;");
        c.Close();
        foreach (ProxyType p in _m.Types)
            ProxySource(c, p);
        return c.ToString();
    }

    void ProxySource(Code c, ProxyType p)
    {
        string q = Q(p);
        string n = p.Name;
        c.Line();
        c.Line($"{q}::{n}(const {n}& other) : handle_(other.handle_ && gTable ? Fn<::helix::u64 (*)(::helix::u64)>(0)(other.handle_) : 0) {{}}");
        c.Line();
        c.Open($"auto {q}::operator=(const {n}& other) -> {q}&");
        c.Line("if (this == &other)");
        c.Line("    return *this;");
        c.Line("::helix::u64 copy = other.handle_ && gTable ? Fn<::helix::u64 (*)(::helix::u64)>(0)(other.handle_) : 0;");
        c.Line("if (handle_ && gTable)");
        c.Line("    Fn<void (*)(::helix::u64)>(1)(handle_);");
        c.Line("handle_ = copy;");
        c.Line("return *this;");
        c.Close();
        c.Line();
        c.Open($"auto {q}::operator=({n}&& other) noexcept -> {q}&");
        c.Line("if (this == &other)");
        c.Line("    return *this;");
        c.Line("if (handle_ && gTable)");
        c.Line("    Fn<void (*)(::helix::u64)>(1)(handle_);");
        c.Line("handle_ = other.handle_;");
        c.Line("other.handle_ = 0;");
        c.Line("return *this;");
        c.Close();
        c.Line();
        c.Open($"{q}::~{n}()");
        c.Line("if (handle_ && gTable)");
        c.Line("    Fn<void (*)(::helix::u64)>(1)(handle_);");
        c.Close();
        c.Line();
        c.Open($"auto {q}::Adopt(::helix::u64 handle) -> {q}");
        c.Line($"{n} proxy;");
        c.Line("proxy.handle_ = handle;");
        c.Line("return proxy;");
        c.Close();
        foreach (ProxyMember m in p.Constructors)
            Member(c, $"auto {q}::New({Params(m)}) -> {q}", p, m, null);
        foreach (ProxyMember m in p.Methods)
            Member(c, $"auto {q}::{m.Name}({Params(m)}){(m.Static ? "" : " const")} -> {Ret(m)}", p, m, m.Static ? null : "handle_");
        foreach (ProxyProperty prop in p.Properties)
        {
            string s = $"{q}::HxProperty_{prop.Name}";
            string owner = prop.Static ? null! :
                $"reinterpret_cast<const {q}*>(reinterpret_cast<const char*>(this) - ::helix::scripting::OffsetOf(&{q}::{prop.Name}))->handle_";
            Member(c, $"auto {s}::Get() const -> {CppValue(prop.Type)}", p, prop.Getter, owner);
            if (prop.Setter != null)
                Member(c, $"auto {s}::operator=({CppIn(prop.Type)} {prop.Setter.Parameters[0].CppName}) -> {s}&", p, prop.Setter, owner, "*this");
        }
    }

    // A member's definition: the table ready, the object there, the arguments as C ABI values, the call, the status,
    // the result. `self` is the expression of the handle (null for static members).
    void Member(Code c, string signature, ProxyType p, ProxyMember m, string? self, string? returns = null)
    {
        string what = $"{p.FullName}.{m.Name}";
        bool hasResult = m.Return.Kind != ManagedKind.Void;
        string fail = returns != null ? $"return {returns};" : hasResult ? $"return Default<{CppValue(m.Return)}>();" : "return;";
        c.Line();
        c.Open(signature);
        c.Line($"if (!Ready(\"{what}\"))");
        c.Line($"    {fail}");
        var abi = new List<string>();
        var args = new List<string>();
        if (self != null)
        {
            c.Line($"::helix::u64 self = {self};");
            c.Open("if (!self)");
            c.Line($"::helix::scripting::ReportBindingFailure(\"{what}\", ::helix::scripting::InteropMissingObject);");
            c.Line(fail);
            c.Close();
            abi.Add("::helix::u64");
            args.Add("self");
        }
        foreach (ProxyParameter pa in m.Parameters)
        {
            (List<string> a, List<string> x, string? prep) = CppArg(pa.Type, pa.CppName);
            if (prep != null)
                c.Line(prep);
            abi.AddRange(a);
            args.AddRange(x);
        }
        if (hasResult)
        {
            c.Line($"{CppOut(m.Return)} r{{}};");
            abi.Add(CppOut(m.Return) + "*");
            args.Add("&r");
        }
        c.Line($"::helix::i32 status = Fn<::helix::i32 (*)({string.Join(", ", abi)})>({m.Index})({string.Join(", ", args)});");
        c.Open("if (status != 0)");
        c.Line($"::helix::scripting::ReportBindingFailure(\"{what}\", status);");
        c.Line(fail);
        c.Close();
        if (returns != null)
            c.Line($"return {returns};");
        else if (hasResult && m.Return.Kind == ManagedKind.Array)
        {
            string e = CppValue(m.Return.Element!);
            c.Line($"::helix::Array<{e}> items;");
            c.Line("items.Reserve(::helix::usize(r.count));");
            c.Line("for (::helix::i32 i = 0; i < r.count; i++)");
            c.Line($"    items.Push(static_cast<const {e}*>(r.data)[i]);");
            c.Line("return items;");
        }
        else if (hasResult)
            c.Line($"return {CppResult(m.Return, "r")};");
        c.Close();
    }

    // ------------------------------------------------------------------------------------------- the C# exports

    static string CsAbi(ManagedType t) => t.ByRef ? CsValue(t) + "*" : t.Kind switch
    {
        ManagedKind.Bool => "byte",
        ManagedKind.Proxy => "ulong",
        ManagedKind.NativeStruct => t.Cs + "*",
        _ => t.Cs,
    };

    static string CsValue(ManagedType t) => t.Kind == ManagedKind.Bool && !t.ByRef ? "byte" : t.Cs;

    static string CsOut(ManagedType t) => t.Kind switch
    {
        ManagedKind.Bool => "byte*",
        ManagedKind.String => "global::Helix.Interop.InteropString16*",
        ManagedKind.Proxy => "ulong*",
        ManagedKind.NativeObject => "global::Helix.Interop.InteropObject*",
        ManagedKind.Array => "global::Helix.Interop.InteropArray*",
        _ => t.Cs + "*",
    };

    (List<string> Params, List<string> Abi, string Value) CsArg(ProxyParameter pa)
    {
        ManagedType t = pa.Type;
        string n = "a_" + pa.Name;
        if (t.ByRef)
            return (new() { $"{t.Cs}* {n}" }, new() { t.Cs + "*" }, $"{(pa.Out ? "out" : "ref")} *{n}");
        return t.Kind switch
        {
            ManagedKind.Bool => (new() { $"byte {n}" }, new() { "byte" }, $"{n} != 0"),
            ManagedKind.String => (new() { $"byte* {n}", $"int {n}_n" }, new() { "byte*", "int" }, $"global::Helix.Interop.Native.ReadString({n}, {n}_n)"),
            ManagedKind.Proxy => (new() { $"ulong {n}" }, new() { "ulong" },
                                  $"{n} == 0 ? null : ({t.Cs})global::System.Runtime.InteropServices.GCHandle.FromIntPtr((global::System.IntPtr){n}).Target"),
            ManagedKind.NativeObject => (new() { $"ulong {n}", $"uint {n}_t" }, new() { "ulong", "uint" }, $"global::Helix.Interop.Objects.Wrap<{t.Cs}>({n}, {n}_t)"),
            ManagedKind.NativeStruct => (new() { $"{t.Cs}* {n}" }, new() { t.Cs + "*" }, $"*{n}"),
            ManagedKind.Array => (new() { $"{t.Element!.Cs}* {n}", $"int {n}_n" }, new() { t.Element!.Cs + "*", "int" },
                                  $"global::Helix.Interop.Native.ReadArray({n}, {n}_n)"),
            _ => (new() { $"{t.Cs} {n}" }, new() { t.Cs }, n),
        };
    }

    static string CsStore(ManagedType t, string value) => t.Kind switch
    {
        ManagedKind.Bool => $"*result = {value} ? (byte)1 : (byte)0;",
        ManagedKind.String => $"global::Helix.Interop.Native.PassString16({value}, result);",
        ManagedKind.Proxy => $"*result = global::Helix.Interop.Objects.NewHandle({value});",
        ManagedKind.NativeObject => $"*result = global::Helix.Interop.Objects.Pass({value});",
        ManagedKind.Array => $"global::Helix.Interop.Native.PassArray({value}, result);",
        _ => $"*result = {value};",
    };

    readonly Dictionary<ProxyMember, string> _fnTypes = new();

    string Exports()
    {
        var c = new Code();
        c.Line("// Generated by Helix.Bindgen. Do not edit.");
        c.Line("#nullable disable");
        c.Line();
        c.Open("namespace Helix.Interop.Generated");
        c.Line($"/// <summary>The C# side of the C++ proxies of {_m.Module}: the exports their function table holds.</summary>");
        c.Open($"public static unsafe class HxProxies_{ModuleIdent}");
        c.Line($"public const ulong Hash = 0x{_m.Hash:x16}ul;");
        const string uco = "[global::System.Runtime.InteropServices.UnmanagedCallersOnly(CallConvs = new[] { typeof(global::System.Runtime.CompilerServices.CallConvCdecl) })]";
        const string gch = "global::System.Runtime.InteropServices.GCHandle";
        c.Line();
        c.Line(uco);
        c.Line($"static ulong HxRetain(ulong handle) => handle == 0 ? 0 : (ulong){gch}.ToIntPtr({gch}.Alloc({gch}.FromIntPtr((global::System.IntPtr)handle).Target));");
        c.Line();
        c.Line(uco);
        c.Open("static void HxRelease(ulong handle)");
        c.Line("if (handle != 0)");
        c.Line($"    {gch}.FromIntPtr((global::System.IntPtr)handle).Free();");
        c.Close();
        foreach (ProxyType p in _m.Types)
        {
            string type = "global::" + p.FullName;
            foreach (ProxyMember m in _m.Members.Where(x => Owner(x) == p))
            {
                var ps = new List<string>();
                var abi = new List<string>();
                var values = new List<string>();
                if (!m.Static)
                {
                    ps.Add("ulong self");
                    abi.Add("ulong");
                }
                foreach (ProxyParameter pa in m.Parameters)
                {
                    (List<string> pp, List<string> a, string v) = CsArg(pa);
                    ps.AddRange(pp);
                    abi.AddRange(a);
                    values.Add(v);
                }
                bool hasResult = m.Return.Kind != ManagedKind.Void;
                if (hasResult)
                {
                    ps.Add($"{CsOut(m.Return)} result");
                    abi.Add(CsOut(m.Return));
                }
                abi.Add("int");
                _fnTypes[m] = $"delegate* unmanaged[Cdecl]<{string.Join(", ", abi)}>";
                c.Line();
                c.Line(uco);
                c.Open($"static int {m.Thunk}({string.Join(", ", ps)})");
                c.Open("try");
                string target = type;
                if (!m.Static)
                {
                    c.Line($"var hx_o = ({type}){gch}.FromIntPtr((global::System.IntPtr)self).Target;");
                    c.Line("if (hx_o == null)");
                    c.Line("    return 1;");
                    target = "hx_o";
                }
                string call = m.Kind switch
                {
                    ProxyMemberKind.Constructor => $"new {type}({string.Join(", ", values)})",
                    ProxyMemberKind.Getter => $"{target}.{m.Name}",
                    ProxyMemberKind.Setter => $"{target}.{m.Name} = {values[0]}",
                    _ => $"{target}.{m.Name}({string.Join(", ", values)})",
                };
                if (hasResult)
                    c.Line(CsStore(m.Return, call));
                else
                    c.Line(call + ";");
                c.Line("return 0;");
                c.Close();
                c.Open("catch (global::System.Exception hx_e)");
                c.Line("global::Helix.Interop.Native.ReportException(hx_e);");
                c.Line("return 3;");
                c.Close();
                c.Close();
            }
        }
        c.Line();
        c.Line("/// <summary>Hands the exports to the native module (its HxProxies_..._SetTable); `export` finds that function.</summary>");
        c.Open("public static void Register(global::System.Func<string, global::System.IntPtr> export)");
        c.Line($"var set = (delegate* unmanaged[Cdecl]<void**, uint, ulong, byte>)export(\"HxProxies_{ModuleIdent}_SetTable\");");
        c.Line("if (set == null)");
        c.Line($"    throw new global::System.InvalidOperationException(\"{_m.Module}: the native module has no proxies\");");
        int count = _m.Members.Count + 2;
        c.Line($"void** table = (void**)global::System.Runtime.InteropServices.NativeMemory.Alloc((nuint)({count} * sizeof(void*)));");
        c.Line("table[0] = (void*)(delegate* unmanaged[Cdecl]<ulong, ulong>)&HxRetain;");
        c.Line("table[1] = (void*)(delegate* unmanaged[Cdecl]<ulong, void>)&HxRelease;");
        foreach (ProxyMember m in _m.Members)
            c.Line($"table[{m.Index}] = (void*)({_fnTypes[m]})&{m.Thunk};");
        c.Line($"if (set(table, {count}u, Hash) == 0)");
        c.Line($"    throw new global::System.InvalidOperationException(\"{_m.Module}: the native proxies were made from another build\");");
        c.Close();
        c.Close();
        c.Close();
        return c.ToString();
    }

    ProxyType Owner(ProxyMember m) => _m.Types.First(p => p.Methods.Contains(m) || p.Constructors.Contains(m) ||
                                                          p.Properties.Any(x => x.Getter == m || x.Setter == m));
}
