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
/// <para>Every invocation is forced NON-INTERACTIVE (<c>GIT_TERMINAL_PROMPT=0</c> plus
/// <c>credential.interactive=false</c> and GCM's <c>credential.guiPrompt=false</c>). gscript always
/// supplies its own credential in the push URL, so a credential prompt can only mean the mint or the
/// token failed - and a modal dialog there HANGS the terminal instead of reporting the failure.
/// Fail clean, not modal (docs/CREDENTIAL-SOURCE.md Phase C).</para>
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

    /// <summary>Config overrides prepended to EVERY invocation so a credential failure reports
    /// instead of opening a blocking dialog. <c>credential.interactive</c> covers git's own helper
    /// negotiation; <c>credential.guiPrompt</c> is Git Credential Manager's separate switch. These
    /// must precede the git subcommand, which is why they are prepended rather than appended.</summary>
    private static readonly string[] NonInteractiveArgs =
    {
        "-c", "credential.interactive=false",
        "-c", "credential.guiPrompt=false",
    };

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
        foreach (var a in NonInteractiveArgs) psi.ArgumentList.Add(a);
        foreach (var a in args) psi.ArgumentList.Add(a);

        // Env belt to the -c suspenders: kills the terminal prompt even for a helper that ignores
        // the config overrides. Set on the child only - the operator's own shell is untouched.
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";

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
