using System.Text;

namespace DLSS5Tool.Core.Games;

/// <summary>Valve KeyValues (text VDF/ACF) parser: nested string-keyed nodes.</summary>
public sealed class VdfNode
{
    public string Name { get; }
    public string? Value { get; set; }
    public List<VdfNode> Children { get; } = new();

    public VdfNode(string name) { Name = name; }

    public VdfNode? this[string key] => Children.FirstOrDefault(c => c.Name.Equals(key, StringComparison.OrdinalIgnoreCase));

    public string? Get(string key) => this[key]?.Value;

    public static VdfNode Parse(string text)
    {
        var root = new VdfNode("<root>");
        var stack = new Stack<VdfNode>();
        stack.Push(root);
        var i = 0;
        string? pendingKey = null;
        while (i < text.Length)
        {
            var ch = text[i];
            if (char.IsWhiteSpace(ch)) { i++; continue; }
            if (ch == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] != '\n') i++;
                continue;
            }
            if (ch == '{')
            {
                var node = new VdfNode(pendingKey ?? string.Empty);
                stack.Peek().Children.Add(node);
                stack.Push(node);
                pendingKey = null;
                i++;
                continue;
            }
            if (ch == '}')
            {
                if (stack.Count > 1) stack.Pop();
                pendingKey = null;
                i++;
                continue;
            }
            var token = ReadToken(text, ref i);
            if (pendingKey == null)
            {
                pendingKey = token;
            }
            else
            {
                stack.Peek().Children.Add(new VdfNode(pendingKey) { Value = token });
                pendingKey = null;
            }
        }
        return root;
    }

    private static string ReadToken(string text, ref int i)
    {
        var sb = new StringBuilder();
        if (text[i] == '"')
        {
            i++;
            while (i < text.Length && text[i] != '"')
            {
                if (text[i] == '\\' && i + 1 < text.Length)
                {
                    i++;
                    sb.Append(text[i] switch { 'n' => '\n', 't' => '\t', '"' => '"', '\\' => '\\', var c => c });
                }
                else
                {
                    sb.Append(text[i]);
                }
                i++;
            }
            i++;
            return sb.ToString();
        }
        while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] != '{' && text[i] != '}')
        {
            sb.Append(text[i]);
            i++;
        }
        return sb.ToString();
    }
}
