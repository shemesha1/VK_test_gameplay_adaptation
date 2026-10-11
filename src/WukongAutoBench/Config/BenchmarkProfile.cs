using System.Globalization;

namespace WukongAutoBench.Config;

public enum PassKind { Cpu, Gpu }

public sealed record AppliedSetting(string Title, string Location, string Key, string? Before, string After, bool Applied);

public sealed class BenchmarkProfile
{
    public const string ScalabilitySection = "ScalabilityGroups";
    public const string DefaultMainSection = "/Script/GSGameSettings.GSGameUserSettings";

    /// <summary>
    /// Уровень трассировки лучей в GPU-тесте. В файле результата бенчмарк пишет в поле Rtx именно
    /// уровень (0 – выключена), а не флаг «включена».
    /// </summary>
    public const string RayTracingLevel = "2";

    public required PassKind Kind { get; init; }
    public required string Title { get; init; }

    public required int RenderScalePercent { get; init; }

    public required int Quality { get; init; }

    public required int ViewDistance { get; init; }

    public required bool RayTracing { get; init; }

    public required IReadOnlyList<string> Rationale { get; init; }

    private static readonly (string Key, string Title)[] UiQualityKeys =
    [
        ("QualityLevel", "Общий пресет качества"),
        ("AntiAliasing", "Сглаживание"),
        ("PostProcessing", "Постобработка"),
        ("ShadowQuality", "Тени"),
        ("TextureQuality", "Текстуры"),
        ("FxQuality", "Эффекты"),
        ("MaterialQuality", "Материалы"),
        ("VegetationQuality", "Растительность"),
        ("GlobalIllumination", "Глобальное освещение"),
        ("ReflectionQuality", "Отражения"),
    ];

    private static readonly string[] SgQualityKeys =
    [
        "sg.AntiAliasingQuality", "sg.PostProcessQuality", "sg.ShadowQuality", "sg.TextureQuality",
        "sg.EffectsQuality", "sg.ShadingQuality", "sg.FoliageQuality", "sg.GlobalIlluminationQuality",
        "sg.ReflectionQuality",
    ];

    public static BenchmarkProfile Cpu(int renderScale = 25) => new()
    {
        Kind = PassKind.Cpu,
        Title = "CPU-тест",
        RenderScalePercent = renderScale,
        Quality = 1,
        ViewDistance = 5,
        RayTracing = false,
        Rationale =
        [
            $"Масштаб рендеринга {renderScale}%: видеокарта рисует в ~{Math.Round(10000.0 / (renderScale * renderScale)):0} раз меньше пикселей, " +
            "время кадра на GPU падает до единиц миллисекунд и перестаёт ограничивать FPS.",
            "Качество теней, освещения, отражений, постобработки, эффектов, растительности, текстур – минимум: это нагрузка, " +
            "которая почти целиком ложится на GPU.",
            "Дальность прорисовки – максимум: она увеличивает число объектов в сцене, а значит отсечение, анимацию " +
            "и подготовку draw calls на CPU. Это как раз то, что должен мерить CPU-тест.",
            "Трассировка лучей, генерация кадров, VSync и ограничение FPS выключены: генерация кадров дорисовывает кадры " +
            "без участия процессора, а VSync/лимит упирают FPS в частоту монитора вместо скорости CPU.",
            "Разрешение окна не меняется (берётся текущее): нужный эффект даёт масштаб рендеринга, а подменять разрешение, " +
            "которого может не быть у монитора проверяющего, рискованно.",
        ],
    };

    public static BenchmarkProfile Gpu(int renderScale = 100, bool rayTracing = false) => new()
    {
        Kind = PassKind.Gpu,
        Title = "GPU-тест",
        RenderScalePercent = renderScale,
        Quality = 5,
        ViewDistance = 5,
        RayTracing = rayTracing,
        Rationale =
        [
            $"Масштаб рендеринга {renderScale}% – каждый пиксель окна считается без понижения внутреннего разрешения.",
            "Все группы качества на максимуме (5 – «кинематографическое»): тени, глобальное освещение, отражения, " +
            "растительность и постобработка – самые тяжёлые для GPU части кадра.",
            "Генерация кадров выключена: она добавляет кадры, которые GPU не рендерил полностью, и завышает FPS.",
            "VSync и ограничение FPS выключены, иначе видеокарта простаивает, дожидаясь обновления экрана.",
            rayTracing
                ? "Трассировка лучей включена: это самая тяжёлая для видеокарты часть кадра. По умолчанию включается " +
                  "автоматически на видеокартах NVIDIA RTX, выключить можно флагом --no-gpu-rt."
                : "Трассировка лучей выключена: видеокарта не из линейки NVIDIA RTX (или задан --no-gpu-rt). " +
                  "Без аппаратной поддержки RT бенчмарк может упасть на старте, а на других картах поведение " +
                  "полной трассировки не проверено. Включить принудительно можно флагом --gpu-rt.",
        ],
    };

