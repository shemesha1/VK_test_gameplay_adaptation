using Microsoft.Win32;
using WukongAutoBench.Util;

namespace WukongAutoBench.Steam;

public sealed record BenchmarkInstallation(
    string? SteamRoot,
    string InstallDir,
    string ShippingExe,
    string? LauncherExe,
    string? BuildId);

public static class SteamLocator
{
    public const int AppId = 3132990;
    public const string DefaultFolderName = "Black Myth Wukong Benchmark Tool";

    public static string? FindSteamRoot()
    {
        var candidates = new List<string?>();

        if (OperatingSystem.IsWindows())
        {
            candidates.Add(ReadRegistry(RegistryHive.CurrentUser, @"Software\Valve\Steam", "SteamPath"));
            candidates.Add(ReadRegistry(RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"));
            candidates.Add(ReadRegistry(RegistryHive.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath"));
            candidates.Add(@"C:\Program Files (x86)\Steam");
            candidates.Add(@"C:\Program Files\Steam");
        }

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            var normalized = Path.GetFullPath(candidate.Replace('/', Path.DirectorySeparatorChar));
            if (Directory.Exists(Path.Combine(normalized, "steamapps"))) return normalized;
        }

        return null;
    }

    /// <summary>Все библиотеки Steam из libraryfolders.vdf (плюс сам корень Steam).</summary>
    public static IReadOnlyList<string> GetLibraries(string steamRoot)
    {
        var result = new List<string> { steamRoot };
        var vdfPath = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdfPath)) return result;

        foreach (var path in ParseLibraryFolders(File.ReadAllText(vdfPath)))
        {
            if (!result.Contains(path, StringComparer.OrdinalIgnoreCase)) result.Add(path);
        }

        return result;
    }

    /// <summary>
    /// Пути библиотек. Поддерживает и новый формат ("0" { "path" "D:\\Steam" }),
    /// и старый ("1" "D:\\SteamLibrary").
    /// </summary>
    internal static IEnumerable<string> ParseLibraryFolders(string vdfText)
    {
        var root = VdfParser.Parse(vdfText);
        var folders = root["libraryfolders"] ?? root["LibraryFolders"] ?? root;
        foreach (var (key, node) in folders.Children)
        {
            if (!int.TryParse(key, out _)) continue;
            var path = node.Value ?? node.GetString("path");
            if (!string.IsNullOrWhiteSpace(path)) yield return path;
        }
    }

    /// <summary>Библиотека, в которой по libraryfolders.vdf числится наше приложение.</summary>
    internal static string? FindLibraryOwningApp(string vdfText, int appId)
    {
        var root = VdfParser.Parse(vdfText);
        var folders = root["libraryfolders"] ?? root;
        foreach (var (_, node) in folders.Children)
        {
            if (node["apps"]?.Children.ContainsKey(appId.ToString()) == true) return node.GetString("path");
        }

        return null;
    }

    public static BenchmarkInstallation Locate(string? explicitInstallDir)
    {
        var steamRoot = FindSteamRoot();

        if (!string.IsNullOrWhiteSpace(explicitInstallDir))
        {
            return FromInstallDir(steamRoot, Path.GetFullPath(explicitInstallDir), buildId: null)
                   ?? throw new DirectoryNotFoundException(
                       $"В папке «{explicitInstallDir}» не найден b1-Win64-Shipping.exe. Укажите корень установки Benchmark Tool.");
        }

        if (steamRoot is null)
            throw new DirectoryNotFoundException(
                "Steam не найден (реестр HKCU\\Software\\Valve\\Steam). Установите Steam или укажите путь: --install-dir \"<папка бенчмарка>\".");

        Log.Info($"Steam: {steamRoot}");

        var libraries = GetLibraries(steamRoot).ToList();
        var vdfPath = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        if (File.Exists(vdfPath))
        {
            var owner = FindLibraryOwningApp(File.ReadAllText(vdfPath), AppId);
            if (owner is not null)
            {
                libraries.RemoveAll(l => string.Equals(l, owner, StringComparison.OrdinalIgnoreCase));
                libraries.Insert(0, owner);
            }
        }

        foreach (var library in libraries)
        {
            var steamApps = Path.Combine(library, "steamapps");
            var manifest = Path.Combine(steamApps, $"appmanifest_{AppId}.acf");
            string folderName = DefaultFolderName;
            string? buildId = null;

            if (File.Exists(manifest))
            {
                var acf = VdfParser.Parse(File.ReadAllText(manifest));
                var state = acf["AppState"] ?? acf;
                folderName = state.GetString("installdir") ?? folderName;
                buildId = state.GetString("buildid");
            }

            var installation = FromInstallDir(steamRoot, Path.Combine(steamApps, "common", folderName), buildId);
            if (installation is not null) return installation;
        }

        throw new DirectoryNotFoundException(
            $"Black Myth: Wukong Benchmark Tool (App ID {AppId}) не найден ни в одной библиотеке Steam. " +
            "Установите его (бесплатно) или укажите путь: --install-dir \"<папка>\".");
    }

    private static BenchmarkInstallation? FromInstallDir(string? steamRoot, string installDir, string? buildId)
    {
        if (!Directory.Exists(installDir)) return null;

        var shipping = Path.Combine(installDir, "b1", "Binaries", "Win64", "b1-Win64-Shipping.exe");
        if (!File.Exists(shipping))
        {
            shipping = Directory.EnumerateFiles(installDir, "b1-Win64-Shipping.exe", SearchOption.AllDirectories)
                .FirstOrDefault() ?? "";
            if (shipping.Length == 0) return null;
        }

        var launcher = Path.Combine(installDir, "b1_benchmark.exe");
        return new BenchmarkInstallation(steamRoot, installDir, shipping, File.Exists(launcher) ? launcher : null, buildId);
    }

    private static string? ReadRegistry(RegistryHive hive, string subKey, string name)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(subKey);
            return key?.GetValue(name) as string;
        }
        catch
        {
            return null;
        }
    }
}
