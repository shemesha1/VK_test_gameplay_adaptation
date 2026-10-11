using System.Text;

namespace WukongAutoBench.Util;

public static class Log
{
    private static readonly StringBuilder Buffer = new();
    private static readonly object Sync = new();

    public static string Text
    {
        get { lock (Sync) return Buffer.ToString(); }
    }

    public static void Info(string message) => Write(message, ConsoleColor.Gray);
    public static void Step(string message) => Write("> " + message, ConsoleColor.Cyan);
    public static void Ok(string message) => Write("OK " + message, ConsoleColor.Green);
    public static void Warn(string message) => Write("! " + message, ConsoleColor.Yellow);
    public static void Error(string message) => Write("x " + message, ConsoleColor.Red);

    private static void Write(string message, ConsoleColor color)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        lock (Sync)
        {
            Buffer.AppendLine(line);
            var old = Console.ForegroundColor;
            Console.ForegroundColor = color;
            Console.WriteLine(line);
            Console.ForegroundColor = old;
        }
    }
}
