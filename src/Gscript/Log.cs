namespace Gscript;

/// <summary>
/// Console output helpers matching the PowerShell module's Write-Host color vocabulary, so the
/// pipeline transcript reads identically to the PS scripts operators are used to. The foreground
/// color is always restored (no leaked color), and writes are serialized so concurrent callers
/// can't interleave a half-colored line.
/// </summary>
public static class Log
{
    private static readonly object Gate = new();

    public static void Plain(string msg = "") => Console.WriteLine(msg);
    public static void Cyan(string msg) => Write(msg, ConsoleColor.Cyan);
    public static void Green(string msg) => Write(msg, ConsoleColor.Green);
    public static void Yellow(string msg) => Write(msg, ConsoleColor.Yellow);
    public static void Red(string msg) => Write(msg, ConsoleColor.Red);
    public static void DarkGray(string msg) => Write(msg, ConsoleColor.DarkGray);
    public static void DarkCyan(string msg) => Write(msg, ConsoleColor.DarkCyan);

    /// <summary>Pink. Reserved for "this is where YOUR stack starts" — the rows a human still
    /// has to act on. Deliberately the loudest colour in the palette and used for exactly one
    /// meaning, because a colour that means several things means nothing at a glance.</summary>
    public static void Magenta(string msg) => Write(msg, ConsoleColor.Magenta);

    private static void Write(string msg, ConsoleColor color)
    {
        lock (Gate)
        {
            var prev = Console.ForegroundColor;
            try { Console.ForegroundColor = color; Console.WriteLine(msg); }
            finally { Console.ForegroundColor = prev; }
        }
    }
}
