using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Win32;
using WukongAutoBench.Running;

namespace WukongAutoBench.Hardware;

public sealed record InfoItem(string Group, string Label, string Value);

public sealed class SystemInfo
{
    public List<InfoItem> Items { get; } = [];
    public List<string> GpuNames { get; } = [];

    public void Add(string group, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) Items.Add(new InfoItem(group, label, value.Trim()));
    }

    public string? Get(string label) => Items.FirstOrDefault(i => i.Label == label)?.Value;

    [SupportedOSPlatform("windows")]
    public static SystemInfo Collect(string? installDir)
    {
        var info = new SystemInfo();
        var cim = QueryCim();

        var cpu = First(cim, "cpu");
        var cpuName = Str(cpu, "Name") ?? Reg(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString");
        info.Add("CPU", "Процессор", cpuName);
        var cores = Int(cpu, "NumberOfCores") ?? CountPhysicalCores();
        info.Add("CPU", "Ядра / потоки", $"{cores?.ToString() ?? "?"} / {Environment.ProcessorCount}");
        var mhz = Int(cpu, "MaxClockSpeed") ?? RegInt(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0", "~MHz");
        if (mhz is > 0) info.Add("CPU", "Частота (по данным Windows)", $"{mhz / 1000.0:0.00} ГГц");
        var l3 = Int(cpu, "L3CacheSize");
        if (l3 is > 0) info.Add("CPU", "Кэш L3", $"{l3 / 1024.0:0.#} МБ");

        var vramByName = ReadVramFromRegistry();
        var gpus = All(cim, "gpu").ToList();
        if (gpus.Count == 0)
        {
            foreach (var (name, vram) in vramByName)
            {
                info.GpuNames.Add(name);
                info.Add("GPU", "Видеокарта", $"{name}, {FormatBytes(vram)} VRAM");
            }
        }

        var index = 0;
        foreach (var gpu in gpus)
        {
            var name = Str(gpu, "Name") ?? "Неизвестная видеокарта";
            info.GpuNames.Add(name);
            var label = gpus.Count > 1 ? $"Видеокарта {++index}" : "Видеокарта";
            var vram = vramByName.FirstOrDefault(v => v.Name == name).Bytes;
            if (vram == 0) vram = (ulong)(Long(gpu, "AdapterRAM") ?? 0);
            info.Add("GPU", label, vram > 0 ? $"{name}, {FormatBytes(vram)} VRAM" : name);
            info.Add("GPU", label + ": драйвер", Str(gpu, "DriverVersion"));
            var hr = Int(gpu, "CurrentHorizontalResolution");
            var vr = Int(gpu, "CurrentVerticalResolution");
            var hz = Int(gpu, "CurrentRefreshRate");
            if (hr is > 0 && vr is > 0) info.Add("Дисплей", label + ": режим", $"{hr}x{vr} @ {hz} Гц");
        }

        var status = new Win32.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<Win32.MEMORYSTATUSEX>() };
        Win32.GlobalMemoryStatusEx(ref status);
        Win32.GetPhysicallyInstalledSystemMemory(out var installedKb);
        var installed = installedKb > 0 ? installedKb * 1024 : status.ullTotalPhys;
        info.Add("RAM", "Оперативная память", $"{FormatBytes(installed)} (доступно системе {FormatBytes(status.ullTotalPhys)})");

        var modules = All(cim, "mem").ToList();
        if (modules.Count > 0)
        {
            var speed = modules.Select(m => Int(m, "ConfiguredClockSpeed") ?? Int(m, "Speed")).Where(s => s > 0).DefaultIfEmpty().Max();
            var layout = string.Join(" + ", modules.Select(m => FormatBytes((ulong)(Long(m, "Capacity") ?? 0))));
            info.Add("RAM", "Модули", $"{modules.Count} шт.: {layout}{(speed > 0 ? $", {speed} МТ/с" : "")}");
        }

        var os = First(cim, "os");
        info.Add("Система", "ОС", OsName(os));
        info.Add("Система", "Материнская плата", Join(Str(First(cim, "board"), "Manufacturer"), Str(First(cim, "board"), "Product")));
        info.Add("Система", "Схема питания", PowerPlan());

        foreach (var disk in All(cim, "disk"))
        {
            var media = Str(disk, "MediaType") switch { "4" => "SSD", "3" => "HDD", var x => x };
            info.Add("Накопители", Str(disk, "FriendlyName") ?? "Диск",
                Join(media, Str(disk, "BusType") switch { "17" => "NVMe", "11" => "SATA", "7" => "USB", var x => x },
                    Long(disk, "Size") is { } s ? FormatBytes((ulong)s) : null));
        }

        if (installDir is not null)
        {
            try
            {
                var drive = new DriveInfo(Path.GetPathRoot(installDir)!);
                info.Add("Накопители", "Диск с бенчмарком", $"{drive.Name} – свободно {FormatBytes((ulong)drive.AvailableFreeSpace)} из {FormatBytes((ulong)drive.TotalSize)}");
            }
            catch { }
        }

        return info;
    }

    private const string CimScript = """
        $ErrorActionPreference='SilentlyContinue'
        [Console]::OutputEncoding=[Text.Encoding]::UTF8
        $o=[ordered]@{
          cpu   = @(Get-CimInstance Win32_Processor | Select-Object Name,NumberOfCores,NumberOfLogicalProcessors,MaxClockSpeed,L3CacheSize)
          gpu   = @(Get-CimInstance Win32_VideoController | Select-Object Name,DriverVersion,AdapterRAM,CurrentHorizontalResolution,CurrentVerticalResolution,CurrentRefreshRate)
          mem   = @(Get-CimInstance Win32_PhysicalMemory | Select-Object Capacity,Speed,ConfiguredClockSpeed,Manufacturer,PartNumber)
          board = @(Get-CimInstance Win32_BaseBoard | Select-Object Manufacturer,Product)
          os    = @(Get-CimInstance Win32_OperatingSystem | Select-Object Caption,Version,BuildNumber,OSArchitecture)
          disk  = @(Get-PhysicalDisk | Select-Object FriendlyName,@{n='MediaType';e={[int]$_.MediaType}},@{n='BusType';e={[int]$_.BusType}},Size)
        }
        $o | ConvertTo-Json -Depth 4 -Compress
        """;

    [SupportedOSPlatform("windows")]
    private static JsonElement? QueryCim()
    {
        try
        {
            var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(CimScript));
            using var p = Process.Start(new ProcessStartInfo("powershell.exe",
                $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
            });
            if (p is null) return null;
            var output = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(30000)) { p.Kill(); return null; }
            using var doc = JsonDocument.Parse(output.Result);
            return doc.RootElement.Clone();
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<JsonElement> All(JsonElement? root, string name)
    {
        if (root is not { ValueKind: JsonValueKind.Object } r || !r.TryGetProperty(name, out var v)) yield break;
        if (v.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in v.EnumerateArray()) if (e.ValueKind == JsonValueKind.Object) yield return e;
        }
        else if (v.ValueKind == JsonValueKind.Object) yield return v;
    }

    private static JsonElement? First(JsonElement? root, string name) =>
        All(root, name).Select(e => (JsonElement?)e).FirstOrDefault();

    private static string? Str(JsonElement? e, string name)
    {
        if (e is not { ValueKind: JsonValueKind.Object } o || !o.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        };
    }

    private static long? Long(JsonElement? e, string name) =>
        long.TryParse(Str(e, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static int? Int(JsonElement? e, string name) => Long(e, name) is { } v ? (int)v : null;

    [SupportedOSPlatform("windows")]
    private static List<(string Name, ulong Bytes)> ReadVramFromRegistry()
    {
        var result = new List<(string, ulong)>();
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (cls is null) return result;
            foreach (var sub in cls.GetSubKeyNames().Where(n => n.All(char.IsDigit)))
            {
                using var key = cls.OpenSubKey(sub);
                var name = key?.GetValue("DriverDesc") as string;
                var raw = key?.GetValue("HardwareInformation.qwMemorySize");
                ulong bytes = raw switch
                {
                    long l => (ulong)l,
                    byte[] b when b.Length >= 8 => BitConverter.ToUInt64(b, 0),
                    byte[] b when b.Length >= 4 => BitConverter.ToUInt32(b, 0),
                    _ => 0,
                };
                if (bytes == 0 && key?.GetValue("HardwareInformation.MemorySize") is int i) bytes = (uint)i;
                if (name is not null) result.Add((name, bytes));
            }
        }
        catch { }

        return result;
    }

    [SupportedOSPlatform("windows")]
    private static int? CountPhysicalCores()
    {
        uint length = 0;
        Win32.GetLogicalProcessorInformationEx(Win32.RelationProcessorCore, IntPtr.Zero, ref length);
        if (length == 0) return null;
        var buffer = Marshal.AllocHGlobal((int)length);
        try
        {
            if (!Win32.GetLogicalProcessorInformationEx(Win32.RelationProcessorCore, buffer, ref length)) return null;
            int count = 0, offset = 0;
            while (offset < length)
            {
                var size = Marshal.ReadInt32(buffer, offset + 4);
                if (size <= 0) break;
                count++;
                offset += size;
            }

            return count;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [SupportedOSPlatform("windows")]
    private static string OsName(JsonElement? os)
    {
        var caption = Str(os, "Caption");
        const string cv = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
        var build = Reg(cv, "CurrentBuild") ?? Str(os, "BuildNumber");
        var ubr = RegInt(cv, "UBR");
        var display = Reg(cv, "DisplayVersion");
        if (caption is null)
        {
            caption = Reg(cv, "ProductName") ?? "Windows";
            if (int.TryParse(build, out var b) && b >= 22000) caption = caption.Replace("Windows 10", "Windows 11");
        }

        return $"{caption} {display} (сборка {build}{(ubr is > 0 ? "." + ubr : "")}), {RuntimeInformation.OSArchitecture}";
    }

    private static string? PowerPlan()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("powercfg", "/getactivescheme")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = OemEncoding(),
            });
            var text = p?.StandardOutput.ReadToEnd() ?? "";
            p?.WaitForExit(5000);
            var open = text.LastIndexOf('(');
            var close = text.LastIndexOf(')');
            return open >= 0 && close > open ? text[(open + 1)..close] : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Кодировка вывода консольных утилит Windows (OEM, например 866 для русской системы).</summary>
    private static System.Text.Encoding OemEncoding()
    {
        try
        {
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
            return System.Text.Encoding.GetEncoding((int)Win32.GetOEMCP());
        }
        catch
        {
            return System.Text.Encoding.UTF8;
        }
    }

    [SupportedOSPlatform("windows")]
    private static string? Reg(string path, string name)
    {
        try { using var k = Registry.LocalMachine.OpenSubKey(path); return k?.GetValue(name)?.ToString(); }
        catch { return null; }
    }

    [SupportedOSPlatform("windows")]
    private static int? RegInt(string path, string name) => int.TryParse(Reg(path, name), out var v) ? v : null;

    private static string? Join(params string?[] parts)
    {
        var s = string.Join(", ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
        return s.Length == 0 ? null : s;
    }

    public static string FormatBytes(ulong bytes)
    {
        const double gb = 1024.0 * 1024 * 1024;
        return bytes >= gb ? $"{bytes / gb:0.#} ГБ" : $"{bytes / 1024.0 / 1024:0} МБ";
    }
}
