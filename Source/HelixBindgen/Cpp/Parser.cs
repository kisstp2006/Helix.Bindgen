using Helix.Bindgen.Model;

namespace Helix.Bindgen.Cpp;

/// <summary>
/// Reads the tagged subset of C++ headers: namespaces, tagged classes, structs, enums and interfaces, and the tagged
/// members inside them; every other declaration is skipped by balancing its brackets. Untagged public functions of
/// tagged types are kept too, as the setters properties pair with.
/// </summary>
public sealed class Parser
{
    static readonly HashSet<string> Tags = new() { "HX_CLASS", "HX_STRUCT", "HX_INTERFACE", "HX_ENUM", "HX_FIELD", "HX_PROPERTY", "HX_FUNCTION", "HX_EVENT", "HX_HANDLE" };

    readonly List<Token> _t;
    readonly string _file;
    readonly ModuleModel _module;
    readonly string _defaultNamespace;
    readonly List<string> _namespaces = new();
    int _pos;

    Parser(string file, string text, ModuleModel module, string defaultNamespace)
    {
        _file = file;
        _t = Lexer.Tokenize(text);
        _module = module;
        _defaultNamespace = defaultNamespace;
    }

    public static void ParseFile(string path, ModuleModel module, string defaultNamespace)
    {
        var p = new Parser(path, File.ReadAllText(path), module, defaultNamespace);
        p.ParseScope(null);
        module.Headers.Add(path);
    }

    public static void ParseText(string name, string text, ModuleModel module, string defaultNamespace)
    {
        var p = new Parser(name, text, module, defaultNamespace);
        p.ParseScope(null);
    }

    // ----------------------------------------------------------------------------------------------- tokens

    Token Peek(int ahead = 0) => _t[Math.Min(_pos + ahead, _t.Count - 1)];
    Token Next() => _t[Math.Min(_pos++, _t.Count - 1)];
    Location Here() => new() { File = _file, Line = Peek().Line };

    BindgenException Error(string message) => new(Here(), message);

    void Expect(string text)
    {
        if (!Peek().Is(text))
            throw Error($"'{text}' expected, found '{Peek().Text}'");
        _pos++;
    }

    string Identifier()
    {
        if (Peek().Kind != TokenKind.Identifier)
            throw Error($"a name expected, found '{Peek().Text}'");
        return Next().Text;
    }

    // ----------------------------------------------------------------------------------------------- scopes

