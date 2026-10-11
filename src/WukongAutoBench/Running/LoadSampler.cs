using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;

namespace WukongAutoBench.Running;

public sealed record LoadSummary(double? CpuAvg, double? CpuPeak, double? GpuAvg, double? GpuPeak, string GpuSource, int Samples);

[SupportedOSPlatform("windows")]
public sealed class LoadSampler : IDisposable
{
    private readonly List<double> _cpu = [];
    private readonly List<double> _gpu = [];
    private readonly CancellationTokenSource _cts = new();
    private readonly string? _nvidiaSmi;
    private Task? _task;
    private volatile bool _recording;

    public LoadSampler()
    {
        _nvidiaSmi = FindNvidiaSmi();
    }

    public void Start()
    {
        _task = Task.Run(Loop);
    }

    public bool Recording { get => _recording; set => _recording = value; }

    public LoadSummary Stop()
    {
        _cts.Cancel();
        try { _task?.Wait(3000); } catch { }

        lock (_cpu)
        {
            return new LoadSummary(
                _cpu.Count == 0 ? null : _cpu.Average(),
                _cpu.Count == 0 ? null : _cpu.Max(),
                _gpu.Count == 0 ? null : _gpu.Average(),
                _gpu.Count == 0 ? null : _gpu.Max(),
                _nvidiaSmi is null ? "нет (nvidia-smi не найден)" : "nvidia-smi",
                _cpu.Count);
        }
    }

    private async Task Loop()
    {
        Win32.GetSystemTimes(out var idle0, out var kernel0, out var user0);
        while (!_cts.IsCancellationRequested)
        {
            try { await Task.Delay(1000, _cts.Token); }
            catch (OperationCanceledException) { break; }

            Win32.GetSystemTimes(out var idle1, out var kernel1, out var user1);
            var total = (kernel1 - kernel0) + (user1 - user0);
            var busy = total - (idle1 - idle0);
            (idle0, kernel0, user0) = (idle1, kernel1, user1);
            if (!_recording || total <= 0) continue;

            var gpu = _nvidiaSmi is null ? null : QueryNvidia();
            lock (_cpu)
            {
                _cpu.Add(100.0 * busy / total);
                if (gpu is not null) _gpu.Add(gpu.Value);
            }
        }
    }

    private double? QueryNvidia()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(_nvidiaSmi!,
                "--query-gpu=utilization.gpu --format=csv,noheader,nounits")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (p is null) return null;
            var text = p.StandardOutput.ReadToEnd();
            p.WaitForExit(2000);
            var first = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
            return double.TryParse(first, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? FindNvidiaSmi()
    {
        var system = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "nvidia-smi.exe");
        if (File.Exists(system)) return system;
        var programFiles = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe");
        return File.Exists(programFiles) ? programFiles : null;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
