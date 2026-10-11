using System.Text;

namespace WukongAutoBench.Steam;

public sealed class VdfNode
{
    public string? Value { get; init; }
    public Dictionary<string, VdfNode> Children { get; } = new(StringComparer.OrdinalIgnoreCase);

    public VdfNode? this[string key] => Children.TryGetValue(key, out var node) ? node : null;

    public string? GetString(string key) => this[key]?.Value;
}

public static class VdfParser
{
    public static VdfNode Parse(string text)
    {
        var tokens = Tokenize(text).ToList();
        var position = 0;
        var root = new VdfNode();
        ParseBlock(tokens, ref position, root, isRoot: true);
        return root;
    }

    private static void ParseBlock(List<string> tokens, ref int position, VdfNode target, bool isRoot)
    {
        while (position < tokens.Count)
        {
            var token = tokens[position++];
            if (token == "}")
            {
                if (isRoot) continue;
                return;
            }

            if (token == "{") continue;
            var key = token;
            if (position >= tokens.Count) break;

            var next = tokens[position++];
            if (next == "{")
            {
                var child = new VdfNode();
                ParseBlock(tokens, ref position, child, isRoot: false);
                target.Children[key] = child;
            }
            else
            {
                target.Children[key] = new VdfNode { Value = next };
            }
        }
    }

    private static IEnumerable<string> Tokenize(string text)
    {
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }

            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] != '\n') i++;
                continue;
            }

            if (c is '{' or '}')
            {
                i++;
                yield return c.ToString();
                continue;
            }

            if (c == '"')
            {
                i++;
                var sb = new StringBuilder();
                while (i < text.Length && text[i] != '"')
                {
                    if (text[i] == '\\' && i + 1 < text.Length)
                    {
                        var e = text[i + 1];
                        sb.Append(e switch { 'n' => '\n', 't' => '\t', _ => e });
                        i += 2;
                        continue;
                    }

                    sb.Append(text[i++]);
                }

                i++;
                yield return sb.ToString();
                continue;
            }

            var start = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] is not ('{' or '}' or '"')) i++;
            yield return text[start..i];
        }
    }
}
