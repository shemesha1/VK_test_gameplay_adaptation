using System.Text;
using System.Text.Json;
using WukongAutoBench;
using WukongAutoBench.Config;
using WukongAutoBench.Hardware;
using WukongAutoBench.Reporting;
using WukongAutoBench.Results;
using WukongAutoBench.Running;
using WukongAutoBench.Steam;

var tests = new List<(string Name, Action Body)>();
void Test(string name, Action body) => tests.Add((name, body));

string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
string TempDir()
{
    var dir = Path.Combine(Path.GetTempPath(), "wab-tests-" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(dir);
    return dir;
}

Test("VDF: пути библиотек из нового формата, экранированные слеши", () =>
{
    var paths = SteamLocator.ParseLibraryFolders(File.ReadAllText(Fixture("libraryfolders.vdf"))).ToList();
    Assert.Equal(2, paths.Count);
    Assert.Equal(@"C:\Program Files (x86)\Steam", paths[0]);
    Assert.Equal(@"D:\SteamLibrary", paths[1]);
});

Test("VDF: библиотека, которой принадлежит App ID 3132990", () =>
{
    var owner = SteamLocator.FindLibraryOwningApp(File.ReadAllText(Fixture("libraryfolders.vdf")), SteamLocator.AppId);
    Assert.Equal(@"D:\SteamLibrary", owner);
});

Test("VDF: старый формат \"1\" \"путь\"", () =>
{
    const string old = "\"LibraryFolders\"\n{\n\t\"TimeNextStatsReport\"\t\"1\"\n\t\"1\"\t\"E:\\\\Games\\\\Steam\"\n}";
    var paths = SteamLocator.ParseLibraryFolders(old).ToList();
    Assert.Equal(1, paths.Count);
    Assert.Equal(@"E:\Games\Steam", paths[0]);
});

Test("ACF: installdir и buildid, комментарии игнорируются", () =>
{
    var acf = VdfParser.Parse(File.ReadAllText(Fixture("appmanifest_3132990.acf")));
    Assert.Equal("Black Myth Wukong Benchmark Tool", acf["AppState"]!.GetString("installdir"));
    Assert.Equal("15117622", acf["AppState"]!.GetString("buildid"));
    Assert.Equal("russian", acf["AppState"]!["UserConfig"]!.GetString("language"));
});

Test("INI: без изменений файл сохраняется байт в байт (UTF-8 и UTF-16 LE)", () =>
{
    var utf8 = File.ReadAllBytes(Fixture("GameUserSettings.ini"));
    Assert.True(IniDocument.Parse(utf8).ToBytes().SequenceEqual(utf8), "UTF-8 изменился");

    var text = Encoding.UTF8.GetString(utf8).Replace("\n", "\r\n");
    var utf16 = new UnicodeEncoding(false, true).GetPreamble().Concat(Encoding.Unicode.GetBytes(text)).ToArray();
    var doc = IniDocument.Parse(utf16);
    Assert.Equal("utf-16", doc.Encoding.WebName);
    Assert.Equal("\r\n", doc.NewLine);
    Assert.True(doc.ToBytes().SequenceEqual(utf16), "UTF-16 изменился");
});

Test("INI: чтение и изменение ключа в секции, остальные строки не трогаются", () =>
{
    var doc = IniDocument.Load(Fixture("GameUserSettings.ini"));
    Assert.Equal("1440", doc.Get("/Script/GSGameSettings.GSGameUserSettings", "ResolutionSizeY"));
    doc.Set("ScalabilityGroups", "sg.ShadowQuality", "0");
    Assert.Equal("0", doc.Get("ScalabilityGroups", "sg.ShadowQuality"));
    Assert.Equal("2", doc.Get("ScalabilityGroups", "sg.FoliageQuality"));
    Assert.Equal("b1", doc.Get("ShaderPipelineCache.CacheFile", "LastOpened"));
});

Test("INI: новый ключ дописывается в свою секцию, новая секция создаётся", () =>
{
    var doc = IniDocument.Load(Fixture("GameUserSettings.ini"));
    doc.Set("ScalabilityGroups", "sg.RayTracingQuality", "2");
    doc.Set("NewSection", "Key", "Value");
    var text = doc.ToText();
    Assert.True(text.IndexOf("sg.RayTracingQuality=2") < text.IndexOf("[ShaderPipelineCache.CacheFile]"),
        "ключ должен оказаться внутри [ScalabilityGroups]");
    Assert.Equal("Value", doc.Get("NewSection", "Key"));
});

Test("INI: UISettingData – меняется только значение нужной пары", () =>
{
    var doc = IniDocument.Load(Fixture("GameUserSettings.ini"));
    Assert.Equal(22, doc.GetUiSettings().Count);
    Assert.True(doc.SetUiSetting("QualityLevel", "5"));
    Assert.True(doc.SetUiSetting("rtx", "1"), "регистр ключа не важен");
    var ui = doc.GetUiSettings();
    Assert.Equal("5", ui["QualityLevel"]);
    Assert.Equal("1", ui["Rtx"]);
    Assert.Equal("50", ui["Brightness"]);
    Assert.True(doc.ToText().Contains("(\"QualityLevel\", \"5\")"), "формат пары сохранён");
});

Test("INI: неизвестный ключ UISettingData не добавляется", () =>
{
    var doc = IniDocument.Load(Fixture("GameUserSettings.ini"));
    var before = doc.ToText();
    Assert.False(doc.SetUiSetting("RtxLevel", "2"));
    Assert.Equal(before, doc.ToText());
});

Test("INI: UISettingData с другими пробелами тоже разбирается", () =>
{
    var doc = IniDocument.ParseText("[A]\nUISettingData=((\"Vsync\",\"1\"), ( \"Rtx\" , \"0\" ))");
    Assert.True(doc.SetUiSetting("Vsync", "0"));
    Assert.True(doc.SetUiSetting("Rtx", "1"));
    Assert.Equal("0", doc.GetUiSetting("Vsync"));
    Assert.Equal("1", doc.GetUiSetting("Rtx"));
});

Test("Профиль CPU: минимум графики, 25% рендера, максимум дальности, всё «ускоряющее» выключено", () =>
{
    var doc = IniDocument.Load(Fixture("GameUserSettings.ini"));
    var height = BenchmarkProfile.ReadWindowHeight(doc)!.Value;
    Assert.Equal(1440, height);
    var applied = BenchmarkProfile.Cpu().ApplyTo(doc, height);

    var ui = doc.GetUiSettings();
    Assert.Equal("1", ui["QualityLevel"]);
    Assert.Equal("1", ui["ShadowQuality"]);
    Assert.Equal("5", ui["ViewDistance"]);
    Assert.Equal("360", ui["ImageQuality"]);
    Assert.Equal("0", ui["InsertFrame"]);
    Assert.Equal("0", ui["Vsync"]);
    Assert.Equal("0", ui["LockFrameRate"]);
    Assert.Equal("0", ui["Rtx"]);
    Assert.Equal("3", ui["SuperResolutionSampling"]);
    Assert.Equal("25", doc.Get("ScalabilityGroups", "sg.ResolutionQuality"));
    Assert.Equal("0", doc.Get("ScalabilityGroups", "sg.ShadowQuality"));
    Assert.Equal("4", doc.Get("ScalabilityGroups", "sg.ViewDistanceQuality"));
    Assert.Equal("False", doc.Get("/Script/GSGameSettings.GSGameUserSettings", "bUseVSync"));
    Assert.Equal("0.000000", doc.Get("/Script/GSGameSettings.GSGameUserSettings", "FrameRateLimit"));
    Assert.True(applied.All(a => a.Applied), "все ключи профиля есть в конфиге");
    Assert.Equal("1440", doc.Get("/Script/GSGameSettings.GSGameUserSettings", "ResolutionSizeY"));
});

Test("Профиль GPU: максимум качества, 100% рендера; с --gpu-rt включается RT", () =>
{
    var doc = IniDocument.Load(Fixture("GameUserSettings.ini"));
    var applied = BenchmarkProfile.Gpu(100, rayTracing: true).ApplyTo(doc, 1440);
    var ui = doc.GetUiSettings();
    Assert.Equal("5", ui["QualityLevel"]);
    Assert.Equal("5", ui["GlobalIllumination"]);
    Assert.Equal("1440", ui["ImageQuality"]);
    Assert.Equal("1", ui["Rtx"]);
    Assert.Equal("4", doc.Get("ScalabilityGroups", "sg.GlobalIlluminationQuality"));
    Assert.Equal("100", doc.Get("ScalabilityGroups", "sg.ResolutionQuality"));
    Assert.Equal("2", doc.Get("ScalabilityGroups", "sg.RayTracingQuality"));
    Assert.False(applied.Single(a => a.Key == "RtxLevel").Applied);
});

Test("Профили не наследуют изменения друг друга при сборке от исходника", () =>
{
    var original = File.ReadAllBytes(Fixture("GameUserSettings.ini"));
    var cpu = IniDocument.Parse(original);
    BenchmarkProfile.Cpu().ApplyTo(cpu, 1440);
    var gpu = IniDocument.Parse(original);
    BenchmarkProfile.Gpu().ApplyTo(gpu, 1440);
    Assert.Equal("5", gpu.GetUiSetting("QualityLevel"));
    Assert.Equal("1", cpu.GetUiSetting("QualityLevel"));
});

Test("Результат: основные поля, Records и статистика", () =>
{
    var r = ResultParser.TryParseFile(Fixture("cpu_result_sample"))!;
    Assert.NotNull(r);
    Assert.Equal(55.0, r.Fps95!.Value);
    Assert.Equal(200, r.Records.Count);
    Assert.Equal("NVIDIA GeForce RTX 4070", r.Field("GPUModel"));
    var s = r.Stats!;
    Assert.Equal(200, s.Samples);
    Assert.True(s.Low01PercentFps <= s.Low1PercentFps && s.Low1PercentFps < s.MedianFps && s.MedianFps <= r.FpsMax!.Value, "0.1% low ≤ 1% low < медиана ≤ max");
    Assert.True(s.CpuBoundShare > 95, $"в синтетике CPU-limited, получено {s.CpuBoundShare}");
    Assert.True(Math.Abs(s.MeanFps - r.FpsAvg!.Value) < 0.2, "среднее по Records ≈ FPSAvg");
});

Test("Результат: битый/чужой JSON не считается результатом", () =>
{
    var dir = TempDir();
    File.WriteAllText(Path.Combine(dir, "a"), "{\"FPSAvg\": 5");
    File.WriteAllText(Path.Combine(dir, "b"), "{\"Hello\": 1}");
    Assert.Null(ResultParser.TryParseFile(Path.Combine(dir, "a")));
    Assert.Null(ResultParser.TryParseFile(Path.Combine(dir, "b")));
});

Test("Перцентиль: линейная интерполяция", () =>
{
    double[] v = [10, 20, 30, 40, 50];
    Assert.Equal(30, FrameStatistics.Percentile(v, 50));
    Assert.Equal(10.4, Math.Round(FrameStatistics.Percentile(v, 1), 6));
    Assert.Equal(50, FrameStatistics.Percentile(v, 100));
});

Test("ResultWatcher: старые файлы игнорируются, новый берётся только после того, как дописан", () =>
{
    var dir = TempDir();
    var tool = Path.Combine(dir, "Tool");
    Directory.CreateDirectory(tool);
    File.Copy(Fixture("cpu_result_sample"), Path.Combine(tool, "1700000000"));

    var watcher = new ResultWatcher([dir]);
    watcher.Snapshot();
    Assert.Null(watcher.TryTakeNew());

    var fresh = Path.Combine(tool, "1760000000");
    File.Copy(Fixture("cpu_result_sample"), fresh);
    Assert.Null(watcher.TryTakeNew());
    var taken = watcher.TryTakeNew();
    Assert.NotNull(taken);
    Assert.Equal(fresh, taken!.Value.Path);
    Assert.Null(watcher.TryTakeNew());
});

Test("ConfigGuard: восстановление после прохода и после «убитого» запуска", () =>
{
    var dir = TempDir();
    var ini = Path.Combine(dir, "GameUserSettings.ini");
    File.Copy(Fixture("GameUserSettings.ini"), ini);
    var original = File.ReadAllBytes(ini);

    var guard = new ConfigGuard(ini);
    guard.Backup();
    File.WriteAllText(ini, "испорчено");
    guard.Restore();
    Assert.True(File.ReadAllBytes(ini).SequenceEqual(original), "восстановлено после прохода");
    Assert.False(File.Exists(guard.BackupPath), "копия удалена");

    new ConfigGuard(ini).Backup();
    File.WriteAllText(ini, "после краша");
    Assert.True(new ConfigGuard(ini).RecoverLeftover());
    Assert.True(File.ReadAllBytes(ini).SequenceEqual(original), "восстановлено при следующем запуске");
});

Test("Отчёт: консоль, Markdown и валидный JSON, вердикт CPU-теста", () =>
{
    var system = new SystemInfo();
    system.Add("CPU", "Процессор", "Test CPU");
    var result = ResultParser.TryParseFile(Fixture("cpu_result_sample"))!;
    var doc = IniDocument.Load(Fixture("GameUserSettings.ini"));
    var cpu = new PassReport { Profile = BenchmarkProfile.Cpu(), Result = result, WindowResolution = "2560x1440" };
    cpu.Applied = cpu.Profile.ApplyTo(doc, 1440);
    var gpu = new PassReport { Profile = BenchmarkProfile.Gpu(), Error = "таймаут" };

    var builder = new ReportBuilder(system, [cpu, gpu], "15117622");
    var console = builder.ToConsole();
    Assert.True(console.Contains("CPU-тест: в 100% замеров кадр ограничивал процессор"), console);
    Assert.True(console.Contains("ошибка"), "упавший проход отмечен");
    Assert.True(builder.ToMarkdown(DateTime.Now).Contains("| Средний FPS (бенчмарк) |"));
    using var json = JsonDocument.Parse(builder.ToJson(DateTime.Now));
    Assert.Equal(2, json.RootElement.GetProperty("passes").GetArrayLength());
});

Test("Трассировка лучей включается автоматически только на NVIDIA RTX", () =>
{
    Assert.True(BenchmarkProfile.SupportsRayTracing(["NVIDIA GeForce RTX 4070"]));
    Assert.True(BenchmarkProfile.SupportsRayTracing(["Intel(R) UHD Graphics 770", "NVIDIA GeForce RTX 3060 Laptop GPU"]));
    Assert.False(BenchmarkProfile.SupportsRayTracing(["NVIDIA GeForce GTX 1660 SUPER"]));
    Assert.False(BenchmarkProfile.SupportsRayTracing(["AMD Radeon RX 7900 XTX"]));
    Assert.False(BenchmarkProfile.SupportsRayTracing([]));
});

Test("Сверка ImageQuality: подходят и проценты, и высота в пикселях, расхождение – предупреждение", () =>
{
    PassReport Check(string imageQuality)
    {
        var json = $"{{\"FPSAvg\": 60, \"ScreenResolution\": \"2560x1440\", \"QualityLevel\": 1, \"ImageQuality\": {imageQuality}}}";
        var report = new PassReport { Profile = BenchmarkProfile.Cpu(25), Result = ResultParser.Parse(json) };
        App.CheckAppliedSettings(report);
        return report;
    }

    Assert.Equal(0, Check("25").Warnings.Count);
    Assert.Equal(0, Check("360").Warnings.Count);
    Assert.Equal(1, Check("100").Warnings.Count);
    Assert.Equal(1440, App.HeightOf("2560x1440"));
    Assert.Equal(1440, App.HeightOf("2560 × 1440"));
    Assert.Null(App.HeightOf("0"));
});

Test("Первичная настройка отмечается пройденной: игра не покажет экраны яркости, языка и соглашения", () =>
{
    var text = string.Join("\r\n",
        "[/Script/GSGameSettings.GSGameUserSettings]",
        "UISettingData=((\"MainDisplay\", \"0\"),(\"PrivacyAgreement\", \"0\"),(\"Min\", \"0\"))",
        "PrivacyAgreement=0",
        "AgreementReaded=0",
        "FirstSettingFinish=False",
        "ResolutionSizeX=2560");
    var ini = IniDocument.ParseText(text);

    Assert.True(BenchmarkProfile.MarkFirstRunDone(ini));
    Assert.Equal("1", ini.GetUiSetting("PrivacyAgreement"));
    Assert.Equal("1", ini.Get("/Script/GSGameSettings.GSGameUserSettings", "PrivacyAgreement"));
    Assert.Equal("1", ini.Get("/Script/GSGameSettings.GSGameUserSettings", "AgreementReaded"));
    Assert.Equal("True", ini.Get("/Script/GSGameSettings.GSGameUserSettings", "FirstSettingFinish"));
    Assert.False(BenchmarkProfile.MarkFirstRunDone(ini));
});

Test("Главное меню распознаётся по светлому тексту пункта «Выйти», тёмная заставка – нет", () =>
{
    Frame Make(bool menuText)
    {
        const int w = 400, h = 200;
        var pixels = new byte[w * h * 4];
        for (var i = 0; i < pixels.Length; i += 4) { pixels[i] = 20; pixels[i + 1] = 22; pixels[i + 2] = 25; }
        if (menuText)
        {
            for (var y = 120; y < 123; y++)
            for (var x = 19; x < 36; x++)
            {
                var i = (y * w + x) * 4;
                pixels[i] = pixels[i + 1] = pixels[i + 2] = 220;
            }
        }

        return new Frame(w, h, pixels);
    }

    Assert.True(ScreenProbe.IsMainMenu(Make(menuText: true)));
    Assert.False(ScreenProbe.IsMainMenu(Make(menuText: false)));
    Assert.Equal(0.0, Make(menuText: false).BrightShare(0, 0, 1, 1, 110));
    Assert.True(ScreenProbe.IsBlank(Make(menuText: false)));
    Assert.False(ScreenProbe.IsBlank(new Frame(4, 4, Enumerable.Repeat((byte)200, 4 * 4 * 4).ToArray())));
});

Test("Сверка пресета: «пользовательский» (6) в CPU-тесте не ошибка, если группы качества совпадают", () =>
{
    PassReport Check(string groups)
    {
        var json = "{\"FPSAvg\": 60, \"ScreenResolution\": \"2560 × 1440\", \"QualityLevel\": 6, \"ImageQuality\": 25, \"ViewDistance\": 5, " +
                   $"\"AntiAliasing\": {groups}, \"PostProcessing\": 1, \"ShadowQuality\": 1, \"TextureQuality\": 1, \"MaterialQuality\": 1, \"VegetationQuality\": 1}}";
        var report = new PassReport { Profile = BenchmarkProfile.Cpu(25), Result = ResultParser.Parse(json) };
        App.CheckAppliedSettings(report);
        return report;
    }

    Assert.Equal(0, Check("1").Warnings.Count);
    Assert.Equal(1, Check("3").Warnings.Count);
});

Test("Сверка трассировки: бенчмарк пишет в Rtx уровень трассировки, а не флаг", () =>
{
    PassReport Check(string rtx)
    {
        var json = "{\"FPSAvg\": 40, \"ScreenResolution\": \"2560 × 1440\", \"QualityLevel\": 5, \"ImageQuality\": 100, " +
                   $"\"ViewDistance\": 5, \"InsertFrame\": 0, \"Rtx\": {rtx}}}";
        var report = new PassReport { Profile = BenchmarkProfile.Gpu(100, rayTracing: true), Result = ResultParser.Parse(json) };
        App.CheckAppliedSettings(report);
        return report;
    }

    Assert.Equal(0, Check("2").Warnings.Count);
    Assert.Equal(1, Check("0").Warnings.Count);
});

Test("Параметры командной строки", () =>
{
    var o = Options.Parse(["run", "--only", "gpu", "--gpu-rt", "--gpu-scale", "150", "--start-button", "0.12,0.46", "--timeout", "20"]);
    Assert.False(o.RunCpu);
    Assert.True(o.RunGpu && o.GpuRayTracing == true);
    Assert.Null(Options.Parse([]).GpuRayTracing);
    Assert.Equal(false, Options.Parse(["--no-gpu-rt"]).GpuRayTracing);
    Assert.Equal(150, o.GpuRenderScale);
    Assert.Equal(0.12, o.StartButton.X);
    Assert.Equal(TimeSpan.FromMinutes(20), o.PassTimeout);
    Assert.Throws<ArgumentException>(() => Options.Parse(["--cpu-scale", "5"]));
    Assert.Throws<ArgumentException>(() => Options.Parse(["--unknown"]));
    Assert.Equal("help", Options.Parse(["--help"]).Command);
});

var failed = 0;
foreach (var (name, body) in tests)
{
    try
    {
        body();
        Console.WriteLine($"  PASS  {name}");
    }
    catch (Exception e)
    {
        failed++;
        Console.WriteLine($"  FAIL  {name}\n        {e.GetType().Name}: {e.Message}");
    }
}

Console.WriteLine($"\n{tests.Count - failed}/{tests.Count} тестов прошло.");
return failed == 0 ? 0 : 1;

internal static class Assert
{
    public static void True(bool condition, string message = "ожидалось true")
    {
        if (!condition) throw new Exception(message);
    }

    public static void False(bool condition, string message = "ожидалось false") => True(!condition, message);

    public static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception($"ожидалось «{expected}», получено «{actual}»");
    }

    public static void Null(object? value)
    {
        if (value is not null) throw new Exception($"ожидался null, получено {value}");
    }

    public static void NotNull(object? value)
    {
        if (value is null) throw new Exception("ожидалось не null");
    }

    public static void Throws<TException>(Action action) where TException : Exception
    {
        try { action(); }
        catch (TException) { return; }
        throw new Exception($"ожидалось исключение {typeof(TException).Name}");
    }
}
