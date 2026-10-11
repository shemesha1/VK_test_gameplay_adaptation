using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using WukongAutoBench;
using WukongAutoBench.Config;
using WukongAutoBench.Hardware;
using WukongAutoBench.Reporting;
using WukongAutoBench.Results;
using WukongAutoBench.Running;
using WukongAutoBench.Steam;
using WukongAutoBench.Util;

Console.OutputEncoding = Encoding.UTF8;
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;

Options options;
try
{
    options = Options.Parse(args);
}
catch (ArgumentException e)
{
    Log.Error(e.Message);
    return 1;
}

if (options.Command == "help")
{
    Console.WriteLine(Options.Usage);
    return 0;
}

try
{
    return options.Command switch
    {
        "run" when OperatingSystem.IsWindows() => App.Run(options),
        "inspect" when OperatingSystem.IsWindows() => App.Inspect(options),
        "run" or "inspect" => throw new PlatformNotSupportedException(
            "Запуск бенчмарка поддерживается только в Windows (Benchmark Tool – Windows-приложение, меню проходится через WinAPI). " +
            "На других ОС доступна команда report."),
        "report" => App.ReportFromFiles(options),
        _ => throw new ArgumentException($"Неизвестная команда «{options.Command}». Справка: --help"),
    };
}
catch (Exception e) when (e is ArgumentException or FileNotFoundException or DirectoryNotFoundException or PlatformNotSupportedException)
{
    Log.Error(e.Message);
    return 1;
}

internal static class App
{
    private static readonly TimeSpan FirstLaunchExtraTime = TimeSpan.FromMinutes(10);

