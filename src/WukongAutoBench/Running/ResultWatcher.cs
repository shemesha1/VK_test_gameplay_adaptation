using WukongAutoBench.Results;

namespace WukongAutoBench.Running;

public sealed class ResultWatcher
{
    private readonly IReadOnlyList<string> _directories;
    private readonly Dictionary<string, DateTime> _known = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _lastSize = new(StringComparer.OrdinalIgnoreCase);

    public ResultWatcher(IReadOnlyList<string> directories) => _directories = directories;

    public IReadOnlyList<string> Directories => _directories;

    /// <summary>Каталоги, где бенчмарк может хранить историю прогонов (порядок = приоритет).</summary>
    public static IReadOnlyList<string> DefaultDirectories(string installDir)
    {
        var temp = Path.GetTempPath();
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return new[]
            {
                Path.Combine(temp, "b1", "BenchMarkHistory"),
                Path.Combine(local, "Temp", "b1", "BenchMarkHistory"),
                Path.Combine(local, "b1", "Saved", "BenchMarkHistory"),
                Path.Combine(installDir, "b1", "Saved", "BenchMarkHistory"),
            }
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public void Snapshot()
    {
        _known.Clear();
        _lastSize.Clear();
        foreach (var file in EnumerateFiles()) _known[file] = File.GetLastWriteTimeUtc(file);
    }

    /// <summary>Новый готовый результат или null. Файл считается готовым, когда его размер не менялся между опросами.</summary>
    public (string Path, BenchmarkResult Result)? TryTakeNew()
    {
        foreach (var file in EnumerateFiles().OrderByDescending(File.GetLastWriteTimeUtc))
        {
            var written = File.GetLastWriteTimeUtc(file);
            if (_known.TryGetValue(file, out var before) && before >= written) continue;

            long size;
            try { size = new FileInfo(file).Length; }
            catch (IOException) { continue; }

            if (size == 0) continue;
            if (!_lastSize.TryGetValue(file, out var previous) || previous != size)
            {
                _lastSize[file] = size;
                continue;
            }

            var result = ResultParser.TryParseFile(file);
            if (result is null) continue;

            _known[file] = written;
            return (file, result);
        }

        return null;
    }

    private IEnumerable<string> EnumerateFiles()
    {
        foreach (var dir in _directories)
        {
            if (!Directory.Exists(dir)) continue;
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).ToList(); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }

            foreach (var f in files) yield return f;
        }
    }
}
