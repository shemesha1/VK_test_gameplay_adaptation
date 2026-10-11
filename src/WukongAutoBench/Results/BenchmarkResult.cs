namespace WukongAutoBench.Results;

public sealed record FrameRecord(
    double FrameRate,
    double? CpuUsage,
    double? GpuUsage,
    double? CpuFrameTimeMs,
    double? GpuFrameTimeMs,
    double? VideoMemoryUsage);

public sealed class BenchmarkResult
{
    public double? FpsAvg { get; init; }
    public double? FpsMin { get; init; }
    public double? FpsMax { get; init; }
    public double? Fps95 { get; init; }
    public double? CpuUsageAvg { get; init; }
    public double? GpuUsageAvg { get; init; }
    public double? VideoMemoryUsed { get; init; }

    public IReadOnlyList<FrameRecord> Records { get; init; } = [];
    public IReadOnlyDictionary<string, string> Fields { get; init; } = new Dictionary<string, string>();

    public FrameStatistics? Stats => FrameStatistics.From(Records);

    public string? Field(string name) => Fields.TryGetValue(name, out var v) ? v : null;

    public static readonly string[] HardwareFields =
        ["CPUModel", "GPUModel", "GpuDriverVer", "VideoMemSize", "SysMem", "SysVer", "GameVer"];

    public static readonly string[] MetricFields =
        ["FPSAvg", "FPSMin", "FPSMax", "FPS95", "CPUAvg", "GPUAvg", "VideoMem", "Records"];
}

public sealed record FrameStatistics(
    int Samples,
    double MeanFps,
    double MedianFps,
    double Low1PercentFps,
    double Low01PercentFps,
    double StdDevFps,
    double MeanFrameTimeMs,
    double P99FrameTimeMs,
    double? MeanCpuFrameTimeMs,
    double? MeanGpuFrameTimeMs,
    double? CpuBoundShare,
    double? MeanCpuUsage,
    double? MeanGpuUsage,
    double? PeakVideoMemory)
{
    public static FrameStatistics? From(IReadOnlyList<FrameRecord> records)
    {
        var fps = records.Select(r => r.FrameRate).Where(f => f > 0 && double.IsFinite(f)).ToArray();
        if (fps.Length == 0) return null;

        Array.Sort(fps);
        var mean = fps.Average();
        var std = Math.Sqrt(fps.Sum(f => (f - mean) * (f - mean)) / fps.Length);
        var frameTimes = fps.Select(f => 1000.0 / f).OrderBy(t => t).ToArray();

        double? Avg(Func<FrameRecord, double?> pick)
        {
            var values = records.Select(pick).Where(v => v is > 0 && double.IsFinite(v.Value)).Select(v => v!.Value).ToArray();
            return values.Length == 0 ? null : values.Average();
        }

        var paired = records
            .Where(r => r.CpuFrameTimeMs is > 0 && r.GpuFrameTimeMs is > 0)
            .ToArray();
        double? cpuBound = paired.Length == 0
            ? null
            : 100.0 * paired.Count(r => r.CpuFrameTimeMs > r.GpuFrameTimeMs) / paired.Length;

        var vram = records.Select(r => r.VideoMemoryUsage).Where(v => v is > 0).Select(v => v!.Value).ToArray();

        return new FrameStatistics(
            fps.Length,
            mean,
            Percentile(fps, 50),
            Percentile(fps, 1),
            Percentile(fps, 0.1),
            std,
            frameTimes.Average(),
            Percentile(frameTimes, 99),
            Avg(r => r.CpuFrameTimeMs),
            Avg(r => r.GpuFrameTimeMs),
            cpuBound,
            Avg(r => r.CpuUsage),
            Avg(r => r.GpuUsage),
            vram.Length == 0 ? null : vram.Max());
    }

    /// <summary>Перцентиль с линейной интерполяцией по отсортированному массиву.</summary>
    internal static double Percentile(double[] sorted, double percent)
    {
        if (sorted.Length == 1) return sorted[0];
        var rank = percent / 100.0 * (sorted.Length - 1);
        var lo = (int)Math.Floor(rank);
        var hi = (int)Math.Ceiling(rank);
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (rank - lo);
    }
}
