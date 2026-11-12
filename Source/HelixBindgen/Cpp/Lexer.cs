using System.Text;

namespace Helix.Bindgen.Cpp;

public enum TokenKind
{
    Identifier,
    Number,
    String,
    Char,
    Punct,
    End,
}

public readonly record struct Token(TokenKind Kind, string Text, int Line)
{
    public bool Is(string text) => Kind != TokenKind.String && Kind != TokenKind.Char && Text == text;
}

/// <summary>C++ tokens without comments and preprocessor lines (which Bindgen does not need).</summary>
public static class Lexer
{
    static readonly string[] Puncts = { "::", "->", "...", "&&", "||", "<=", ">=", "==", "!=", "+=", "-=", "*=", "/=", "<<" };

    public static List<Token> Tokenize(string text)
    {
        var tokens = new List<Token>();
        int i = 0, line = 1;
        bool lineStart = true;
        while (i < text.Length)
        {
            char c = text[i];
            if (c == '\n')
            {
                line++;
                i++;
                lineStart = true;
                continue;
            }
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }
            // Preprocessor lines, continued by a backslash at the end.
            if (c == '#' && lineStart)
            {
                while (i < text.Length && text[i] != '\n')
                {
                    if (text[i] == '\\' && i + 1 < text.Length && text[i + 1] == '\n')
                    {
                        line++;
                        i++;
                    }
                    i++;
                }
                continue;
            }
            lineStart = false;
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] != '\n')
                    i++;
                continue;
            }
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < text.Length && !(text[i] == '*' && text[i + 1] == '/'))
                {
                    if (text[i] == '\n')
                        line++;
                    i++;
                }
                i += 2;
                continue;
            }
            if (char.IsLetter(c) || c == '_')
            {
                int s = i;
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_'))
                    i++;
                tokens.Add(new Token(TokenKind.Identifier, text[s..i], line));
                continue;
            }
            if (char.IsDigit(c) || (c == '.' && i + 1 < text.Length && char.IsDigit(text[i + 1])))
            {
                int s = i;
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '.' || text[i] == '\'' ||
                                           ((text[i] == '+' || text[i] == '-') && (text[i - 1] == 'e' || text[i - 1] == 'E'))))
                    i++;
                tokens.Add(new Token(TokenKind.Number, text[s..i], line));
                continue;
            }
            if (c == '"' || c == '\'')
            {
                char quote = c;
                var sb = new StringBuilder();
                i++;
                while (i < text.Length && text[i] != quote)
                {
                    if (text[i] == '\\' && i + 1 < text.Length)
                    {
                        sb.Append(text[i + 1] switch { 'n' => '\n', 't' => '\t', '0' => '\0', _ => text[i + 1] });
                        i += 2;
                        continue;
                    }
                    sb.Append(text[i++]);
                }
                i++;
                tokens.Add(new Token(quote == '"' ? TokenKind.String : TokenKind.Char, sb.ToString(), line));
                continue;
            }
            string? p = Puncts.FirstOrDefault(x => string.CompareOrdinal(text, i, x, 0, x.Length) == 0);
            if (p != null)
            {
                tokens.Add(new Token(TokenKind.Punct, p, line));
                i += p.Length;
                continue;
            }
            tokens.Add(new Token(TokenKind.Punct, c.ToString(), line));
            i++;
        }
        tokens.Add(new Token(TokenKind.End, "", line));
        return tokens;
    }
}
