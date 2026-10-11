using System.Diagnostics;
using System.Runtime.Versioning;
using WukongAutoBench.Config;
using WukongAutoBench.Results;
using WukongAutoBench.Steam;
using WukongAutoBench.Util;

namespace WukongAutoBench.Running;

public sealed record PassOutcome(string RawFile, BenchmarkResult Result, LoadSummary Load, TimeSpan Duration);

[SupportedOSPlatform("windows")]
public sealed class BenchmarkRunner
{
    private static readonly string[] ProcessNames = ["b1-Win64-Shipping", "b1_benchmark", "b1"];
    private static readonly TimeSpan LaunchTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan MenuWait = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan MenuPoll = TimeSpan.FromSeconds(2);
    private static readonly (double X, double Y) NeutralPoint = (0.75, 0.2);
    private static readonly TimeSpan StepPause = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan RelaunchPause = TimeSpan.FromSeconds(10);
    private const int MaxSnapshots = 80;
    private static readonly TimeSpan SceneLoad = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan RunWait = TimeSpan.FromMinutes(6);
    private static readonly TimeSpan FirstLaunchTimeout = TimeSpan.FromMinutes(5);
    private const int MaxLaunchAttempts = 3;

    private readonly BenchmarkInstallation _install;
    private readonly ResultWatcher _watcher;
    private readonly Options _options;
    private int _snapshots;

    /// <summary>Папка для снимков экрана на ключевых шагах прохода меню; null – не сохранять.</summary>
    public string? SnapshotDir { get; set; }

    public BenchmarkRunner(BenchmarkInstallation install, ResultWatcher watcher, Options options)
    {
        _install = install;
        _watcher = watcher;
        _options = options;
    }

    /// <summary>
    /// Один проход: запускает бенчмарк, проходит меню, ждёт файл результата и закрывает игру.
    /// Если игра закрылась без результата, перезапускает её (до трёх попыток в пределах таймаута).
    /// <paramref name="writeConfig"/> заново записывает настройки прохода перед повторной попыткой:
    /// при штатном выходе игра перезаписывает GameUserSettings.ini своими значениями.
    /// </summary>
    public PassOutcome Run(string passTitle, Action writeConfig)
    {
        KillBenchmark(reason: "перед запуском");
        _watcher.Snapshot();

        var started = Stopwatch.StartNew();
        using var sampler = new LoadSampler();
        sampler.Start();

        try
        {
            for (var attempt = 1; attempt <= MaxLaunchAttempts; attempt++)
            {
                if (attempt > 1)
                {
                    writeConfig();
                    Log.Info($"{passTitle}: настройки прохода записаны заново.");
                }

                Log.Step($"{passTitle}: запуск бенчмарка ({(_options.Launch == LaunchMode.Steam ? "через Steam" : "напрямую")}), попытка {attempt}/{MaxLaunchAttempts}");
                Launch(_options.Launch);

                var outcome = DriveUntilResult(passTitle, started, sampler);
                if (outcome is not null)
                {
                    Log.Ok($"{passTitle}: результат получен за {started.Elapsed:mm\\:ss} – {outcome.Value.Path}");
                    var load = sampler.Stop();
                    return new PassOutcome(outcome.Value.Path, outcome.Value.Result, load, started.Elapsed);
                }

                if (started.Elapsed > _options.PassTimeout) break;
                Log.Warn($"{passTitle}: процесс бенчмарка завершился без результата – перезапускаю.");
                KillBenchmark(reason: "перед перезапуском");
                Log.Info($"Даю Steam {RelaunchPause.TotalSeconds:0} с заметить, что игра закрылась (иначе он отвечает «Игра уже запущена»).");
                Thread.Sleep(RelaunchPause);
            }

            throw new TimeoutException(started.Elapsed > _options.PassTimeout
                ? $"{passTitle}: бенчмарк не выдал результат за {_options.PassTimeout.TotalMinutes:0} мин. " +
                  "Проверьте, что Steam запущен и вы вошли в аккаунт, экран не заблокирован, а окно бенчмарка не перекрыто. " +
                  "Если меню бенчмарка выглядит иначе – поправьте --start-button / --confirm-button."
                : $"{passTitle}: бенчмарк не выдал результат за {MaxLaunchAttempts} запуска – игра каждый раз закрывалась раньше. " +
                  "Код выхода 3 обычно означает сбой драйвера видеокарты (в системном журнале Windows – событие nvlddmkm 153 " +
                  "или LiveKernelEvent 141): обновите драйвер и проверьте целостность файлов бенчмарка в Steam.");
        }
        finally
        {
            KillBenchmark(reason: "после прохода");
        }
    }

