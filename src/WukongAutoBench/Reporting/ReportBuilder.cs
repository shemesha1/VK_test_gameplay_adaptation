using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using WukongAutoBench.Config;
using WukongAutoBench.Hardware;
using WukongAutoBench.Results;

namespace WukongAutoBench.Reporting;

public sealed class ReportBuilder
{
    private readonly SystemInfo _system;
    private readonly IReadOnlyList<PassReport> _passes;
    private readonly string? _benchmarkVersion;

    public ReportBuilder(SystemInfo system, IReadOnlyList<PassReport> passes, string? benchmarkVersion)
    {
        _system = system;
        _passes = passes;
        _benchmarkVersion = benchmarkVersion;
    }

    private sealed record Section(string Title, TextTable? Table, IReadOnlyList<string> Lines);

    private IEnumerable<Section> Sections()
    {
        var hw = new TextTable("Параметр", "Значение");
        foreach (var item in _system.Items) hw.Row(item.Label, item.Value);
        if (_benchmarkVersion is not null) hw.Row("Сборка Benchmark Tool (Steam build id)", _benchmarkVersion);

        var fromBench = _passes.Select(p => p.Result).FirstOrDefault(r => r is not null);
        if (fromBench is not null)
        {
            foreach (var field in BenchmarkResult.HardwareFields)
            {
                var v = fromBench.Field(field);
                if (!string.IsNullOrWhiteSpace(v)) hw.Row($"{field} (по данным бенчмарка)", v);
            }
        }

        yield return new Section("ХАРАКТЕРИСТИКИ КОМПЬЮТЕРА", hw, []);

        var headers = new[] { "Метрика" }.Concat(_passes.Select(p => p.Profile.Title)).ToArray();
        var results = new TextTable(headers);
        void Metric(string title, Func<PassReport, string?> pick) =>
            results.Row(new[] { title }.Concat(_passes.Select(p => p.Succeeded ? pick(p) : "ошибка")).ToArray());

        Metric("Средний FPS (бенчмарк)", p => F(p.Result!.FpsAvg));
        Metric("Минимальный FPS (бенчмарк)", p => F(p.Result!.FpsMin));
        Metric("Максимальный FPS (бенчмарк)", p => F(p.Result!.FpsMax));
        Metric("5% low, FPS95 (бенчмарк)", p => F(p.Result!.Fps95));
        Metric("Медиана FPS (Records)", p => F(p.Result!.Stats?.MedianFps));
        Metric("1% low FPS (Records)", p => F(p.Result!.Stats?.Low1PercentFps));
        Metric("0.1% low FPS (Records)", p => F(p.Result!.Stats?.Low01PercentFps));
        Metric("Среднее время кадра, мс", p => F(p.Result!.Stats?.MeanFrameTimeMs, 2));
        Metric("99-й перцентиль времени кадра, мс", p => F(p.Result!.Stats?.P99FrameTimeMs, 2));
        Metric("Время кадра на CPU, мс", p => F(p.Result!.Stats?.MeanCpuFrameTimeMs, 2));
        Metric("Время кадра на GPU, мс", p => F(p.Result!.Stats?.MeanGpuFrameTimeMs, 2));
        Metric("Замеров, где кадр ограничен CPU", p => Pct(p.Result!.Stats?.CpuBoundShare));
        Metric("Загрузка CPU (бенчмарк)", p => (p.Result!.CpuUsageAvg ?? p.Result!.Stats?.MeanCpuUsage) is > 1 and var cpu ? Pct(cpu) : "не измеряет");
        Metric("Загрузка GPU (бенчмарк)", p => Pct(p.Result!.GpuUsageAvg ?? p.Result!.Stats?.MeanGpuUsage));
        Metric("Загрузка CPU (замер инструмента), ср./пик", p => p.Load?.CpuAvg is null ? null : $"{Pct(p.Load.CpuAvg)} / {Pct(p.Load.CpuPeak)}");
        Metric("Загрузка GPU (nvidia-smi), ср./пик", p => p.Load?.GpuAvg is null ? null : $"{Pct(p.Load.GpuAvg)} / {Pct(p.Load.GpuPeak)}");
        Metric("Видеопамять (бенчмарк)", p => F(p.Result!.VideoMemoryUsed ?? p.Result!.Stats?.PeakVideoMemory, 0));
        Metric("Записей Records", p => p.Result!.Stats?.Samples.ToString(CultureInfo.InvariantCulture));
        Metric("Длительность прохода", p => p.Duration is { } d ? $"{(int)d.TotalMinutes}:{d.Seconds:00}" : null);

        if (_passes.Any(p => p.Succeeded || p.Error is not null))
            yield return new Section("РЕЗУЛЬТАТЫ", results, Verdicts().ToList());

        foreach (var pass in _passes)
        {
            var table = new TextTable("Настройка", "Значение", "Было", "Где");
            foreach (var s in pass.Applied)
                table.Row(s.Title, s.Applied ? s.After : $"{s.After} (ключ не найден)", s.Before, s.Location == "UISettingData" ? "меню (UISettingData)" : s.Location);

            var lines = new List<string>
            {
                $"Разрешение окна: {pass.WindowResolution ?? "не определено"} (не меняется), масштаб рендеринга {pass.Profile.RenderScalePercent}%.",
            };

            if (pass.Result is not null)
            {
                var actual = pass.Result.Fields
                    .Where(f => !BenchmarkResult.MetricFields.Contains(f.Key, StringComparer.OrdinalIgnoreCase)
                                && !BenchmarkResult.HardwareFields.Contains(f.Key, StringComparer.OrdinalIgnoreCase))
                    .Select(f => $"{f.Key}={f.Value}");
                lines.Add("Фактически применено (по данным бенчмарка): " + string.Join(", ", actual));
            }

            lines.Add("Почему так:");
            lines.AddRange(pass.Profile.Rationale.Select(r => " - " + r));
            lines.AddRange(pass.Warnings.Select(w => "Внимание: " + w));
            if (pass.Error is not null) lines.Add("Ошибка: " + pass.Error);
            if (pass.RawFile is not null) lines.Add("Исходный файл результата: " + pass.RawFile);

            yield return new Section($"НАСТРОЙКИ: {pass.Profile.Title.ToUpperInvariant()}", pass.Applied.Count > 0 ? table : null, lines);
        }
    }

