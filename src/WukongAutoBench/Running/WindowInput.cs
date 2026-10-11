using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace WukongAutoBench.Running;

[SupportedOSPlatform("windows")]
internal static class WindowInput
{
    public static void EnableDpiAwareness()
    {
        try { Win32.SetProcessDpiAwarenessContext(Win32.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2); }
        catch { }
    }

    /// <summary>Самое крупное видимое окно процесса (у UE это окно игры, а не служебные).</summary>
    public static IntPtr FindMainWindow(int processId)
    {
        IntPtr best = IntPtr.Zero;
        long bestArea = 0;
        Win32.EnumWindows((hWnd, _) =>
        {
            Win32.GetWindowThreadProcessId(hWnd, out var pid);
            if (pid != processId || !Win32.IsWindowVisible(hWnd)) return true;
            if (!Win32.GetClientRect(hWnd, out var r)) return true;
            long area = (long)(r.Right - r.Left) * (r.Bottom - r.Top);
            if (area > bestArea && r.Right - r.Left >= 320) { best = hWnd; bestArea = area; }
            return true;
        }, IntPtr.Zero);
        return best;
    }

    public static (int Width, int Height) GetClientSize(IntPtr hWnd)
    {
        Win32.GetClientRect(hWnd, out var r);
        return (r.Right - r.Left, r.Bottom - r.Top);
    }

    public static bool IsForeground(IntPtr hWnd) => Win32.GetForegroundWindow() == hWnd;

    /// <summary>
    /// Windows не даёт фоновому процессу просто так забрать фокус. Стандартный обход:
    /// «нажать» Alt (это снимает блокировку SetForegroundWindow) и привязать ввод к потоку окна.
    /// </summary>
    public static bool Activate(IntPtr hWnd)
    {
        if (IsForeground(hWnd)) return true;
        if (Win32.IsIconic(hWnd)) Win32.ShowWindow(hWnd, Win32.SW_RESTORE);

        var foreground = Win32.GetForegroundWindow();
        var fgThread = Win32.GetWindowThreadProcessId(foreground, out _);
        var myThread = Win32.GetCurrentThreadId();
        var attached = fgThread != 0 && fgThread != myThread && Win32.AttachThreadInput(myThread, fgThread, true);
        try
        {
            Key(Win32.VK_MENU, Win32.SCAN_ALT, down: true);
            Key(Win32.VK_MENU, Win32.SCAN_ALT, down: false);
            Win32.BringWindowToTop(hWnd);
            Win32.SetForegroundWindow(hWnd);
        }
        finally
        {
            if (attached) Win32.AttachThreadInput(myThread, fgThread, false);
        }

        Thread.Sleep(300);
        return IsForeground(hWnd);
    }

    /// <summary>Клик в точку окна, заданную долями клиентской области (0..1) – не зависит от разрешения.</summary>
    public static void ClickRelative(IntPtr hWnd, double fx, double fy)
    {
        Win32.GetClientRect(hWnd, out var r);
        var p = new Win32.POINT
        {
            X = (int)Math.Round((r.Right - r.Left) * fx),
            Y = (int)Math.Round((r.Bottom - r.Top) * fy),
        };
        Win32.ClientToScreen(hWnd, ref p);

        var vx = Win32.GetSystemMetrics(Win32.SM_XVIRTUALSCREEN);
        var vy = Win32.GetSystemMetrics(Win32.SM_YVIRTUALSCREEN);
        var vw = Math.Max(1, Win32.GetSystemMetrics(Win32.SM_CXVIRTUALSCREEN));
        var vh = Math.Max(1, Win32.GetSystemMetrics(Win32.SM_CYVIRTUALSCREEN));
        var nx = (int)Math.Round((p.X - vx) * 65535.0 / (vw - 1));
        var ny = (int)Math.Round((p.Y - vy) * 65535.0 / (vh - 1));

        Win32.SetCursorPos(p.X, p.Y);
        Thread.Sleep(80);
        const uint abs = Win32.MOUSEEVENTF_ABSOLUTE | Win32.MOUSEEVENTF_VIRTUALDESK;
        Send(Mouse(nx, ny, Win32.MOUSEEVENTF_MOVE | abs));
        Thread.Sleep(80);
        Send(Mouse(nx, ny, Win32.MOUSEEVENTF_LEFTDOWN | abs));
        Thread.Sleep(90);
        Send(Mouse(nx, ny, Win32.MOUSEEVENTF_LEFTUP | abs));
    }

    public static void PressEnter()
    {
        Key(Win32.VK_RETURN, Win32.SCAN_RETURN, down: true);
        Thread.Sleep(90);
        Key(Win32.VK_RETURN, Win32.SCAN_RETURN, down: false);
    }

    private static void Key(ushort vk, ushort scan, bool down)
    {
        var input = new Win32.INPUT
        {
            type = Win32.INPUT_KEYBOARD,
            u = new Win32.InputUnion
            {
                ki = new Win32.KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = down ? 0 : Win32.KEYEVENTF_KEYUP },
            },
        };
        Send(input);
    }

    private static Win32.INPUT Mouse(int x, int y, uint flags) => new()
    {
        type = Win32.INPUT_MOUSE,
        u = new Win32.InputUnion { mi = new Win32.MOUSEINPUT { dx = x, dy = y, dwFlags = flags } },
    };

    private static void Send(Win32.INPUT input)
    {
        var sent = Win32.SendInput(1, [input], Marshal.SizeOf<Win32.INPUT>());
        if (sent == 0)
            Debug.WriteLine($"SendInput failed: {Marshal.GetLastWin32Error()}");
    }
}