    /// <summary>
    /// Первый запуск на этом ПК: файла настроек ещё нет, его создаёт сама игра при старте.
    /// Запускает бенчмарк, ждёт (до 5 минут), пока в GameUserSettings.ini появится строка UISettingData,
    /// и закрывает игру штатно, как крестиком окна: при штатном выходе игра сохраняет настройки.
    /// Возвращает путь к конфигу или null, если он так и не появился.
    /// </summary>
    public string? FirstLaunch(Func<string?> findConfig)
    {
        KillBenchmark(reason: "перед первым запуском");
        Log.Step("Файла настроек бенчмарка ещё нет – запускаю бенчмарк один раз, чтобы он его создал. " +
                 "При первом старте игра компилирует шейдеры, это может занять несколько минут.");
        Launch(_options.Launch);

        var watch = Stopwatch.StartNew();
        Process? game = null;
        while (watch.Elapsed < FirstLaunchTimeout)
        {
            if (IsConfigReady(findConfig())) break;
            game ??= FindProcess("b1-Win64-Shipping");
            Thread.Sleep(2000);
        }

        CloseGracefully(game ?? FindProcess("b1-Win64-Shipping"));
        var config = findConfig();
        if (config is not null) Log.Ok($"Бенчмарк создал файл настроек: {config}");
        return config;
    }