    // Declarations until the scope's closing brace (not consumed) or the end.
    void ParseScope(TypeModel? owner)
    {
        bool isPublic = owner == null || owner.Kind != TypeKind.Class || owner.Tag.Has("PublicByDefault");
        if (owner != null && owner.Kind is TypeKind.Struct or TypeKind.Interface)
            isPublic = true;
        while (true)
        {
            Token tok = Peek();
            if (tok.Kind == TokenKind.End || tok.Is("}"))
                return;
            if (tok.Is(";"))
            {
                _pos++;
                continue;
            }
            if (tok.Is("namespace"))
            {
                _pos++;
                var parts = new List<string>();
                while (Peek().Kind == TokenKind.Identifier)
                {
                    parts.Add(Next().Text);
                    if (!Peek().Is("::"))
                        break;
                    _pos++;
                }
                if (!Peek().Is("{"))
                {
                    SkipDeclaration();
                    continue;
                }
                _pos++;
                _namespaces.AddRange(parts);
                ParseScope(null);
                Expect("}");
                _namespaces.RemoveRange(_namespaces.Count - parts.Count, parts.Count);
                continue;
            }
            if (owner != null && (tok.Is("public") || tok.Is("protected") || tok.Is("private")) && Peek(1).Is(":"))
            {
                isPublic = tok.Is("public");
                _pos += 2;
                continue;
            }
            if (tok.Kind == TokenKind.Identifier && tok.Text.StartsWith("HX_", StringComparison.Ordinal) && Peek(1).Is("(") && !Tags.Contains(tok.Text))
            {
                // Another macro of the HX_ family (HX_ENGINE_CLASS, ...): its arguments skipped, no semicolon after it.
                _pos += 2;
                for (int depth = 1; depth > 0 && Peek().Kind != TokenKind.End;)
                {
                    Token t = Next();
                    depth += t.Is("(") ? 1 : t.Is(")") ? -1 : 0;
                }
                continue;
            }
            if (tok.Kind == TokenKind.Identifier && tok.Text.StartsWith("HX_", StringComparison.Ordinal) && Peek(1).Is("("))
            {
                string tag = Next().Text;
                Location where = Here();
                TagArgs args = ParseTagArgs();
                switch (tag)
                {
                    case "HX_CLASS": ParseRecord(TypeKind.Class, args, owner, where); break;
                    case "HX_STRUCT": ParseRecord(TypeKind.Struct, args, owner, where); break;
                    case "HX_INTERFACE": ParseRecord(TypeKind.Interface, args, owner, where); break;
                    case "HX_ENUM": ParseEnum(args, owner, where); break;
                    case "HX_HANDLE": ParseHandle(args, where); break;
                    case "HX_FIELD":
                    case "HX_PROPERTY":
                    case "HX_FUNCTION":
                    case "HX_EVENT":
                        if (owner == null)
                            throw new BindgenException(where, $"{tag} outside a tagged type");
                        if (!isPublic && !(tag == "HX_FIELD" && args.Has("SerializeField")))
                            throw new BindgenException(where, $"{tag} on a member that is not public (only SerializeField fields may be private)");
                        ParseMember(owner, tag, args, where, isPublic);
                        break;
                    default:
                        throw new BindgenException(where, $"unknown tag {tag}");
                }
                continue;
            }
            // An untagged function of a tagged type: kept for pairing properties (public ones) and for the methods of
            // script classes (any access).
            if (owner != null)
            {
                int start = _pos;
                try
                {
                    ParseMember(owner, null, new TagArgs(), Here(), isPublic);
                    continue;
                }
                catch (BindgenException)
                {
                    _pos = start;
                }
            }
            SkipDeclaration();
        }
    }

    // To the end of a declaration: a ';' outside brackets, or the body of a function (a '{' after parentheses).
    void SkipDeclaration()
    {
        int depth = 0;
        bool sawParen = false;
        while (Peek().Kind != TokenKind.End)
        {
            Token tok = Next();
            if (tok.Is("(") || tok.Is("["))
            {
                sawParen |= tok.Is("(");
                depth++;
            }
            else if (tok.Is(")") || tok.Is("]"))
                depth--;
            else if (tok.Is("{"))
            {
                SkipBalanced();
                if (depth == 0 && sawParen && !Peek().Is(";"))
                    return;
            }
            else if (tok.Is("}"))
            {
                // The scope's own closing brace: put it back.
                _pos--;
                return;
            }
            else if (tok.Is(";") && depth <= 0)
                return;
        }
    }

    // After a '{': to its matching '}'.
    void SkipBalanced()
    {
        int depth = 1;
        while (depth > 0 && Peek().Kind != TokenKind.End)
        {
            Token tok = Next();
            if (tok.Is("{"))
                depth++;
            else if (tok.Is("}"))
                depth--;
        }
    }

    TagArgs ParseTagArgs()
    {
        var args = new TagArgs();
        Expect("(");
        while (!Peek().Is(")"))
        {
            string key = Identifier();
            if (Peek().Is("="))
            {
                _pos++;
                Token v = Next();
                // A negative number is two tokens.
                args.Values[key] = v.Is("-") && Peek().Kind == TokenKind.Number ? "-" + Next().Text : v.Text;
            }
            else
                args.Flags.Add(key);
            if (Peek().Is(","))
                _pos++;
            else if (!Peek().Is(")"))
                throw Error($"',' or ')' expected in the tag, found '{Peek().Text}'");
        }
        _pos++;
        return args;
    }

    // ----------------------------------------------------------------------------------------------- types

    string CppNamespace() => string.Join("::", _namespaces);

