using System.Runtime.Versioning;

namespace WukongAutoBench.Running;

/// <summary>
/// Кадр клиентской области окна: пиксели в формате BGRA, строки сверху вниз.
/// </summary>
public sealed class Frame
{
    public int Width { get; }
    public int Height { get; }
    public byte[] Pixels { get; }

    public Frame(int width, int height, byte[] pixels)
    {
        Width = width;
        Height = height;
        Pixels = pixels;
    }

    /// <summary>
    /// Доля пикселей прямоугольника (координаты в долях кадра 0..1), яркость которых не ниже порога.
    /// Светлый текст меню на тёмном фоне даёт заметную долю, тёмная заставка – почти ноль.
    /// </summary>
    public double BrightShare(double x0, double y0, double x1, double y1, int threshold)
    {
        int left = Clamp(x0 * Width, Width), right = Clamp(x1 * Width, Width);
        int top = Clamp(y0 * Height, Height), bottom = Clamp(y1 * Height, Height);
        if (right <= left || bottom <= top) return 0;

        var bright = 0;
        for (var y = top; y < bottom; y++)
        {
            var row = y * Width * 4;
            for (var x = left; x < right; x++)
            {
                var i = row + x * 4;
                var luma = (Pixels[i + 2] * 299 + Pixels[i + 1] * 587 + Pixels[i] * 114) / 1000;
                if (luma >= threshold) bright++;
            }
        }

        return bright / (double)((right - left) * (bottom - top));
    }

    /// <summary>Сохраняет кадр, уменьшенный в <paramref name="step"/> раз, как 24-битный BMP (для разбора неудачных прогонов).</summary>
    public void SaveBmp(string path, int step = 4)
    {
        int w = Math.Max(1, Width / step), h = Math.Max(1, Height / step);
        var stride = (w * 3 + 3) & ~3;
        var data = new byte[stride * h];
        for (var y = 0; y < h; y++)
        {
            var dst = (h - 1 - y) * stride;
            for (var x = 0; x < w; x++)
            {
                var src = (y * step * Width + x * step) * 4;
                data[dst + x * 3] = Pixels[src];
                data[dst + x * 3 + 1] = Pixels[src + 1];
                data[dst + x * 3 + 2] = Pixels[src + 2];
            }
        }

        using var file = new BinaryWriter(File.Create(path));
        file.Write((ushort)0x4D42);
        file.Write(54 + data.Length);
        file.Write(0);
        file.Write(54);
        file.Write(40);
        file.Write(w);
        file.Write(h);
        file.Write((ushort)1);
        file.Write((ushort)24);
        file.Write(0);
        file.Write(data.Length);
        file.Write(2835);
        file.Write(2835);
        file.Write(0);
        file.Write(0);
        file.Write(data);
    }

    private static int Clamp(double v, int max) => Math.Clamp((int)Math.Round(v), 0, max);
}

/// <summary>
/// Снимок окна игры и распознавание главного меню бенчмарка по яркости пикселей.
/// Нужен, чтобы нажимать кнопки, только когда меню действительно на экране, а не вслепую по таймеру.
/// </summary>
public static class ScreenProbe
{
    /// <summary>
    /// Область пункта «Выйти» (нижний пункт меню слева, в долях кадра). На заставке
    /// «Нажмите любую кнопку» и на экранах загрузки здесь тёмный фон, в главном меню – светлый текст.
    /// </summary>
    private static readonly (double X0, double Y0, double X1, double Y1) ExitItem = (0.045, 0.59, 0.09, 0.62);
    private const int TextThreshold = 110;
    private const double MenuTextShare = 0.05;
    private const int BlankThreshold = 40;
    private const double BlankShare = 0.01;

    /// <summary>На кадре видно главное меню (пункты слева, без затемняющего диалога поверх).</summary>
    public static bool IsMainMenu(Frame frame) =>
        frame.BrightShare(ExitItem.X0, ExitItem.Y0, ExitItem.X1, ExitItem.Y1, TextThreshold) >= MenuTextShare;

    /// <summary>
    /// Кадр почти целиком чёрный: игра ещё загружается и ничего не показывает. Нажимать в это время
    /// бесполезно, а нажатия во время загрузки совпадали с зависанием драйвера видеокарты.
    /// </summary>
    public static bool IsBlank(Frame frame) =>
        frame.BrightShare(0, 0, 1, 1, BlankThreshold) < BlankShare;

    /// <summary>Снимок клиентской области окна с экрана. null, если окно свёрнуто или снимок не удался.</summary>
    [SupportedOSPlatform("windows")]
    public static Frame? Capture(IntPtr hWnd)
    {
        if (!Win32.GetClientRect(hWnd, out var r)) return null;
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        if (w <= 0 || h <= 0) return null;
        var origin = new Win32.POINT();
        Win32.ClientToScreen(hWnd, ref origin);

        var screen = Win32.GetDC(IntPtr.Zero);
        var memory = Win32.CreateCompatibleDC(screen);
        var bitmap = Win32.CreateCompatibleBitmap(screen, w, h);
        var old = Win32.SelectObject(memory, bitmap);
        try
        {
            if (!Win32.BitBlt(memory, 0, 0, w, h, screen, origin.X, origin.Y, Win32.SRCCOPY)) return null;
            Win32.SelectObject(memory, old);
            var info = new Win32.BITMAPINFOHEADER { biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
            var pixels = new byte[w * h * 4];
            return Win32.GetDIBits(memory, bitmap, 0, (uint)h, pixels, ref info, 0) == h ? new Frame(w, h, pixels) : null;
        }
        finally
        {
            Win32.SelectObject(memory, old);
            Win32.DeleteObject(bitmap);
            Win32.DeleteDC(memory);
            Win32.ReleaseDC(IntPtr.Zero, screen);
        }
    }
}