    /// <summary>
    /// Можно ли включать трассировку лучей автоматически: true, если среди видеокарт есть NVIDIA RTX.
    /// Видеокарты AMD и Intel тоже поддерживают RT, но полная трассировка в Black Myth: Wukong сделана
    /// в первую очередь под NVIDIA, и на других картах её поведение не проверено, поэтому для них
    /// RT включается только вручную (--gpu-rt).
    /// </summary>
    public static bool SupportsRayTracing(IEnumerable<string> gpuNames) =>
        gpuNames.Any(name => name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)
                             && name.Contains("RTX", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Применяет профиль к документу и возвращает список изменений.
    /// <paramref name="windowHeight"/> нужен, чтобы перевести масштаб рендеринга в высоту кадра:
    /// в UISettingData поле ImageQuality хранит именно высоту внутреннего рендера в пикселях.
    /// </summary>
    public IReadOnlyList<AppliedSetting> ApplyTo(IniDocument ini, int windowHeight)
    {
        var changes = new List<AppliedSetting>();
        var q = Quality.ToString(CultureInfo.InvariantCulture);
        var sg = (Quality - 1).ToString(CultureInfo.InvariantCulture);
        var mainSection = ini.FindSectionContaining("ResolutionSizeX") ?? DefaultMainSection;

        void Ui(string title, string key, string value)
        {
            var before = ini.GetUiSetting(key);
            var ok = ini.SetUiSetting(key, value);
            changes.Add(new AppliedSetting(title, "UISettingData", key, before, value, ok));
        }

        void Ini(string title, string section, string key, string value)
        {
            var before = ini.Get(section, key);
            ini.Set(section, key, value);
            changes.Add(new AppliedSetting(title, section == ScalabilitySection ? "ScalabilityGroups" : "Main", key, before, value, true));
        }

        var renderHeight = Math.Max(1, (int)Math.Round(windowHeight * RenderScalePercent / 100.0));

        foreach (var (key, title) in UiQualityKeys) Ui(title, key, q);
        Ui("Дальность прорисовки", "ViewDistance", ViewDistance.ToString(CultureInfo.InvariantCulture));
        Ui("Высота внутреннего рендера, px", "ImageQuality", renderHeight.ToString(CultureInfo.InvariantCulture));
        Ui("Трассировка лучей", "Rtx", RayTracing ? "1" : "0");
        if (RayTracing) Ui("Уровень трассировки", "RtxLevel", RayTracingLevel);
        Ui("Генерация кадров", "InsertFrame", "0");
        Ui("VSync", "Vsync", "0");
        Ui("Ограничение FPS", "LockFrameRate", "0");
        Ui("Размытие в движении", "MotionBlur", "0");

        Ini("Масштаб рендеринга, %", ScalabilitySection, "sg.ResolutionQuality",
            RenderScalePercent.ToString(CultureInfo.InvariantCulture));
        Ini("Дальность прорисовки (UE)", ScalabilitySection, "sg.ViewDistanceQuality",
            (ViewDistance - 1).ToString(CultureInfo.InvariantCulture));
        foreach (var key in SgQualityKeys) Ini(key, ScalabilitySection, key, sg);
        if (RayTracing) Ini("Качество трассировки (UE)", ScalabilitySection, "sg.RayTracingQuality", "2");

        Ini("VSync (UE)", mainSection, "bUseVSync", "False");
        Ini("Лимит FPS (UE)", mainSection, "FrameRateLimit", "0.000000");

        return changes;
    }

    /// <summary>
    /// Отмечает в конфиге, что первичная настройка игры пройдена: яркость и язык выбраны,
    /// соглашение о конфиденциальности принято. Без этих флагов игра при каждом запуске показывает
    /// экраны первого запуска вместо главного меню, и клики по кнопкам меню бенчмарка не попадают.
    /// Возвращает true, если какой-то флаг пришлось поменять.
    /// </summary>
    public static bool MarkFirstRunDone(IniDocument ini)
    {
        var section = ini.FindSectionContaining("FirstSettingFinish") ?? ini.FindSectionContaining("ResolutionSizeX") ?? DefaultMainSection;
        var changed = false;

        void Flag(string key, string value)
        {
            if (string.Equals(ini.Get(section, key), value, StringComparison.OrdinalIgnoreCase)) return;
            ini.Set(section, key, value);
            changed = true;
        }

        if (ini.GetUiSetting("PrivacyAgreement") is { } ui && ui != "1")
            changed |= ini.SetUiSetting("PrivacyAgreement", "1");
        Flag("PrivacyAgreement", "1");
        Flag("AgreementReaded", "1");
        Flag("FirstSettingFinish", "True");
        return changed;
    }

    /// <summary>Высота окна из конфига (ResolutionSizeY), если её удалось прочитать.</summary>
    public static int? ReadWindowHeight(IniDocument ini)
    {
        var section = ini.FindSectionContaining("ResolutionSizeY");
        var raw = section is null ? null : ini.Get(section, "ResolutionSizeY");
        return int.TryParse(raw?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var h) && h > 0 ? h : null;
    }

    public static string? ReadWindowResolution(IniDocument ini)
    {
        var section = ini.FindSectionContaining("ResolutionSizeX");
        if (section is null) return null;
        var x = ini.Get(section, "ResolutionSizeX")?.Trim();
        var y = ini.Get(section, "ResolutionSizeY")?.Trim();
        return x is null || y is null ? null : $"{x}x{y}";
    }
}
