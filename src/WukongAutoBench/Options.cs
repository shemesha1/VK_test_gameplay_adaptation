using System.Globalization;

namespace WukongAutoBench;

public enum LaunchMode { Steam, Direct }

public sealed class Options
{
    public string Command { get; set; } = "run";
    public string? InstallDir { get; set; }
    public string OutputDir { get; set; } = Path.Combine(AppContext.BaseDirectory, "results");
    public bool RunCpu { get; set; } = true;
    public bool RunGpu { get; set; } = true;
    public int CpuRenderScale { get; set; } = 25;
    public int GpuRenderScale { get; set; } = 100;
    public bool? GpuRayTracing { get; set; }
    public int? RenderHeight { get; set; }
    public LaunchMode Launch { get; set; } = LaunchMode.Steam;
    public TimeSpan PassTimeout { get; set; } = TimeSpan.FromMinutes(15);
    public bool KeepConfig { get; set; }
    public bool DryRun { get; set; }
    public (double X, double Y) StartButton { get; set; } = (0.11, 0.45);
    public (double X, double Y) ConfirmButton { get; set; } = (0.39, 0.585);
    public string? CpuResultFile { get; set; }
    public string? GpuResultFile { get; set; }

    public const string Usage = """
        WukongAutoBench – автоматический прогон Black Myth: Wukong Benchmark Tool (CPU- и GPU-тест).

        Команды:
          run       (по умолчанию) оба прохода подряд, затем отчёт
          inspect   показать найденные пути, текущие настройки и характеристики ПК, ничего не запуская
          report    собрать отчёт из уже сохранённых файлов результатов: --cpu <файл> --gpu <файл>

        Параметры:
          --install-dir <путь>     папка Benchmark Tool, если не нашлась через Steam
          --out <путь>             куда складывать отчёты (по умолчанию results рядом с exe)
          --only cpu|gpu           выполнить только один проход
          --cpu-scale <10..100>    масштаб рендеринга в CPU-тесте, % (по умолчанию 25)
          --gpu-scale <50..200>    масштаб рендеринга в GPU-тесте, % (по умолчанию 100)
          --gpu-rt                 включить трассировку лучей в GPU-тесте принудительно
          --no-gpu-rt              выключить трассировку лучей в GPU-тесте
                                   (по умолчанию включается сама, если видеокарта NVIDIA RTX)
          --render-height <px>     высота окна для расчёта рендера, если в конфиге её нет
          --launch steam|direct    запуск через Steam (по умолчанию) или напрямую exe
          --timeout <минуты>       таймаут одного прохода (по умолчанию 15)
          --start-button x,y       координаты кнопки «Тест быстродействия» в долях окна (0.11,0.45)
          --confirm-button x,y     координаты кнопки «Подтвердить» в долях окна (0.39,0.585)
          --keep-config            не возвращать исходные настройки после прогона
          --dry-run                ничего не запускать: показать, какие настройки будут записаны
          -h, --help               эта справка
        """;

    public static Options Parse(string[] args)
    {
        var o = new Options();
        var i = 0;
        if (args.Length > 0 && !args[0].StartsWith('-'))
        {
            o.Command = args[0].ToLowerInvariant();
            i = 1;
        }

        string Next(string name) =>
            ++i < args.Length ? args[i] : throw new ArgumentException($"После {name} нужно значение.");

        for (; i < args.Length; i++)
        {
            var a = args[i];
            switch (a.ToLowerInvariant())
            {
                case "-h" or "--help" or "/?": o.Command = "help"; break;
                case "--install-dir": o.InstallDir = Next(a); break;
                case "--out": o.OutputDir = Path.GetFullPath(Next(a)); break;
                case "--only":
                    var only = Next(a).ToLowerInvariant();
                    o.RunCpu = only == "cpu";
                    o.RunGpu = only == "gpu";
                    if (!o.RunCpu && !o.RunGpu) throw new ArgumentException("--only принимает cpu или gpu.");
                    break;
                case "--cpu-scale": o.CpuRenderScale = Int(Next(a), a, 10, 100); break;
                case "--gpu-scale": o.GpuRenderScale = Int(Next(a), a, 50, 200); break;
                case "--gpu-rt": o.GpuRayTracing = true; break;
                case "--no-gpu-rt": o.GpuRayTracing = false; break;
                case "--render-height": o.RenderHeight = Int(Next(a), a, 240, 8640); break;
                case "--launch":
                    o.Launch = Next(a).ToLowerInvariant() switch
                    {
                        "steam" => LaunchMode.Steam,
                        "direct" => LaunchMode.Direct,
                        _ => throw new ArgumentException("--launch принимает steam или direct."),
                    };
                    break;
                case "--timeout": o.PassTimeout = TimeSpan.FromMinutes(Int(Next(a), a, 3, 120)); break;
                case "--start-button": o.StartButton = Point(Next(a), a); break;
                case "--confirm-button": o.ConfirmButton = Point(Next(a), a); break;
                case "--keep-config": o.KeepConfig = true; break;
                case "--dry-run": o.DryRun = true; break;
                case "--cpu": o.CpuResultFile = Next(a); break;
                case "--gpu": o.GpuResultFile = Next(a); break;
                default: throw new ArgumentException($"Неизвестный параметр: {a}. Справка: --help");
            }
        }

        return o;
    }

    private static int Int(string raw, string name, int min, int max)
    {
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) || v < min || v > max)
            throw new ArgumentException($"{name}: ожидается целое число от {min} до {max}, получено «{raw}».");
        return v;
    }

    private static (double, double) Point(string raw, string name)
    {
        var parts = raw.Split(',', ';');
        if (parts.Length == 2
            && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y)
            && x is > 0 and < 1 && y is > 0 and < 1)
            return (x, y);
        throw new ArgumentException($"{name}: ожидается пара долей окна, например 0.11,0.45");
    }
}
