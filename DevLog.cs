using System.Text.RegularExpressions;

namespace HyperInventory;

// Dev-only verbose console logger. Compile into release builds too — gated by Verbose flag so zero cost when off.
public static partial class DevLog
{
    public static bool Verbose { get; set; }

    public static void Step(string msg)    => Write(msg, ConsoleColor.Cyan);
    public static void Ok(string msg)      => Write(msg, ConsoleColor.Green);
    public static void Warn(string msg)    => Write(msg, ConsoleColor.Yellow);
    public static void Err(string msg)     => Write(msg, ConsoleColor.Red);

    public static void Script(string script)
    {
        if (!Verbose) return;
        var safe = RedactPassword().Replace(script, "ConvertTo-SecureString '***'");
        Console.ForegroundColor = ConsoleColor.DarkMagenta;
        Console.WriteLine($"\n[{Ts()}] [SCRIPT] ──────────────────────────────────");
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine(safe);
        Console.ForegroundColor = ConsoleColor.DarkMagenta;
        Console.WriteLine("────────────────────────────────────────────────────");
        Console.ResetColor();
    }

    public static void PsOut(string stdout)
    {
        if (!Verbose || string.IsNullOrWhiteSpace(stdout)) return;
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"[{Ts()}] [stdout] {stdout.Trim()}");
        Console.ResetColor();
    }

    public static void PsErr(string stderr)
    {
        if (!Verbose || string.IsNullOrWhiteSpace(stderr)) return;
        Console.ForegroundColor = ConsoleColor.DarkRed;
        Console.WriteLine($"[{Ts()}] [stderr] {stderr.Trim()}");
        Console.ResetColor();
    }

    public static void ExitCode(int code)
    {
        if (!Verbose) return;
        var color = code == 0 ? ConsoleColor.DarkGreen : ConsoleColor.DarkRed;
        Console.ForegroundColor = color;
        Console.WriteLine($"[{Ts()}] [exit]   {code}");
        Console.ResetColor();
    }

    private static void Write(string msg, ConsoleColor color)
    {
        if (!Verbose) return;
        Console.ForegroundColor = color;
        Console.Write($"[{Ts()}] ");
        Console.ResetColor();
        Console.WriteLine(msg);
    }

    private static string Ts() => DateTimeOffset.Now.ToString("HH:mm:ss.fff");

    [GeneratedRegex(@"ConvertTo-SecureString '[^']*'")]
    private static partial Regex RedactPassword();
}