    [SupportedOSPlatform("windows")]
    public static int Run(Options options)
    {
        WindowInput.EnableDpiAwareness();
        var started = DateTime.Now;

        Log.Step("Поиск Black Myth: Wukong Benchmark Tool");
        var install = SteamLocator.Locate(options.InstallDir);
        Log.Ok($"Бенчмарк: {install.InstallDir}");

        var watcher = new ResultWatcher(ResultWatcher.DefaultDirectories(install.InstallDir));
        var runner = new BenchmarkRunner(install, watcher, options);

        var configPath = TryFindConfig(install);
        if (configPath is null && !options.DryRun)
        {
            configPath = runner.FirstLaunch(() => TryFindConfig(install));
            if (configPath is not null)
            {
                options.PassTimeout += FirstLaunchExtraTime;
                Log.Info($"Таймаут прохода увеличен до {options.PassTimeout.TotalMinutes:0} мин: " +
                         "после первого запуска игра может ещё докомпилировать шейдеры.");
            }
        }

        if (configPath is null) throw MissingConfig(install);
        Log.Ok($"Конфиг: {configPath}");

        var outDir = Path.Combine(options.OutputDir, started.ToString("yyyy-MM-dd_HH-mm-ss"));
        Directory.CreateDirectory(outDir);
        runner.SnapshotDir = Path.Combine(outDir, "screens");

        var guard = new ConfigGuard(configPath);
        guard.RecoverLeftover();
        var original = File.ReadAllBytes(configPath);
        var baseline = IniDocument.Parse(original);

        var windowHeight = options.RenderHeight ?? BenchmarkProfile.ReadWindowHeight(baseline)
            ?? Win32.GetSystemMetrics(Win32.SM_CYSCREEN);
        var windowResolution = BenchmarkProfile.ReadWindowResolution(baseline) ?? $"?x{windowHeight}";
        if (!baseline.HasUiSettings)
            Log.Warn("В конфиге нет строки UISettingData – будут изменены только группы качества UE. " +
                     "Запустите бенчмарк вручную один раз и закройте его, чтобы игра записала полный конфиг.");

        Log.Step("Сбор характеристик компьютера");
        var system = SystemInfo.Collect(install.InstallDir);

        var gpuRayTracing = options.GpuRayTracing ?? BenchmarkProfile.SupportsRayTracing(system.GpuNames);
        if (options.RunGpu && options.GpuRayTracing is null)
            Log.Info(gpuRayTracing
                ? "Видеокарта NVIDIA RTX – трассировка лучей в GPU-тесте включена (выключить: --no-gpu-rt)."
                : "Видеокарта не из линейки NVIDIA RTX – трассировка лучей в GPU-тесте выключена (включить: --gpu-rt).");

        var profiles = new List<BenchmarkProfile>();
        if (options.RunCpu) profiles.Add(BenchmarkProfile.Cpu(options.CpuRenderScale));
        if (options.RunGpu) profiles.Add(BenchmarkProfile.Gpu(options.GpuRenderScale, gpuRayTracing));

        var reports = new List<PassReport>();

        if (!options.DryRun) guard.Backup();

        ConsoleCancelEventHandler onCancel = (_, e) =>
        {
            e.Cancel = false;
            Log.Warn("Прервано пользователем – закрываю бенчмарк и восстанавливаю настройки.");
            BenchmarkRunner.KillBenchmark("прерывание");
            if (!options.KeepConfig) guard.Restore();
        };
        Console.CancelKeyPress += onCancel;

        try
        {
            foreach (var profile in profiles)
            {
                var report = new PassReport { Profile = profile, WindowResolution = windowResolution };
                reports.Add(report);

                var ini = IniDocument.Parse(original);
                report.Applied = profile.ApplyTo(ini, windowHeight);
                foreach (var missed in report.Applied.Where(a => !a.Applied))
                    report.Warnings.Add($"ключ {missed.Key} не найден в UISettingData этой версии бенчмарка – пропущен");
                if (BenchmarkProfile.MarkFirstRunDone(ini) && profile == profiles[0])
                    Log.Info("Первичная настройка игры ещё не пройдена – отмечаю её пройденной в конфиге " +
                             "(яркость и язык по умолчанию, соглашение о конфиденциальности принято), чтобы игра сразу открыла главное меню. " +
                             "После прогона исходный конфиг возвращается.");

                if (options.DryRun)
                {
                    var preview = Path.Combine(outDir, $"{profile.Kind.ToString().ToLowerInvariant()}_GameUserSettings.ini");
                    File.WriteAllBytes(preview, ini.ToBytes());
                    Log.Ok($"{profile.Title}: dry-run, конфиг сохранён для просмотра: {preview}");
                    continue;
                }

                ini.Save(configPath);
                Log.Ok($"{profile.Title}: настройки записаны ({report.Applied.Count(a => a.Applied)} параметров), рендер {profile.RenderScalePercent}% от {windowResolution}");

                try
                {
                    var outcome = runner.Run(profile.Title, () => ini.Save(configPath));
                    report.Result = outcome.Result;
                    report.Load = outcome.Load;
                    report.Duration = outcome.Duration;
                    report.RawFile = Path.Combine(outDir, $"{profile.Kind.ToString().ToLowerInvariant()}_result.json");
                    File.Copy(outcome.RawFile, report.RawFile, overwrite: true);
                    CheckAppliedSettings(report);
                }
                catch (Exception e) when (e is TimeoutException or InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
                {
                    report.Error = e.Message;
                    Log.Error(e.Message);
                }
            }
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
            BenchmarkRunner.KillBenchmark("завершение");
            if (!options.DryRun)
            {
                if (options.KeepConfig) Log.Warn($"--keep-config: настройки не восстановлены, оригинал – {guard.BackupPath}");
                else guard.Restore();
            }
        }

        return WriteReport(system, reports, install.BuildId, outDir, started);
    }

    [SupportedOSPlatform("windows")]
    public static int Inspect(Options options)
    {
        var install = SteamLocator.Locate(options.InstallDir);
        Log.Ok($"Бенчмарк: {install.InstallDir}");
        Log.Info($"Основной exe: {install.ShippingExe}");
        Log.Info($"Лаунчер: {install.LauncherExe ?? "нет"}; Steam build id: {install.BuildId ?? "?"}");

        var configPath = TryFindConfig(install) ?? throw MissingConfig(install);
        var ini = IniDocument.Load(configPath);
        Log.Ok($"Конфиг: {configPath} ({ini.Encoding.WebName})");
        Log.Info($"Разрешение окна в конфиге: {BenchmarkProfile.ReadWindowResolution(ini) ?? "не указано"}");
        var ui = ini.GetUiSettings();
        Log.Info(ui.Count == 0
            ? "UISettingData: не найдено"
            : "UISettingData: " + string.Join(", ", ui.Select(kv => $"{kv.Key}={kv.Value}")));

        foreach (var dir in ResultWatcher.DefaultDirectories(install.InstallDir))
        {
            var count = Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Count() : 0;
            Log.Info($"Каталог результатов: {dir} – {(Directory.Exists(dir) ? $"файлов: {count}" : "нет")}");
        }

        var system = SystemInfo.Collect(install.InstallDir);
        Console.WriteLine(new ReportBuilder(system, [], install.BuildId).ToConsole());
        return 0;
    }

    public static int ReportFromFiles(Options options)
    {
        var started = DateTime.Now;
        var reports = new List<PassReport>();

        void Add(string? file, Func<BenchmarkResult, BenchmarkProfile> profileFor)
        {
            if (file is null) return;
            var result = ResultParser.TryParseFile(file)
                         ?? throw new FileNotFoundException($"Не удалось прочитать результат: {file}");
            var report = new PassReport
            {
                Profile = profileFor(result),
                Result = result,
                RawFile = Path.GetFullPath(file),
                WindowResolution = result.Field("ScreenResolution"),
            };
            CheckAppliedSettings(report);
            reports.Add(report);
        }

        Add(options.CpuResultFile, _ => BenchmarkProfile.Cpu(options.CpuRenderScale));
        Add(options.GpuResultFile, result => BenchmarkProfile.Gpu(options.GpuRenderScale,
            options.GpuRayTracing ?? result.Field("Rtx")?.Trim() is { Length: > 0 } and not "0"));
        if (reports.Count == 0) throw new ArgumentException("Укажите хотя бы один файл: report --cpu <файл> --gpu <файл>");

        var system = OperatingSystem.IsWindows() ? SystemInfo.Collect(null) : new SystemInfo();
        var outDir = Path.Combine(options.OutputDir, started.ToString("yyyy-MM-dd_HH-mm-ss") + "_report");
        Directory.CreateDirectory(outDir);
        return WriteReport(system, reports, null, outDir, started);
    }

    /// <summary>Пресет качества «пользовательский»: так игра помечает набор, где группы качества не совпадают.</summary>
    private const string CustomQualityLevel = "6";

    /// <summary>
    /// Сверка: бенчмарк пишет в результат настройки, с которыми реально шёл прогон.
    /// Расхождения с тем, что записал инструмент, попадают в отчёт как предупреждения.
    /// При пресете 6 (в CPU-тесте дальность прорисовки на максимуме, остальное на минимуме)
    /// сверяются отдельные группы качества.
    /// В файле результата ImageQuality – проценты масштаба (в конфиге то же поле хранит высоту кадра
    /// в пикселях), но на случай других версий бенчмарка подходящим считается любое из двух значений.
    /// </summary>
    internal static void CheckAppliedSettings(PassReport report)
    {
        var result = report.Result!;
        var profile = report.Profile;

        void Expect(string field, string expected, string title)
        {
            var actual = result.Field(field);
            if (actual is not null && actual.Trim() != expected)
                report.Warnings.Add($"{title}: ожидалось {expected}, бенчмарк сообщает {actual}");
        }

        var quality = profile.Quality.ToString(CultureInfo.InvariantCulture);
        if (result.Field("QualityLevel")?.Trim() == CustomQualityLevel)
        {
            Expect("AntiAliasing", quality, "сглаживание");
            Expect("PostProcessing", quality, "постобработка");
            Expect("ShadowQuality", quality, "тени");
            Expect("TextureQuality", quality, "текстуры");
            Expect("MaterialQuality", quality, "материалы");
            Expect("VegetationQuality", quality, "растительность");
        }
        else
        {
            Expect("QualityLevel", quality, "общий пресет качества");
        }

        Expect("ViewDistance", profile.ViewDistance.ToString(CultureInfo.InvariantCulture), "дальность прорисовки");
        Expect("InsertFrame", "0", "генерация кадров");
        Expect("Rtx", profile.RayTracing ? BenchmarkProfile.RayTracingLevel : "0", "уровень трассировки лучей");

        if (double.TryParse(result.Field("ImageQuality"), NumberStyles.Float, CultureInfo.InvariantCulture, out var reported))
        {
            var screenHeight = HeightOf(result.Field("ScreenResolution"));
            double? expectedHeight = screenHeight is null ? null : screenHeight.Value * profile.RenderScalePercent / 100.0;

            var matchesPercent = Math.Abs(reported - profile.RenderScalePercent) <= 1;
            var matchesHeight = expectedHeight is not null && Math.Abs(reported - expectedHeight.Value) <= 2;
            var canJudge = expectedHeight is not null || reported <= 200;

            if (canJudge && !matchesPercent && !matchesHeight)
                report.Warnings.Add(
                    $"масштаб рендеринга: ожидалось {profile.RenderScalePercent}%" +
                    (expectedHeight is null ? "" : $" (≈{expectedHeight.Value:0} px по высоте)") +
                    $", бенчмарк сообщает ImageQuality={reported}");
        }
    }

    /// <summary>
    /// Высота кадра из строки разрешения вида «2560x1440». null, если строку не удалось разобрать.
    /// </summary>
    internal static int? HeightOf(string? resolution)
    {
        var parts = resolution?.Split('x', 'X', '*', '×');
        if (parts is not { Length: 2 }) return null;
        return int.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var height) && height > 0
            ? height
            : null;
    }