    // HX_HANDLE(CSharp = "Namespace.Type") before a class, struct or alias: its name and C# type; the declaration itself
    // is not read.
    void ParseHandle(TagArgs args, Location where)
    {
        string cs = args.Get("CSharp") ?? throw new BindgenException(where, "HX_HANDLE needs CSharp = \"<C# type>\"");
        int start = _pos;
        if (Peek().Is("class") || Peek().Is("struct"))
        {
            _pos++;
            SkipAttributes();
        }
        else if (Peek().Is("using"))
            _pos++;
        else
            throw Error("class, struct or using expected after HX_HANDLE");
        string name = Identifier();
        _pos = start;
        SkipDeclaration();
        string ns = CppNamespace();
        _module.Handles[name] = cs;
        if (ns.Length > 0)
            _module.Handles[ns + "::" + name] = cs;
    }

    void ParseRecord(TypeKind kind, TagArgs args, TypeModel? outer, Location where)
    {
        if (!(Peek().Is("class") || Peek().Is("struct")))
            throw Error("class or struct expected after the tag");
        bool isStruct = Next().Is("struct");
        SkipAttributes();
        var type = new TypeModel
        {
            Kind = kind,
            Name = Identifier(),
            CppNamespace = CppNamespace(),
            Tag = args,
            Where = where,
            Outer = outer,
        };
        if (isStruct)
            type.Tag.Flags.Add("PublicByDefault");
        type.CsNamespace = args.Get("Namespace") ?? outer?.CsNamespace ?? _defaultNamespace;
        if (Peek().Is("final"))
        {
            _pos++;
            type.Tag.Flags.Add("Sealed");
        }
        if (Peek().Is(":"))
        {
            _pos++;
            while (!Peek().Is("{"))
            {
                while (Peek().Is("public") || Peek().Is("protected") || Peek().Is("private") || Peek().Is("virtual"))
                    _pos++;
                TypeRef b = ParseTypeTokens(ReadTypeTokens());
                type.Bases.Add(b.Name);
                if (Peek().Is(","))
                    _pos++;
            }
        }
        Expect("{");
        ParseScope(type);
        Expect("}");
        if (Peek().Is(";"))
            _pos++;
        PairProperties(type);
        if (_untagged.TryGetValue(type, out var others))
            type.Untagged.AddRange(others);
        if (type.Tag.Has("SerializedOnly"))
        {
            string qualified = "::" + type.CppName;
            _module.SerializedOnly[type.Name] = qualified;
            _module.SerializedOnly[type.CppName] = qualified;
            return;
        }
        _module.Types.Add(type);
    }

    void SkipAttributes()
    {
        while (Peek().Is("[") && Peek(1).Is("["))
        {
            int depth = 0;
            do
            {
                Token tok = Next();
                if (tok.Is("["))
                    depth++;
                else if (tok.Is("]"))
                    depth--;
            } while (depth > 0 && Peek().Kind != TokenKind.End);
        }
    }

    void ParseEnum(TagArgs args, TypeModel? outer, Location where)
    {
        Expect("enum");
        if (Peek().Is("class") || Peek().Is("struct"))
            _pos++;
        var type = new TypeModel
        {
            Kind = TypeKind.Enum,
            Name = Identifier(),
            CppNamespace = CppNamespace(),
            Tag = args,
            Where = where,
            Outer = outer,
        };
        type.CsNamespace = args.Get("Namespace") ?? outer?.CsNamespace ?? _defaultNamespace;
        if (Peek().Is(":"))
        {
            _pos++;
            type.Underlying = ParseTypeTokens(ReadTypeTokens());
        }
        Expect("{");
        long next = 0;
        while (!Peek().Is("}"))
        {
            string name = Identifier();
            long value = next;
            if (Peek().Is("="))
            {
                _pos++;
                var expr = new List<Token>();
                while (!Peek().Is(",") && !Peek().Is("}"))
                    expr.Add(Next());
                value = Evaluate(expr, type);
            }
            type.EnumValues.Add(new EnumValueModel { Name = name, Value = value });
            next = value + 1;
            if (Peek().Is(","))
                _pos++;
        }
        _pos++;
        Expect(";");
        _module.Types.Add(type);
    }

