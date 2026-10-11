using WukongAutoBench.Util;

namespace WukongAutoBench.Config;

public sealed class ConfigGuard
{
    private readonly string _configPath;
    private readonly string _backupPath;
    private byte[]? _original;

    public ConfigGuard(string configPath)
    {
        _configPath = configPath;
        _backupPath = configPath + ".wukongautobench.bak";
    }

    public string BackupPath => _backupPath;

    /// <summary>Если с прошлого раза осталась копия – прошлый запуск не завершился штатно, возвращаем оригинал.</summary>
    public bool RecoverLeftover()
    {
        if (!File.Exists(_backupPath)) return false;
        Log.Warn("Найдена резервная копия конфига от незавершённого прошлого запуска – восстанавливаю исходные настройки.");
        _original = File.ReadAllBytes(_backupPath);
        Restore();
        return true;
    }

    public void Backup()
    {
        if (!File.Exists(_configPath))
            throw new FileNotFoundException(
                $"Не найден конфиг бенчмарка: {_configPath}. Запустите Benchmark Tool вручную один раз – " +
                "при первом старте он создаёт конфиг и компилирует шейдеры.");

        _original = File.ReadAllBytes(_configPath);
        File.WriteAllBytes(_backupPath, _original);
        Log.Info($"Резервная копия настроек: {_backupPath}");
    }

    public void Restore()
    {
        var original = _original;
        if (original is null) return;

        for (var attempt = 1; attempt <= 10; attempt++)
        {
            try
            {
                File.WriteAllBytes(_configPath, original);
                if (File.ReadAllBytes(_configPath).AsSpan().SequenceEqual(original))
                {
                    File.Delete(_backupPath);
                    Log.Ok("Исходные настройки бенчмарка восстановлены.");
                    _original = null;
                    return;
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }

            Thread.Sleep(500);
        }

        Log.Error($"Не удалось восстановить настройки автоматически. Оригинал сохранён в {_backupPath} – " +
                  "его можно скопировать поверх GameUserSettings.ini вручную.");
    }
}
