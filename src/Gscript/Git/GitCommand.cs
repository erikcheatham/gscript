using System.Diagnostics;
using System.Text;

namespace Gscript.Git;

/// <summary>
/// Low-level git subprocess runner. Arguments are passed via <see cref="ProcessStartInfo.ArgumentList"/>
/// (no shell, no string concatenation) — which defends against the PowerShell quoting traps the
/// PS module had to work around with tempfiles. Reads stdout/stderr asynchronously to avoid the
/// classic pipe-buffer deadlock on large output. Never throws on a non-zero git exit; the caller
/// inspects <see cref="Result.ExitCode"/>. The higher-level retry + stale-lock recovery live in
/// <c>GitRunner</c> (which wraps this).
/// </summary>
public static class GitCommand
{
    public sealed record Result(int ExitCode, string Stdout, string Stderr)
    {
        public bool Success => ExitCode == 0;

        /// <summary>stdout then stderr (approximates PowerShell's <c>2&gt;&amp;1</c> capture).</summary>
        public string Combined =>
            Stdout.Length == 0 ? Stderr
            : Stderr.Length == 0 ? Stdout
            : Stdout + "\n" + Stderr;
    }

    /// <summary>
    /// Run <c>git &lt;args&gt;</c> in <paramref name="workingDirectory"/>. Throws only if the git
    /// executable itself cannot be started (e.g. git not on PATH).
    /// </summary>
    public static Result Run(IEnumerable<string> args, string workingDirectory)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        proc.WaitForExit();

        return new Result(
            proc.ExitCode,
            stdout.ToString().TrimEnd('\r', '\n'),
            stderr.ToString().TrimEnd('\r', '\n'));
    }
}
