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
            foreach (ProxyMember m in p.Methods.Concat(p.Constructors).Concat(p.Properties.Select(x => x.Setter).OfType<ProxyMember>())
                                              .Concat(p.Events.SelectMany(e => new[] { e.Add, e.Remove })))
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
        ManagedKind.Interface => $"{Q(t.Proxy!)}&",
        ManagedKind.Array => $"::helix::Span<const {CppValue(t.Element!)}>",
        _ => CppValue(t),
    });

    // Action / Func: a C++ function and a user pointer.
    string CallbackType(ManagedType t) =>
        $"::helix::scripting::Callback<{(t.Result!.Kind == ManagedKind.Void ? "void" : CppValue(t.Result))}({string.Join(", ", t.Arguments.Select(CppValue))})>";

    string CppValue(ManagedType t) => t.Kind switch
    {
        ManagedKind.Enum => Q(t.Enum!),
        ManagedKind.Proxy => Q(t.Proxy!),
        ManagedKind.NativeObject => t.Cpp + "*",
        ManagedKind.Array => $"::helix::Array<{CppValue(t.Element!)}>",
        ManagedKind.Delegate => CallbackType(t),
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
            ManagedKind.Interface => (new() { "void*" }, new() { $"static_cast<void*>(&{n})" }, null),
            ManagedKind.Delegate => (new() { "void*", "void*" }, new() { $"std::bit_cast<void*>({n}.function)", $"{n}.user" }, null),
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

    void InterfaceClass(Code c, ProxyType p)
    {
        c.Line($"// The C# interface {p.FullName}: a C++ class implements it and is handed to C# for a call.");
        c.Open($"class {p.Name}");
        c.Line("public:", -1);
        c.Line($"virtual ~{p.Name}() = default;");
        foreach (ProxyMember m in p.Methods)
            c.Line($"virtual {Ret(m)} {m.Name}({Params(m)}) = 0;");
        c.Close("};");
    }

    void ProxyClass(Code c, ProxyType p)
    {
        if (p.IsInterface)
        {
            InterfaceClass(c, p);
            return;
        }
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
        foreach (ProxyEvent e in p.Events)
        {
            string s = $"HxEvent_{e.Name}";
            c.Line();
            c.Line($"// The C# event {e.Name}: Add answers a token Remove takes.");
            c.Open($"struct {s}");
            c.Line($"::helix::u64 Add({CallbackType(e.Delegate)} handler) const;");
            c.Line("void Remove(::helix::u64 token) const;");
            c.Close("};");
            c.Line(e.Static ? $"static inline {s} {e.Name}{{}};" : $"HX_NO_UNIQUE_ADDRESS {s} {e.Name};");
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
        c.Line("[[maybe_unused]] F Fn(::helix::u32 index) { return std::bit_cast<F>(gTable[index]); }");
        c.Line();
        c.Line("// What a failed call answers.");
        c.Line("template <class T>");
        c.Line("[[maybe_unused]] T Default() { return T(); }");
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
        foreach (ProxyType p in _m.Types.Where(t => !t.IsInterface))
            ProxySource(c, p);
        NativeTable(c);
        return c.ToString();
    }

    // The C++ functions C# calls on C++ objects of C# interfaces.
    void NativeTable(Code c)
    {
        c.Line();
        c.Open("namespace");
        foreach (ProxyMember m in _m.NativeMembers)
        {
            ProxyType p = _m.Types.First(t => t.Methods.Contains(m));
            var ps = new List<string> { "void* self" };
            var args = new List<string>();
            var prep = new List<string>();
            foreach (ProxyParameter pa in m.Parameters)
            {
                string n = "p_" + pa.Name;
                switch (pa.Type.Kind)
                {
                    case ManagedKind.Bool:
                        ps.Add($"::helix::u8 {n}");
                        args.Add($"{n} != 0");
                        break;
                    case ManagedKind.String:
                        ps.Add($"const char16_t* {n}");
                        ps.Add($"::helix::i32 {n}_n");
                        prep.Add($"::helix::String {n}_s = ::helix::scripting::StringFromUtf16({n}, {n}_n);");
                        args.Add($"{n}_s");
                        break;
                    default:
                        ps.Add($"{CppValue(pa.Type)} {n}");
                        args.Add(n);
                        break;
                }
            }
            string call = $"static_cast<{Q(p)}*>(self)->{m.Name}({string.Join(", ", args)})";
            string? store = m.Return.Kind switch
            {
                ManagedKind.Void => null,
                ManagedKind.Bool => $"*result = ({call}) ? 1 : 0;",
                ManagedKind.String => $"*result = ::helix::scripting::PassString({call});",
                _ => $"*result = {call};",
            };
            if (store != null)
                ps.Add(m.Return.Kind switch
                {
                    ManagedKind.Bool => "::helix::u8* result",
                    ManagedKind.String => "::helix::scripting::InteropString* result",
                    _ => $"{CppValue(m.Return)}* result",
                });
            c.Open($"::helix::i32 {m.Thunk}({string.Join(", ", ps)})");
            c.Line("if (!self)");
            c.Line("    return ::helix::scripting::InteropMissingObject;");
            foreach (string l in prep)
                c.Line(l);
            c.Line(store ?? call + ";");
            c.Line("return ::helix::scripting::InteropOk;");
            c.Close();
        }
        c.Open("const void* const kNative[] =");
        foreach (ProxyMember m in _m.NativeMembers)
            c.Line($"std::bit_cast<const void*>(&{m.Thunk}),");
        c.Line("nullptr,");
        c.Close("};");
        c.Close();
        c.Line();
        c.Line("// The functions C# calls on C++ implementations of its interfaces.");
        c.Open($"HX_BINDINGS_EXPORT const void* const* HxProxies_{ModuleIdent}_Native(::helix::u32* count)");
        c.Line($"*count = {_m.NativeMembers.Count}u;");
        c.Line("return kNative;");
        c.Close();
        c.Line();
        c.Line("// The exports by name (RegisterNativeExport), for hosts that find them without the system's symbols.");
        c.Open($"HX_BINDINGS_EXPORT void HxProxies_{ModuleIdent}_Publish()");
        foreach (string e in new[] { "SetTable", "Native" })
            c.Line($"::helix::scripting::RegisterNativeExport(\"HxProxies_{ModuleIdent}_{e}\", std::bit_cast<const void*>(&HxProxies_{ModuleIdent}_{e}));");
        c.Close();
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
        foreach (ProxyEvent e in p.Events)
        {
            string s = $"{q}::HxEvent_{e.Name}";
            string owner = e.Static ? null! :
                $"reinterpret_cast<const {q}*>(reinterpret_cast<const char*>(this) - ::helix::scripting::OffsetOf(&{q}::{e.Name}))->handle_";
            Member(c, $"auto {s}::Add({CallbackType(e.Delegate)} handler) const -> ::helix::u64", p, e.Add, owner);
            Member(c, $"auto {s}::Remove(::helix::u64 token) const -> void", p, e.Remove, owner);
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
            ManagedKind.Interface => (new() { $"void* {n}" }, new() { "void*" }, $"hx_i_{pa.Name}"),
            ManagedKind.Delegate => (new() { $"void* {n}", $"void* {n}_u" }, new() { "void*", "void*" }, $"hx_d_{pa.Name}"),
            _ => (new() { $"{t.Cs} {n}" }, new() { t.Cs }, n),
        };
    }

    static string CsCallbackAbi(ManagedType t) => t.Kind == ManagedKind.Bool ? "byte" : t.Cs;

    // The lines before a call that make the C# value of an interface or delegate argument (and after it, end it).
    void CsPrepare(Code c, ProxyParameter pa, List<string> after)
    {
        string n = "a_" + pa.Name;
        ManagedType t = pa.Type;
        if (t.Kind == ManagedKind.Interface)
        {
            // Borrowed for the call: the C# object stops forwarding when the call returns.
            c.Line($"var hx_i_{pa.Name} = {n} == null ? null : new HxNative_{Ident(t.Proxy!)}({n});");
            after.Add($"hx_i_{pa.Name}?.Invalidate();");
        }
        else if (t.Kind == ManagedKind.Delegate)
        {
            var abi = t.Arguments.Select(CsCallbackAbi).ToList();
            string result = t.Result!.Kind == ManagedKind.Void ? "void" : CsCallbackAbi(t.Result);
            string fn = $"delegate* unmanaged[Cdecl]<global::System.IntPtr{string.Join("", abi.Select(x => ", " + x))}, {result}>";
            var names = t.Arguments.Select((_, i) => $"x{i}").ToList();
            var values = t.Arguments.Select((a, i) => a.Kind == ManagedKind.Bool ? $"(byte)(x{i} ? 1 : 0)" : $"x{i}").ToList();
            string invoke = $"(({fn})hx_f_{pa.Name})(hx_u_{pa.Name}{string.Join("", values.Select(v => ", " + v))})";
            if (t.Result.Kind == ManagedKind.Bool)
                invoke += " != 0";
            c.Line($"global::System.IntPtr hx_f_{pa.Name} = (global::System.IntPtr){n}, hx_u_{pa.Name} = (global::System.IntPtr){n}_u;");
            c.Line($"{t.Cs} hx_d_{pa.Name} = {n} == null ? null : new {t.Cs}(({string.Join(", ", names)}) => {invoke});");
        }
    }

    string Ident(ProxyType p) => p.FullName.Replace('.', '_');

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
                ProxyEvent? ev = p.Events.FirstOrDefault(x => x.Add == m || x.Remove == m);
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
                var after = new List<string>();
                foreach (ProxyParameter pa in m.Parameters)
                    CsPrepare(c, pa, after);
                if (after.Count > 0)
                    c.Open("try");
                switch (m.Kind)
                {
                    case ProxyMemberKind.EventAdd:
                        c.Line($"{target}.{m.Name} += {values[0]};");
                        c.Line($"*result = global::Helix.Interop.Objects.NewHandle({values[0]});");
                        break;
                    case ProxyMemberKind.EventRemove:
                        c.Line($"if (a_token == 0)");
                        c.Line("    return 0;");
                        c.Line($"var hx_h = {gch}.FromIntPtr((global::System.IntPtr)a_token);");
                        c.Line($"{target}.{m.Name} -= ({ev!.Delegate.Cs})hx_h.Target;");
                        c.Line("hx_h.Free();");
                        break;
                    default:
                    {
                        string call = m.Kind switch
                        {
                            ProxyMemberKind.Constructor => $"new {type}({string.Join(", ", values)})",
                            ProxyMemberKind.Getter => $"{target}.{m.Name}",
                            ProxyMemberKind.Setter => $"{target}.{m.Name} = {values[0]}",
                            _ => $"{target}.{m.Name}({string.Join(", ", values)})",
                        };
                        c.Line(hasResult ? CsStore(m.Return, call) : call + ";");
                        break;
                    }
                }
                if (after.Count > 0)
                {
                    c.Close();
                    c.Open("finally");
                    foreach (string l in after)
                        c.Line(l);
                    c.Close();
                }
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
        c.Line($"var native = (delegate* unmanaged[Cdecl]<uint*, void**>)export(\"HxProxies_{ModuleIdent}_Native\");");
        c.Line("uint nativeCount = 0;");
        c.Line("void** nativeTable = native == null ? null : native(&nativeCount);");
        c.Line($"if (nativeCount != {_m.NativeMembers.Count})");
        c.Line($"    throw new global::System.InvalidOperationException(\"{_m.Module}: the native module has other interface functions\");");
        foreach (ProxyMember m in _m.NativeMembers)
            c.Line($"{m.Thunk} = ({NativeFnType(m)})nativeTable[{m.Index}];");
        c.Close();
        foreach (ProxyMember m in _m.NativeMembers)
            c.Line($"internal static {NativeFnType(m)} {m.Thunk};");
        c.Close();
        foreach (ProxyType p in _m.Types.Where(t => t.IsInterface))
            ForwardingClass(c, p);
        c.Close();
        return c.ToString();
    }

    string NativeFnType(ProxyMember m)
    {
        var abi = new List<string> { "void*" };
        foreach (ProxyParameter pa in m.Parameters)
        {
            if (pa.Type.Kind == ManagedKind.String)
            {
                abi.Add("char*");
                abi.Add("int");
            }
            else
                abi.Add(CsCallbackAbi(pa.Type));
        }
        if (m.Return.Kind == ManagedKind.String)
            abi.Add("global::Helix.Interop.InteropString*");
        else if (m.Return.Kind != ManagedKind.Void)
            abi.Add(CsCallbackAbi(m.Return) + "*");
        abi.Add("int");
        return $"delegate* unmanaged[Cdecl]<{string.Join(", ", abi)}>";
    }

    // A C++ object of a C# interface, as C# sees it during the call it was handed to.
    void ForwardingClass(Code c, ProxyType p)
    {
        string cls = $"HxNative_{Ident(p)}";
        c.Line();
        c.Line($"/// <summary>A C++ implementation of {p.FullName}, borrowed for the call it was handed to.</summary>");
        c.Open($"internal sealed unsafe class {cls} : global::{p.FullName}");
        c.Line("void* _self;");
        c.Line($"public {cls}(void* self) => _self = self;");
        c.Line("public void Invalidate() => _self = null;");
        foreach (ProxyMember m in p.Methods)
        {
            string ps = string.Join(", ", m.Parameters.Select(pa => $"{pa.Type.Cs} {pa.Name}"));
            c.Open($"public {m.Return.Cs} {m.Name}({ps})");
            c.Line("if (_self == null)");
            c.Line($"    throw new global::System.ObjectDisposedException(\"{p.FullName}\", \"a C++ object is borrowed for the call it was handed to\");");
            var args = new List<string> { "_self" };
            var fixeds = new List<string>();
            foreach (ProxyParameter pa in m.Parameters)
            {
                if (pa.Type.Kind == ManagedKind.String)
                {
                    fixeds.Add($"fixed (char* hx_{pa.Name} = {pa.Name})");
                    args.Add($"hx_{pa.Name}");
                    args.Add($"{pa.Name} == null ? 0 : {pa.Name}.Length");
                }
                else if (pa.Type.Kind == ManagedKind.Bool)
                    args.Add($"(byte)({pa.Name} ? 1 : 0)");
                else
                    args.Add(pa.Name);
            }
            string decl = m.Return.Kind switch
            {
                ManagedKind.Void => "",
                ManagedKind.String => "global::Helix.Interop.InteropString hx_r;",
                _ => $"{CsCallbackAbi(m.Return)} hx_r;",
            };
            if (decl.Length > 0)
            {
                c.Line(decl);
                args.Add("&hx_r");
            }
            foreach (string f in fixeds)
                c.Line(f);
            c.Open();
            c.Line($"int hx_s = HxProxies_{ModuleIdent}.{m.Thunk}({string.Join(", ", args)});");
            c.Line("if (hx_s != 0)");
            c.Line($"    global::Helix.Interop.Native.Fail(hx_s, \"{p.FullName}.{m.Name}\");");
            c.Close();
            if (m.Return.Kind == ManagedKind.String)
                c.Line("return global::Helix.Interop.Native.ReadString((byte*)hx_r.data, hx_r.size);");
            else if (m.Return.Kind == ManagedKind.Bool)
                c.Line("return hx_r != 0;");
            else if (decl.Length > 0)
                c.Line("return hx_r;");
            c.Close();
        }
        c.Close();
    }

    ProxyType Owner(ProxyMember m) => _m.Types.First(p => p.Methods.Contains(m) || p.Constructors.Contains(m) ||
                                                          p.Properties.Any(x => x.Getter == m || x.Setter == m) ||
                                                          p.Events.Any(x => x.Add == m || x.Remove == m));
}