    // Enum values: numbers, earlier values, -, <<, |, parentheses.
    long Evaluate(List<Token> expr, TypeModel type)
    {
        int at = 0;
        long Primary()
        {
            Token tok = expr[at++];
            if (tok.Is("-"))
                return -Primary();
            if (tok.Is("("))
            {
                long v = Or();
                at++;
                return v;
            }
            if (tok.Kind == TokenKind.Number)
            {
                string s = tok.Text.Replace("'", "").TrimEnd('u', 'U', 'l', 'L');
                return s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? Convert.ToInt64(s[2..], 16) : long.Parse(s);
            }
            if (tok.Kind == TokenKind.Identifier)
            {
                EnumValueModel? earlier = type.EnumValues.FirstOrDefault(v => v.Name == tok.Text);
                if (earlier == null)
                    throw new BindgenException(new Location { File = _file, Line = tok.Line }, $"the enum value {tok.Text} is not known here");
                return earlier.Value;
            }
            throw new BindgenException(new Location { File = _file, Line = tok.Line }, $"an enum value Bindgen cannot compute: {tok.Text}");
        }
        long Shift()
        {
            long v = Primary();
            while (at < expr.Count && expr[at].Is("<<"))
            {
                at++;
                v <<= (int)Primary();
            }
            return v;
        }
        long Or()
        {
            long v = Shift();
            while (at < expr.Count && expr[at].Is("|"))
            {
                at++;
                v |= Shift();
            }
            return v;
        }
        return Or();
    }

    // The tokens of a type: to a ',' '{' ';' '=' ')' or a name followed by one of those, at bracket depth 0.
    List<Token> ReadTypeTokens()
    {
        var list = new List<Token>();
        int angle = 0;
        while (Peek().Kind != TokenKind.End)
        {
            Token tok = Peek();
            if (angle == 0 && (tok.Is(",") || tok.Is("{") || tok.Is(";") || tok.Is(")")))
                break;
            if (tok.Is("<"))
                angle++;
            else if (tok.Is(">"))
                angle--;
            list.Add(Next());
        }
        return list;
    }

    // A type from its tokens (qualifiers, a qualified name, template arguments, pointers and references).
    TypeRef ParseTypeTokens(List<Token> tokens)
    {
        int at = 0;
        TypeRef Parse()
        {
            var t = new TypeRef();
            while (at < tokens.Count && (tokens[at].Is("const") || tokens[at].Is("typename") || tokens[at].Is("volatile")))
            {
                if (tokens[at].Is("const"))
                    t.Const = true;
                at++;
            }
            var name = new List<string>();
            if (at < tokens.Count && tokens[at].Is("::"))
                at++;
            while (at < tokens.Count && tokens[at].Kind == TokenKind.Identifier)
            {
                name.Add(tokens[at++].Text);
                if (at < tokens.Count && tokens[at].Is("::"))
                    at++;
                else
                    break;
            }
            if (name.Count == 0)
                throw new BindgenException(new Location { File = _file, Line = tokens.Count > 0 ? tokens[Math.Min(at, tokens.Count - 1)].Line : Peek().Line },
                                           "a type expected");
            t.Name = string.Join("::", name);
            if (at < tokens.Count && tokens[at].Is("<"))
            {
                at++;
                while (at < tokens.Count && !tokens[at].Is(">"))
                {
                    t.Arguments.Add(Parse());
                    if (at < tokens.Count && tokens[at].Is(","))
                        at++;
                }
                at++;
            }
            while (at < tokens.Count)
            {
                if (tokens[at].Is("const"))
                    t.Const = true;
                else if (tokens[at].Is("*"))
                    t.Pointers++;
                else if (tokens[at].Is("&") || tokens[at].Is("&&"))
                    t.Reference = true;
                else
                    break;
                at++;
            }
            return t;
        }
        TypeRef result = Parse();
        if (at != tokens.Count)
            throw new BindgenException(new Location { File = _file, Line = tokens[at].Line }, $"unexpected '{tokens[at].Text}' in a type");
        return result;
    }

    // ----------------------------------------------------------------------------------------------- members