    private static int WriteReport(SystemInfo system, List<PassReport> reports, string? build, string outDir, DateTime started)
    {
        var builder = new ReportBuilder(system, reports, build);
        Console.WriteLine(builder.ToConsole());

        File.WriteAllText(Path.Combine(outDir, "report.md"), builder.ToMarkdown(started), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(outDir, "report.json"), builder.ToJson(started), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(outDir, "run.log"), Log.Text, new UTF8Encoding(false));
        Log.Ok($"Отчёт сохранён: {outDir}");

        return reports.All(r => r.Succeeded || r.Result is null && r.Error is null) ? 0 : 2;
    }

    /// <summary>Места, где бенчмарк обычно хранит GameUserSettings.ini.</summary>
    private static string[] ConfigCandidates(BenchmarkInstallation install) =>
    [
        Path.Combine(install.InstallDir, "b1", "Saved", "Config", "Windows", "GameUserSettings.ini"),
        Path.Combine(install.InstallDir, "b1", "Saved", "Config", "WindowsNoEditor", "GameUserSettings.ini"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "b1", "Saved", "Config", "Windows", "GameUserSettings.ini"),
    ];

    /// <summary>Путь к GameUserSettings.ini или null, если бенчмарк его ещё не создал.</summary>
    private static string? TryFindConfig(BenchmarkInstallation install)
    {
        var found = ConfigCandidates(install).FirstOrDefault(File.Exists);
        if (found is not null) return found;

        var saved = Path.Combine(install.InstallDir, "b1", "Saved");
        return Directory.Exists(saved)
            ? Directory.EnumerateFiles(saved, "GameUserSettings.ini", SearchOption.AllDirectories).FirstOrDefault()
            : null;
    }

    /// <summary>Ошибка «конфиг не найден» с подсказкой, что делать, и списком проверенных мест.</summary>
    private static FileNotFoundException MissingConfig(BenchmarkInstallation install) =>
        new("GameUserSettings.ini не найден. Запустите Benchmark Tool вручную один раз (при первом старте он создаёт конфиг " +
            "и компилирует шейдеры), закройте его и повторите. Искал: " + string.Join("; ", ConfigCandidates(install)));
}
