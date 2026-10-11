using System.Globalization;
using System.Text;
using System.Text.Json;

namespace WukongAutoBench.Results;

public static class ResultParser
{
    public static BenchmarkResult? TryParseFile(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return Parse(reader.ReadToEnd());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or FormatException)
        {
            return null;
        }
    }

    public static BenchmarkResult Parse(string json)
    {
        using var doc = JsonDocument.Parse(json.TrimStart('﻿', '\0').TrimEnd('\0'),
            new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });

        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new FormatException("Результат бенчмарка должен быть JSON-объектом.");

        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var records = new List<FrameRecord>();

        foreach (var property in root.EnumerateObject())
        {
            switch (property.Value.ValueKind)
            {
                case JsonValueKind.Array when property.Name.Equals("Records", StringComparison.OrdinalIgnoreCase):
                    foreach (var item in property.Value.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.Object) continue;
                        var fps = Number(item, "FrameRate") ?? Number(item, "FPS");
                        if (fps is null) continue;
                        records.Add(new FrameRecord(
                            fps.Value,
                            Number(item, "CPUUsage"),
                            Number(item, "GPUUsage"),
                            Number(item, "CPUFrameTime"),
                            Number(item, "GPUFrameTime"),
                            Number(item, "VideoMemoryUsage")));
                    }

                    break;
                case JsonValueKind.String:
                    fields[property.Name] = property.Value.GetString() ?? "";
                    break;
                case JsonValueKind.Number:
                    fields[property.Name] = property.Value.GetRawText();
                    break;
                case JsonValueKind.True:
                case JsonValueKind.False:
                    fields[property.Name] = property.Value.GetBoolean() ? "1" : "0";
                    break;
            }
        }

        if (!fields.ContainsKey("FPSAvg") && records.Count == 0)
            throw new FormatException("В файле нет ни FPSAvg, ни Records – это не результат бенчмарка.");

        return new BenchmarkResult
        {
            FpsAvg = Num(fields, "FPSAvg"),
            FpsMin = Num(fields, "FPSMin"),
            FpsMax = Num(fields, "FPSMax"),
            Fps95 = Num(fields, "FPS95"),
            CpuUsageAvg = Num(fields, "CPUAvg"),
            GpuUsageAvg = Num(fields, "GPUAvg"),
            VideoMemoryUsed = Num(fields, "VideoMem"),
            Records = records,
            Fields = fields,
        };
    }

    private static double? Num(IReadOnlyDictionary<string, string> fields, string name) =>
        fields.TryGetValue(name, out var raw) ? ParseDouble(raw) : null;

    private static double? Number(JsonElement obj, string name)
    {
        foreach (var p in obj.EnumerateObject())
        {
            if (!p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            return p.Value.ValueKind switch
            {
                JsonValueKind.Number => p.Value.GetDouble(),
                JsonValueKind.String => ParseDouble(p.Value.GetString()),
                _ => null,
            };
        }

        return null;
    }

    private static double? ParseDouble(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        raw = raw.Trim().TrimEnd('%');
        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v
            : double.TryParse(raw.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v
            : null;
    }
}
