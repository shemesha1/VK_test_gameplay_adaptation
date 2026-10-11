using WukongAutoBench.Config;
using WukongAutoBench.Results;
using WukongAutoBench.Running;

namespace WukongAutoBench.Reporting;

public sealed class PassReport
{
    public required BenchmarkProfile Profile { get; init; }
    public IReadOnlyList<AppliedSetting> Applied { get; set; } = [];
    public string? WindowResolution { get; set; }
    public BenchmarkResult? Result { get; set; }
    public string? RawFile { get; set; }
    public LoadSummary? Load { get; set; }
    public TimeSpan? Duration { get; set; }
    public string? Error { get; set; }
    public List<string> Warnings { get; } = [];

    public bool Succeeded => Result is not null;
}
