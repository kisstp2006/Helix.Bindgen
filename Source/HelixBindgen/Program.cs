using System.Text;
using Helix.Bindgen.Cpp;
using Helix.Bindgen.Generators;
using Helix.Bindgen.Managed;
using Helix.Bindgen.Model;

namespace Helix.Bindgen;

/// <summary>
/// Native to C#:  HelixBindgen --module Name --namespace CsNamespace [--kind Engine|NativeGame] --header a.h ...
///                  [--reference b.h ...] [--include-root dir ...] --out-cpp file.cpp --out-cs file.cs [--out-api file.h]
/// C# to native:  HelixBindgen --module AssemblyName --assembly file.dll --proxy Namespace.Type ... [--reference b.h ...]
///                  [--include-root dir ...] --out-h file.h --out-cpp file.cpp --out-cs file.cs
/// Headers are the module's own (generated); references are other modules' headers (their types known, not generated).
/// The second form writes C++ proxies of C# classes and the C# exports they call. Outputs are written only when
/// their text changes; members a proxy cannot have are listed on the standard output.
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            return Run(args);
        }
        catch (BindgenException e)
        {
            Console.Error.WriteLine(e.Message);
            return 1;
        }
        catch (ArgumentException e)
        {
            Console.Error.WriteLine($"HelixBindgen: {e.Message}");
            return 2;
        }
    }

    static int Run(string[] args)
    {
        string? module = null, ns = null, outCpp = null, outCs = null, outH = null, outApi = null, assembly = null;
        var proxies = new List<string>();
        string kind = "NativeGame";
        var headers = new List<string>();
        var references = new List<string>();
        var roots = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            string Value() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
            switch (args[i])
            {
                case "--module": module = Value(); break;
                case "--namespace": ns = Value(); break;
                case "--kind": kind = Value(); break;
                case "--header": headers.Add(Value()); break;
                case "--reference": references.Add(Value()); break;
                case "--include-root": roots.Add(Path.GetFullPath(Value())); break;
                case "--out-cpp": outCpp = Value(); break;
                case "--out-cs": outCs = Value(); break;
                case "--out-h": outH = Value(); break;
                case "--out-api": outApi = Value(); break;
                case "--assembly": assembly = Value(); break;
                case "--proxy": proxies.Add(Value()); break;
                default:
                    if (args[i].StartsWith('@'))
                    {
                        // A response file: one argument per line.
                        string[] more = File.ReadAllLines(args[i][1..]).Where(l => l.Trim().Length > 0).Select(l => l.Trim()).ToArray();
                        args = args[..(i + 1)].Concat(more).Concat(args[(i + 1)..]).ToArray();
                        break;
                    }
                    throw new ArgumentException($"unknown argument {args[i]}");
            }
        }
        if (module == null || (outCpp == null && outCs == null))
            throw new ArgumentException("--module and an output are needed");
        if (kind is not ("Engine" or "NativeGame"))
            throw new ArgumentException("--kind is Engine or NativeGame");

        var model = new ModuleModel { Name = module };
        if (assembly != null)
            return Proxies(module, assembly, proxies, references, roots, ns ?? "", outH, outCpp, outCs);
        foreach (string r in references)
        {
            int before = model.Types.Count;
            Parser.ParseFile(r, model, ns ?? "");
            foreach (TypeModel t in model.Types.Skip(before))
                t.External = true;
        }
        model.Headers.Clear();
        foreach (string h in headers)
            Parser.ParseFile(h, model, ns ?? "");

        var binding = new Binding(model);
        if (outCpp != null)
            WriteIfChanged(outCpp, CppGenerator.Generate(binding, headers.Select(h => Include(h, roots)), kind));
        if (outCs != null)
            WriteIfChanged(outCs, CsGenerator.Generate(binding));
        if (outApi != null)
            WriteIfChanged(outApi, ApiGenerator.Generate(binding, headers.Select(h => Include(h, roots))));
        return 0;
    }

    static int Proxies(string module, string assembly, List<string> types, List<string> references, List<string> roots, string ns,
                       string? outH, string? outCpp, string? outCs)
    {
        if (outH == null || outCpp == null || outCs == null || types.Count == 0)
            throw new ArgumentException("proxies need --proxy types, --out-h, --out-cpp and --out-cs");
        var refs = new ModuleModel { Name = module };
        foreach (string r in references)
            Parser.ParseFile(r, refs, ns);
        foreach (TypeModel t in refs.Types)
            t.External = true;
        ProxyModel model = AssemblyReader.Read(assembly, module, types, refs);
        foreach (string skipped in model.Skipped)
            Console.WriteLine($"HelixBindgen: no proxy member for {skipped}");
        (string header, string source, string exports) =
            ProxyGenerator.Generate(model, refs, references.Select(h => Include(h, roots)), "\"" + Path.GetFileName(outH) + "\"");
        WriteIfChanged(outH, header);
        WriteIfChanged(outCpp, source);
        WriteIfChanged(outCs, exports);
        return 0;
    }

    // <path below an include root>, or the full path in quotes.
    static string Include(string header, List<string> roots)
    {
        string full = Path.GetFullPath(header);
        foreach (string root in roots)
        {
            string rel = Path.GetRelativePath(root, full);
            if (!rel.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(rel))
                return "<" + rel.Replace('\\', '/') + ">";
        }
        return "\"" + full.Replace('\\', '/') + "\"";
    }

    static void WriteIfChanged(string path, string text)
    {
        if (File.Exists(path) && File.ReadAllText(path) == text)
            return;
        string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (dir != null)
            Directory.CreateDirectory(dir);
        File.WriteAllText(path, text, new UTF8Encoding(false));
    }
}