    void ParseMember(TypeModel owner, string? tag, TagArgs args, Location where, bool isPublic)
    {
        SkipAttributes();
        // Specifiers.
        bool isStatic = false, isVirtual = false;
        while (Peek().Is("static") || Peek().Is("virtual") || Peek().Is("inline") || Peek().Is("constexpr") || Peek().Is("explicit") ||
               Peek().Is("[") || Peek().Is("friend"))
        {
            if (Peek().Is("["))
            {
                SkipAttributes();
                continue;
            }
            if (Peek().Is("friend"))
                throw Error("a friend declaration");
            isStatic |= Peek().Is("static");
            isVirtual |= Peek().Is("virtual");
            _pos++;
        }
        if (Peek().Is("template") || Peek().Is("using") || Peek().Is("typedef") || Peek().Is("enum") || Peek().Is("class") || Peek().Is("struct") ||
            Peek().Is("~") || Peek().Is("operator"))
            throw Error("not a member Bindgen binds");
        // The declaration's tokens up to '(' or the end: a function has a name then '(' at depth 0.
        var head = new List<Token>();
        int angle = 0;
        while (Peek().Kind != TokenKind.End)
        {
            Token tok = Peek();
            if (angle == 0 && (tok.Is("(") || tok.Is(";") || tok.Is("=") || tok.Is("{")))
                break;
            if (tok.Is("<"))
                angle++;
            else if (tok.Is(">"))
                angle--;
            head.Add(Next());
        }
        if (head.Count == 0 || head[^1].Kind != TokenKind.Identifier)
            throw Error("a member name expected");
        string name = head[^1].Text;
        head.RemoveAt(head.Count - 1);
        if (Peek().Is("("))
        {
            bool constructor = head.Count == 0;
            if (constructor && (name != owner.Name || tag != "HX_FUNCTION" || !args.Has("Constructor")))
                throw Error("a constructor without HX_FUNCTION(Constructor)");
            var m = constructor
                ? new MethodModel
                {
                    Name = name, Return = new TypeRef { Name = owner.Name, Pointers = 1 }, Static = true, RealConstructor = true, Tag = args, Where = where,
                }
                : new MethodModel { Name = name, Return = ParseTypeTokens(head), Static = isStatic, Virtual = isVirtual, Tag = args, Where = where, Public = isPublic };
            // Void: the function's result is dropped (a native status the reference's member does not have).
            if (args.Has("Void") && !m.RealConstructor)
                m.Return = new TypeRef { Name = "void" };
            _pos++;
            while (!Peek().Is(")"))
            {
                var param = new List<Token>();
                int depth = 0;
                while (Peek().Kind != TokenKind.End)
                {
                    Token tok = Peek();
                    if (depth == 0 && (tok.Is(",") || tok.Is(")") || tok.Is("=")))
                        break;
                    if (tok.Is("<") || tok.Is("("))
                        depth++;
                    else if (tok.Is(">") || tok.Is(")"))
                        depth--;
                    param.Add(Next());
                }
                var p = new ParameterModel();
                if (param.Count > 1 && param[^1].Kind == TokenKind.Identifier && !param[^1].Is("const"))
                {
                    p.Name = param[^1].Text;
                    param.RemoveAt(param.Count - 1);
                }
                else
                    p.Name = $"arg{m.Parameters.Count}";
                p.Type = ParseTypeTokens(param);
                if (Peek().Is("="))
                {
                    _pos++;
                    var def = new List<string>();
                    int d = 0;
                    while (Peek().Kind != TokenKind.End && !(d == 0 && (Peek().Is(",") || Peek().Is(")"))))
                    {
                        Token tok = Next();
                        if (tok.Is("(") || tok.Is("{"))
                            d++;
                        else if (tok.Is(")") || tok.Is("}"))
                            d--;
                        def.Add(tok.Kind == TokenKind.String ? "\"" + tok.Text + "\"" : tok.Text);
                    }
                    p.Default = string.Concat(def);
                }
                m.Parameters.Add(p);
                if (Peek().Is(","))
                    _pos++;
            }
            _pos++;
            // Qualifiers and the end: const, noexcept, override, final, = 0, = default, a body.
            while (Peek().Is("const") || Peek().Is("noexcept") || Peek().Is("override") || Peek().Is("final"))
            {
                m.Const |= Peek().Is("const");
                m.Virtual |= Peek().Is("override");
                _pos++;
            }
            if (Peek().Is("="))
            {
                m.Abstract = Peek(1).Is("0");
                while (!Peek().Is(";") && Peek().Kind != TokenKind.End)
                    _pos++;
            }
            // A constructor's member initializers.
            if (Peek().Is(":"))
            {
                while (!Peek().Is("{") && Peek().Kind != TokenKind.End)
                    _pos++;
            }
            if (Peek().Is("{"))
            {
                _pos++;
                SkipBalanced();
            }
            else
                Expect(";");
            AddFunction(owner, tag, m);
            return;
        }
        // A field (or an event field), with an optional initializer.
        TypeRef type = ParseTypeTokens(head);
        if (Peek().Is("="))
        {
            while (!Peek().Is(";") && Peek().Kind != TokenKind.End)
            {
                if (Next().Is("{"))
                    SkipBalanced();
            }
        }
        else if (Peek().Is("{"))
        {
            _pos++;
            SkipBalanced();
        }
        Expect(";");
        if (tag == null)
            return;
        if (tag == "HX_EVENT")
        {
            owner.Events.Add(MakeEvent(name, type, false, isStatic, args, where));
            return;
        }
        if (tag != "HX_FIELD")
            throw new BindgenException(where, $"{tag} on a field");
        if (!isPublic)
            args.Flags.Add("Private");
        owner.Fields.Add(new FieldModel { Name = name, Type = type, Static = isStatic, Tag = args, Where = where });
    }