    /// <summary>Автоматическая проверка, что проходы действительно нагрузили то, что должны.</summary>
    private IEnumerable<string> Verdicts()
    {
        var cpu = _passes.FirstOrDefault(p => p.Profile.Kind == PassKind.Cpu && p.Succeeded);
        var gpu = _passes.FirstOrDefault(p => p.Profile.Kind == PassKind.Gpu && p.Succeeded);

        if (cpu?.Result!.Stats?.CpuBoundShare is { } share)
        {
            yield return share >= 70
                ? $"CPU-тест: в {share:0}% замеров кадр ограничивал процессор (время кадра CPU > GPU) – видеокарта на результат почти не влияет."
                : $"CPU-тест: процессор ограничивал кадр лишь в {share:0}% замеров – видеокарта всё ещё влияет; попробуйте --cpu-scale меньше.";
        }

        if (gpu?.Result!.Stats?.CpuBoundShare is { } cpuShareInGpu)
        {
            var gpuShare = 100 - cpuShareInGpu;
            yield return gpuShare >= 70
                ? $"GPU-тест: в {gpuShare:0}% замеров кадр ограничивала видеокарта – нагрузка легла на GPU."
                : $"GPU-тест: видеокарта ограничивала кадр только в {gpuShare:0}% замеров – возможно, слабый процессор или --gpu-scale стоит увеличить.";
        }

        var gpuUsage = gpu?.Result!.GpuUsageAvg ?? gpu?.Load?.GpuAvg;
        if (gpuUsage is > 0) yield return $"Средняя загрузка видеокарты в GPU-тесте: {gpuUsage:0}%.";

        if (cpu?.Result!.FpsAvg is > 0 && gpu?.Result!.FpsAvg is > 0)
        {
            var ratio = cpu.Result.FpsAvg.Value / gpu.Result.FpsAvg.Value;
            yield return $"Средний FPS CPU-теста в {ratio:0.00} раза {(ratio >= 1 ? "выше" : "ниже")}, чем GPU-теста.";
        }
    }

    public string ToConsole()
    {
        var sb = new StringBuilder();
        foreach (var s in Sections())
        {
            sb.AppendLine();
            sb.AppendLine(s.Title);
            sb.AppendLine(new string('=', s.Title.Length));
            if (s.Table is not null) sb.Append(s.Table.ToConsole());
            foreach (var line in s.Lines) sb.AppendLine("  " + line);
        }

        return sb.ToString();
    }

    public string ToMarkdown(DateTime started)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Black Myth: Wukong Benchmark Tool – отчёт WukongAutoBench");
        sb.AppendLine();
        sb.AppendLine($"Дата прогона: {started:yyyy-MM-dd HH:mm}");
        foreach (var s in Sections())
        {
            sb.AppendLine();
            sb.AppendLine("## " + Capitalize(s.Title));
            sb.AppendLine();
            if (s.Table is not null) sb.AppendLine(s.Table.ToMarkdown());
            foreach (var line in s.Lines)
                sb.AppendLine(line.StartsWith(" - ") ? line.TrimStart() : line + "  ");
        }

        return sb.ToString();
    }

    public string ToJson(DateTime started)
    {
        var model = new
        {
            started,
            benchmarkBuild = _benchmarkVersion,
            system = _system.Items.Select(i => new { i.Group, i.Label, i.Value }),
            passes = _passes.Select(p => new
            {
                name = p.Profile.Kind.ToString().ToLowerInvariant(),
                title = p.Profile.Title,
                succeeded = p.Succeeded,
                error = p.Error,
                warnings = p.Warnings,
                windowResolution = p.WindowResolution,
                renderScalePercent = p.Profile.RenderScalePercent,
                settings = p.Applied.Select(a => new { a.Key, a.Title, a.Location, before = a.Before, value = a.After, a.Applied }),
                result = p.Result is null ? null : new
                {
                    fpsAvg = p.Result.FpsAvg,
                    fpsMin = p.Result.FpsMin,
                    fpsMax = p.Result.FpsMax,
                    fps95 = p.Result.Fps95,
                    cpuUsageAvg = p.Result.CpuUsageAvg,
                    gpuUsageAvg = p.Result.GpuUsageAvg,
                    videoMemory = p.Result.VideoMemoryUsed,
                    stats = p.Result.Stats,
                    benchmarkFields = p.Result.Fields,
                },
                measuredLoad = p.Load,
                durationSeconds = p.Duration?.TotalSeconds,
                rawFile = p.RawFile,
            }),
        };

        return JsonSerializer.Serialize(model, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
            NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
        });
    }

    private static string? F(double? v, int digits = 1) =>
        v is null ? null : v.Value.ToString("F" + digits, CultureInfo.InvariantCulture);

    private static string? Pct(double? v) =>
        v is null ? null : v.Value.ToString("0", CultureInfo.InvariantCulture) + "%";

    private static string Capitalize(string s) =>
        s.Length == 0 ? s : char.ToUpper(s[0]) + s[1..].ToLowerInvariant().Replace("cpu", "CPU").Replace("gpu", "GPU");
}
