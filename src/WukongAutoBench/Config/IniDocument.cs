using System.Text;
using System.Text.RegularExpressions;

namespace WukongAutoBench.Config;

public sealed class IniDocument
{
    private const string UiLinePrefix = "UISettingData=";
    private readonly List<string> _lines;

    public Encoding Encoding { get; }
    public string NewLine { get; }

    private IniDocument(List<string> lines, Encoding encoding, string newLine)
    {
        _lines = lines;
        Encoding = encoding;
        NewLine = newLine;
    }

    public static IniDocument Load(string path)
    {
        var bytes = File.ReadAllBytes(path);
        return Parse(bytes);
    }

    public static IniDocument Parse(byte[] bytes)
    {
        Encoding encoding;
        int bomLength;
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) { encoding = new UnicodeEncoding(false, true); bomLength = 2; }
        else if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) { encoding = new UnicodeEncoding(true, true); bomLength = 2; }
        else if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) { encoding = new UTF8Encoding(true); bomLength = 3; }
        else { encoding = new UTF8Encoding(false); bomLength = 0; }

        var text = encoding.GetString(bytes, bomLength, bytes.Length - bomLength);
        return ParseText(text, encoding);
    }

    public static IniDocument ParseText(string text, Encoding? encoding = null)
    {
        var newLine = text.Contains("\r\n") ? "\r\n" : "\n";
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        return new IniDocument(lines, encoding ?? new UTF8Encoding(false), newLine);
    }

    public string ToText() => string.Join(NewLine, _lines);

    public byte[] ToBytes()
    {
        var preamble = Encoding.GetPreamble();
        var body = Encoding.GetBytes(ToText());
        return preamble.Concat(body).ToArray();
    }

    public void Save(string path)
    {
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, ToBytes());
        File.Move(temp, path, overwrite: true);
    }

    public string? Get(string section, string key)
    {
        var (start, end) = FindSection(section);
        if (start < 0) return null;
        for (var i = start + 1; i < end; i++)
        {
            if (TrySplit(_lines[i], out var k, out var v) && k.Equals(key, StringComparison.OrdinalIgnoreCase)) return v;
        }

        return null;
    }

    /// <summary>Устанавливает значение. Если ключа нет – дописывает в конец секции; если нет секции – создаёт её.</summary>
    public void Set(string section, string key, string value)
    {
        var (start, end) = FindSection(section);
        if (start < 0)
        {
            if (_lines.Count > 0 && _lines[^1].Length != 0) _lines.Add("");
            _lines.Add($"[{section}]");
            _lines.Add($"{key}={value}");
            return;
        }

        for (var i = start + 1; i < end; i++)
        {
            if (TrySplit(_lines[i], out var k, out _) && k.Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                _lines[i] = $"{k}={value}";
                return;
            }
        }

        var insertAt = end;
        while (insertAt - 1 > start && string.IsNullOrWhiteSpace(_lines[insertAt - 1])) insertAt--;
        _lines.Insert(insertAt, $"{key}={value}");
    }

    /// <summary>Имя первой секции, в которой есть ключ (UE иногда меняет имя класса настроек между версиями).</summary>
    public string? FindSectionContaining(string key)
    {
        string? current = null;
        foreach (var line in _lines)
        {
            var t = line.Trim();
            if (t.StartsWith('[') && t.EndsWith(']')) { current = t[1..^1]; continue; }
            if (current is not null && TrySplit(line, out var k, out _) && k.Equals(key, StringComparison.OrdinalIgnoreCase))
                return current;
        }

        return null;
    }

    private (int Start, int End) FindSection(string section)
    {
        var header = $"[{section}]";
        var start = _lines.FindIndex(l => l.Trim().Equals(header, StringComparison.OrdinalIgnoreCase));
        if (start < 0) return (-1, -1);
        var end = start + 1;
        while (end < _lines.Count && !_lines[end].TrimStart().StartsWith('[')) end++;
        return (start, end);
    }

    private static bool TrySplit(string line, out string key, out string value)
    {
        key = value = "";
        var trimmed = line.TrimStart();
        if (trimmed.Length == 0 || trimmed[0] is ';' or '#' or '[') return false;
        var eq = trimmed.IndexOf('=');
        if (eq <= 0) return false;
        key = trimmed[..eq].Trim();
        value = trimmed[(eq + 1)..];
        return true;
    }

    public bool HasUiSettings => FindUiLine() >= 0;

    public IReadOnlyDictionary<string, string> GetUiSettings()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var index = FindUiLine();
        if (index < 0) return result;
        foreach (Match m in UiPairRegex.Matches(_lines[index]))
            result[m.Groups["key"].Value] = m.Groups["value"].Value;
        return result;
    }

    public string? GetUiSetting(string key) =>
        GetUiSettings().TryGetValue(key, out var value) ? value : null;

    /// <summary>
    /// Меняет значение внутри UISettingData. Новые ключи НЕ добавляет: неизвестная игре пара может
    /// уронить бенчмарк на старте, поэтому если ключа нет – возвращаем false и сообщаем в лог.
    /// </summary>
    public bool SetUiSetting(string key, string value)
    {
        var index = FindUiLine();
        if (index < 0) return false;

        var pattern = new Regex(
            "\\(\\s*\"" + Regex.Escape(key) + "\"\\s*,\\s*\"(?<value>[^\"]*)\"\\s*\\)",
            RegexOptions.IgnoreCase);
        var line = _lines[index];
        var match = pattern.Match(line);
        if (!match.Success) return false;

        var valueGroup = match.Groups["value"];
        _lines[index] = line[..valueGroup.Index] + value + line[(valueGroup.Index + valueGroup.Length)..];
        return true;
    }

    private static readonly Regex UiPairRegex =
        new("\\(\\s*\"(?<key>[^\"]+)\"\\s*,\\s*\"(?<value>[^\"]*)\"\\s*\\)", RegexOptions.Compiled);

    private int FindUiLine() =>
        _lines.FindIndex(l => l.TrimStart().StartsWith(UiLinePrefix, StringComparison.OrdinalIgnoreCase));
}