    EventModel MakeEvent(string name, TypeRef type, bool isFunction, bool isStatic, TagArgs args, Location where)
    {
        string last = type.Name.Split("::")[^1];
        if (last != "Event" && last != "Query")
            throw new BindgenException(where, $"HX_EVENT on {type.Spelling()}: an Event<...> or a Query<...> (or a function returning one by reference)");
        return new EventModel
        {
            CsName = args.Get("Name") ?? name,
            Member = name,
            IsFunction = isFunction,
            Static = isStatic,
            Query = last == "Query",
            Arguments = type.Arguments,
            Tag = args,
            Where = where,
        };
    }

    // Untagged functions only help pairing; tagged ones become methods, properties or events.
    readonly Dictionary<TypeModel, List<MethodModel>> _untagged = new();

    void AddFunction(TypeModel owner, string? tag, MethodModel m)
    {
        switch (tag)
        {
            case null:
                if (!_untagged.TryGetValue(owner, out var list))
                    _untagged[owner] = list = new List<MethodModel>();
                list.Add(m);
                break;
            case "HX_FUNCTION":
                owner.Methods.Add(m);
                break;
            case "HX_PROPERTY":
                if (m.Parameters.Count != 0)
                    throw new BindgenException(m.Where, $"HX_PROPERTY on {m.Name}: a getter takes no parameters");
                owner.Properties.Add(new PropertyModel { CsName = m.Tag.Get("Name") ?? m.Name, Getter = m });
                break;
            case "HX_EVENT":
                if (m.Parameters.Count != 0 || !m.Return.Reference)
                    throw new BindgenException(m.Where, $"HX_EVENT on {m.Name}: a function without parameters returning the event by reference");
                owner.Events.Add(MakeEvent(m.Name, m.Return, true, m.Static, m.Tag, m.Where));
                break;
            default:
                throw new BindgenException(m.Where, $"{tag} on a function");
        }
    }

    // A property's setter: Set<Name>(value) among the type's functions (tagged or not), unless ReadOnly.
    void PairProperties(TypeModel type)
    {
        _untagged.TryGetValue(type, out var untagged);
        foreach (PropertyModel p in type.Properties)
        {
            if (p.Getter.Tag.Has("ReadOnly"))
                continue;
            string setter = p.Getter.Tag.Get("Setter") ?? ("Set" + char.ToUpperInvariant(p.Getter.Name[0]) + p.Getter.Name[1..]);
            p.Setter = type.Methods.Concat((untagged ?? new List<MethodModel>()).Where(m => m.Public))
                .FirstOrDefault(m => m.Name == setter && m.Parameters.Count == 1 && m.Static == p.Getter.Static);
            if (p.Setter != null)
                type.Methods.Remove(p.Setter);
        }
    }
}
