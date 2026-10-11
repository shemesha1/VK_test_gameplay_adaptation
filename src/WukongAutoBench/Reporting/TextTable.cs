using System.Text;

namespace WukongAutoBench.Reporting;

public sealed class TextTable
{
    private readonly string[] _headers;
    private readonly List<string[]> _rows = [];

    public TextTable(params string[] headers) => _headers = headers;

    public TextTable Row(params string?[] cells)
    {
        _rows.Add(cells.Select(c => c ?? "–").ToArray());
        return this;
    }

    public int Count => _rows.Count;

    public string ToConsole(string indent = "  ")
    {
        var widths = _headers.Select((h, i) => Math.Max(h.Length, _rows.Count == 0 ? 0 : _rows.Max(r => i < r.Length ? r[i].Length : 0))).ToArray();
        var numeric = _headers.Select((_, i) => i > 0 && _rows.All(r => i >= r.Length || IsNumeric(r[i]))).ToArray();
        var sb = new StringBuilder();
        void Line(string[] cells)
        {
            sb.Append(indent);
            for (var i = 0; i < widths.Length; i++)
            {
                var cell = i < cells.Length ? cells[i] : "";
                sb.Append(numeric[i] ? cell.PadLeft(widths[i]) : cell.PadRight(widths[i]));
                if (i < widths.Length - 1) sb.Append("   ");
            }

            sb.AppendLine();
        }

        Line(_headers);
        sb.Append(indent).AppendLine(string.Join("   ", widths.Select(w => new string('-', w))));
        foreach (var row in _rows) Line(row);
        return sb.ToString();
    }

    public string ToMarkdown()
    {
        var sb = new StringBuilder();
        sb.AppendLine("| " + string.Join(" | ", _headers.Select(Escape)) + " |");
        sb.AppendLine("|" + string.Join("|", _headers.Select((_, i) => i == 0 ? "---" : "---:")) + "|");
        foreach (var row in _rows) sb.AppendLine("| " + string.Join(" | ", row.Select(Escape)) + " |");
        return sb.ToString();
    }

    private static bool IsNumeric(string cell) =>
        cell.Length > 0 && cell.All(c => char.IsDigit(c) || c is '.' or '%' or '-' or '–' or ':' or ' ' or '/' or 'x');

    private static string Escape(string s) => s.Replace("|", "\\|");
}
