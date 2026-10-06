using System.Text;

namespace DLSS5Tool.Core;

/// <summary>
/// Order-preserving INI editor for ReShade-style files. Keys before the first [section] belong to the
/// empty section (ReShadePreset.ini keeps Techniques= there). Comments and unknown lines are kept verbatim.
/// </summary>
public sealed class IniFile
{
    private readonly List<string> _lines = new();

    public IniFile() { }

    public static IniFile Parse(string text)
    {
        var ini = new IniFile();
        if (text.Length > 0 && text[0] == '\uFEFF') text = text[1..];
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            ini._lines.Add(line);
        }
        while (ini._lines.Count > 0 && ini._lines[^1].Length == 0) ini._lines.RemoveAt(ini._lines.Count - 1);
        return ini;
    }

    public static IniFile Load(string path)
    {
        return File.Exists(path) ? Parse(File.ReadAllText(path, Encoding.UTF8)) : new IniFile();
    }

    public void Save(string path)
    {
        File.WriteAllText(path, ToText(), new UTF8Encoding(false));
    }

    public string ToText()
    {
        var sb = new StringBuilder();
        foreach (var l in _lines) sb.Append(l).Append("\r\n");
        return sb.ToString();
    }

    public IEnumerable<string> Sections
    {
        get
        {
            foreach (var l in _lines)
            {
                var t = l.Trim();
                if (t.StartsWith('[') && t.EndsWith(']')) yield return t[1..^1];
            }
        }
    }

    public bool HasSection(string section) => FindSection(section) >= 0;

    public string? Get(string section, string key)
    {
        var (start, end) = SectionRange(section);
        if (start < 0) return null;
        for (var i = start; i < end; i++)
        {
            if (TryParseKey(_lines[i], out var k, out var v) && k.Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                return v;
            }
        }
        return null;
    }

    public void Set(string section, string key, string value)
    {
        var (start, end) = SectionRange(section);
        if (start < 0)
        {
            if (section.Length == 0)
            {
                _lines.Insert(0, key + "=" + value);
                if (_lines.Count > 1 && _lines[1].Length != 0 && !_lines[1].TrimStart().StartsWith('[')) { /* keep contiguous */ }
                return;
            }
            if (_lines.Count > 0 && _lines[^1].Length != 0) _lines.Add(string.Empty);
            _lines.Add("[" + section + "]");
            _lines.Add(key + "=" + value);
            return;
        }
        for (var i = start; i < end; i++)
        {
            if (TryParseKey(_lines[i], out var k, out _) && k.Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                _lines[i] = key + "=" + value;
                return;
            }
        }
        var insertAt = end;
        while (insertAt > start && _lines[insertAt - 1].Length == 0) insertAt--;
        _lines.Insert(insertAt, key + "=" + value);
    }

    public void SetIfMissing(string section, string key, string value)
    {
        if (Get(section, key) == null) Set(section, key, value);
    }

    public bool Remove(string section, string key)
    {
        var (start, end) = SectionRange(section);
        if (start < 0) return false;
        for (var i = start; i < end; i++)
        {
            if (TryParseKey(_lines[i], out var k, out _) && k.Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                _lines.RemoveAt(i);
                return true;
            }
        }
        return false;
    }

    /// <summary>Comma-separated list helpers (ReShade's Techniques=, PreprocessorDefinitions=, Apps=).</summary>
    public List<string> GetList(string section, string key)
    {
        var v = Get(section, key);
        if (string.IsNullOrWhiteSpace(v)) return new List<string>();
        return v.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
    }

    public void SetList(string section, string key, IEnumerable<string> values)
    {
        Set(section, key, string.Join(",", values));
    }

    private int FindSection(string section)
    {
        if (section.Length == 0) return 0;
        for (var i = 0; i < _lines.Count; i++)
        {
            var t = _lines[i].Trim();
            if (t.StartsWith('[') && t.EndsWith(']') && t[1..^1].Equals(section, StringComparison.OrdinalIgnoreCase)) return i + 1;
        }
        return -1;
    }

    private (int start, int end) SectionRange(string section)
    {
        if (section.Length == 0)
        {
            var e = 0;
            while (e < _lines.Count && !_lines[e].TrimStart().StartsWith('[')) e++;
            return (0, e);
        }
        var start = FindSection(section);
        if (start < 0) return (-1, -1);
        var end = start;
        while (end < _lines.Count && !_lines[end].TrimStart().StartsWith('[')) end++;
        return (start, end);
    }

    private static bool TryParseKey(string line, out string key, out string value)
    {
        key = string.Empty; value = string.Empty;
        var t = line.TrimStart();
        if (t.Length == 0 || t[0] == ';' || t[0] == '#' || t[0] == '[') return false;
        var eq = t.IndexOf('=');
        if (eq <= 0) return false;
        key = t[..eq].Trim();
        value = t[(eq + 1)..].Trim();
        return true;
    }
}