    /// <summary>Конфиг готов, если файл есть и в нём уже записаны настройки меню (UISettingData).</summary>
    private static bool IsConfigReady(string? path)
    {
        if (path is null) return false;
        try
        {
            return IniDocument.Load(path).HasUiSettings;
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// Просит окно игры закрыться (сообщение WM_CLOSE – то же, что нажатие крестика) и ждёт выхода до минуты.
    /// Если игра не закрылась, закрывает её принудительно.
    /// </summary>
    private static void CloseGracefully(Process? game)
    {
        if (game is not null && !game.HasExited)
        {
            var window = WindowInput.FindMainWindow(game.Id);
            if (window != IntPtr.Zero) Win32.PostMessage(window, Win32.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            if (!game.WaitForExit(60000)) Log.Warn("Бенчмарк не закрылся сам за минуту – закрываю принудительно.");
        }

        KillBenchmark(reason: "после первого запуска");
    }

    private void Launch(LaunchMode mode)
    {
        if (mode == LaunchMode.Steam && _install.SteamRoot is not null)
        {
            var steamExe = Path.Combine(_install.SteamRoot, "steam.exe");
            if (File.Exists(steamExe))
            {
                Process.Start(new ProcessStartInfo(steamExe, $"-applaunch {SteamLocator.AppId}") { UseShellExecute = false })?.Dispose();
                return;
            }

            Process.Start(new ProcessStartInfo($"steam://rungameid/{SteamLocator.AppId}") { UseShellExecute = true })?.Dispose();
            return;
        }

        var exe = _install.LauncherExe ?? _install.ShippingExe;
        Process.Start(new ProcessStartInfo(exe)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
        })?.Dispose();
    }

    /// <summary>
    /// Ждёт окно, проходит меню и ждёт результат. null – если процесс игры умер без результата.
    /// У Benchmark Tool нет ни ключей командной строки, ни API для старта теста, поэтому меню проходится
    /// эмуляцией ввода. Инструмент смотрит на экран: проходит заставку «Нажмите любую кнопку», пока
    /// не распознает главное меню, затем кликает «Тест быстродействия» → «Подтвердить» и проверяет,
    /// что меню исчезло. После этого до 6 минут ничего не нажимает, чтобы не мешать замеру,
    /// и проходит меню заново, только если результат так и не появился.
    /// </summary>
    private (string Path, BenchmarkResult Result)? DriveUntilResult(string passTitle, Stopwatch total, LoadSampler sampler)
    {
        var launchWatch = Stopwatch.StartNew();
        Process? game = null;
        var window = IntPtr.Zero;
        while (launchWatch.Elapsed < LaunchTimeout)
        {
            if (_watcher.TryTakeNew() is { } early) return early;
            if (game is null && FindProcess("b1-Win64-Shipping") is { } found)
            {
                game = found;
                try { _ = game.Handle; }
                catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }

            if (game is not null)
            {
                game.Refresh();
                if (game.HasExited) return Exited(game);
                window = WindowInput.FindMainWindow(game.Id);
                if (window != IntPtr.Zero) break;
            }

            Thread.Sleep(1000);
        }

        if (window == IntPtr.Zero)
        {
            Log.Warn($"{passTitle}: окно бенчмарка не появилось за {LaunchTimeout.TotalMinutes:0} мин.");
            return null;
        }

        var (w, h) = WindowInput.GetClientSize(window);
        Log.Info($"Окно бенчмарка найдено (PID {game!.Id}, {w}x{h}).");
        Log.Warn("Не трогайте мышь и клавиатуру до конца прохода – инструмент сам нажимает кнопки меню.");

        var attempt = 0;
        while (total.Elapsed < _options.PassTimeout)
        {
            game.Refresh();
            if (game.HasExited) return Exited(game);

            if (!Win32.IsWindow(window)) window = WindowInput.FindMainWindow(game.Id);
            if (window == IntPtr.Zero || !WindowInput.Activate(window))
            {
                Log.Warn("Не удаётся вывести окно бенчмарка на передний план – жду и пробую снова.");
                if (WaitFor(StepPause, game) is { } rw) return rw;
                continue;
            }

            attempt++;
            Log.Info($"Жду главное меню (попытка {attempt}): пока идёт загрузка или заставка «Нажмите любую кнопку», нажимаю её сам…");
            var menu = WaitForMainMenu(window, game, out var early);
            if (early is not null) return early;
            game.Refresh();
            if (game.HasExited) continue;

            Snapshot(window, $"{attempt}-menu");
            Log.Info(menu
                ? "Главное меню на экране: «Тест быстродействия» → «Подтвердить»"
                : $"Главное меню не распознано за {MenuWait.TotalSeconds:0} с – нажимаю кнопки по координатам: «Тест быстродействия» → «Подтвердить»");

            if (!SafeStep(window, () => WindowInput.ClickRelative(window, _options.StartButton.X, _options.StartButton.Y))) continue;
            if (WaitFor(StepPause, game) is { } r1) return r1;
            Snapshot(window, $"{attempt}-dialog");
            if (!SafeStep(window, () => WindowInput.ClickRelative(window, _options.ConfirmButton.X, _options.ConfirmButton.Y))) continue;
            if (WaitFor(StepPause, game) is { } r2) return r2;

            if (menu && StillOnMainMenu(window, game))
            {
                Snapshot(window, $"{attempt}-still-menu");
                Log.Warn("Главное меню всё ещё на экране – тест не запустился, нажимаю кнопки ещё раз.");
                continue;
            }

            if (WaitFor(SceneLoad, game) is { } r3) return r3;
            sampler.Recording = true;

            var left = _options.PassTimeout - total.Elapsed;
            var runWait = left < RunWait ? left : RunWait;
            Log.Info($"Тест запущен – жду результат до {runWait.TotalMinutes:0} мин, ничего не нажимая.");
            if (WaitFor(runWait, game) is { } r4) return r4;

            game.Refresh();
            if (!game.HasExited && total.Elapsed < _options.PassTimeout)
                Log.Warn("Результат так и не появился – похоже, клики не попали в кнопки меню. Пробую пройти меню ещё раз.");
        }

        return null;
    }

    /// <summary>
    /// Ждёт главное меню, проходя заставку «Нажмите любую кнопку»: пока меню не видно, кликает в пустую
    /// область окна, а если клики заставку не убирают – нажимает Enter. Пока экран чёрный (идёт загрузка),
    /// ничего не нажимает. Как только меню распознано на двух кадрах подряд, больше ничего не нажимает.
    /// false – меню так и не распознано (другой язык, разрешение или оформление) или игра закрылась.
    /// </summary>
    private bool WaitForMainMenu(IntPtr window, Process game, out (string, BenchmarkResult)? early)
    {
        early = null;
        var watch = Stopwatch.StartNew();
        var seen = 0;
        var presses = 0;
        while (watch.Elapsed < MenuWait)
        {
            if (_watcher.TryTakeNew() is { } result)
            {
                early = result;
                return false;
            }

            game.Refresh();
            if (game.HasExited) return false;

            if (WindowInput.Activate(window))
            {
                var frame = ScreenProbe.Capture(window);
                if (frame is not null && ScreenProbe.IsMainMenu(frame))
                {
                    if (++seen >= 2) return true;
                }
                else if (frame is null || ScreenProbe.IsBlank(frame))
                {
                    seen = 0;
                }
                else
                {
                    seen = 0;
                    presses++;
                    var enter = presses % 3 == 0;
                    Snapshot(window, enter ? "wait-enter" : "wait-click");
                    if (enter) WindowInput.PressEnter();
                    else WindowInput.ClickRelative(window, NeutralPoint.X, NeutralPoint.Y);
                }
            }

            Thread.Sleep(MenuPoll);
        }

        return false;
    }

    /// <summary>Меню видно и через секунду, и через несколько секунд после «Подтвердить» – значит, тест не начался.</summary>
    private static bool StillOnMainMenu(IntPtr window, Process game)
    {
        for (var check = 0; check < 2; check++)
        {
            game.Refresh();
            if (game.HasExited) return false;
            if (ScreenProbe.Capture(window) is not { } frame || !ScreenProbe.IsMainMenu(frame)) return false;
            Thread.Sleep(StepPause);
        }

        return true;
    }

    /// <summary>Записывает в лог код выхода игры (0 – штатный выход через меню, иное – падение) и забирает результат, если он успел появиться.</summary>
    private (string, BenchmarkResult)? Exited(Process game)
    {
        string code;
        try { code = game.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture); }
        catch (InvalidOperationException) { code = "неизвестен"; }
        Log.Info($"Процесс игры завершился, код выхода: {code}.");
        return _watcher.TryTakeNew();
    }

    /// <summary>Сохраняет уменьшенный снимок окна в папку отчёта: по нему видно, на каком экране застрял проход.</summary>
    private void Snapshot(IntPtr window, string label)
    {
        if (SnapshotDir is null || _snapshots >= MaxSnapshots) return;
        try
        {
            if (ScreenProbe.Capture(window) is not { } frame) return;
            Directory.CreateDirectory(SnapshotDir);
            frame.SaveBmp(Path.Combine(SnapshotDir, $"{DateTime.Now:HHmmss}_{++_snapshots:00}_{label}.bmp"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Шаг ввода выполняется, только если результата ещё нет и окно игры в фокусе.</summary>
    private bool SafeStep(IntPtr window, Action action)
    {
        if (!WindowInput.IsForeground(window)) return false;
        action();
        return true;
    }

    /// <summary>Пауза с опросом результата раз в секунду.</summary>
    private (string, BenchmarkResult)? WaitFor(TimeSpan duration, Process game)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < duration)
        {
            if (_watcher.TryTakeNew() is { } result) return result;
            game.Refresh();
            if (game.HasExited) break;
            Thread.Sleep(1000);
        }

        return _watcher.TryTakeNew();
    }

    private static Process? FindProcess(string name) => Process.GetProcessesByName(name).FirstOrDefault();

    /// <summary>
    /// Принудительно закрывает все процессы бенчмарка. Штатный выход не используется: при нём игра
    /// перезаписывает GameUserSettings.ini и могла бы затереть восстановленные настройки пользователя.
    /// </summary>
    public static void KillBenchmark(string reason)
    {
        var processes = ProcessNames.SelectMany(Process.GetProcessesByName).ToList();
        if (processes.Count == 0) return;

        Log.Info($"Закрываю процессы бенчмарка ({reason}): {string.Join(", ", processes.Select(p => $"{p.ProcessName}#{p.Id}"))}");
        foreach (var p in processes)
        {
            try
            {
                p.Kill(entireProcessTree: true);
                p.WaitForExit(15000);
            }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
            finally
            {
                p.Dispose();
            }
        }

        Thread.Sleep(2000);
    }
}
