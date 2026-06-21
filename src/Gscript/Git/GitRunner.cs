using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Gscript.Git;

/// <summary>Thrown when stale-lock recovery refuses to delete a lock because a live git process is running.</summary>
public sealed class StaleLockClearAbortedException : Exception
{
    public StaleLockClearAbortedException(string message) : base(message) { }
}

/// <summary>
/// High-level git operations layered on <see cref="GitCommand"/>: stale-lock auto-recovery and
/// retry-with-backoff on lock collisions. Defends against the index.lock / HEAD.lock race
/// (GOTCHAS #2): VS Code git-polling briefly grabs the index lock every N seconds, and crashed
/// git processes leave stale locks. "No git procs running + lock exists ⇒ stale" is the
/// load-bearing heuristic.
/// </summary>
public static partial class GitRunner
{
    /// <summary>Six known git lock-file names, auto-cleaned when no git processes are running.</summary>
    private static readonly string[] GitLockNames =
    {
        "index.lock", "HEAD.lock", "config.lock", "packed-refs.lock", "shallow.lock", "fetch.lock",
    };

    [GeneratedRegex(@"index\.lock|HEAD\.lock|Unable to create")]
    private static partial Regex LockCollision();

    public sealed record RetryResult(int ExitCode, string Output, bool Success);

    /// <summary>
    /// Delete stale git lock files in <paramref name="gitDir"/> — but only when NO git process is
    /// running. If a git process is live, refuses (throws <see cref="StaleLockClearAbortedException"/>)
    /// rather than clobber a legitimate concurrent operation. No-op when <paramref name="gitDir"/>
    /// doesn't exist or holds no locks.
    /// </summary>
    public static void ClearStaleGitLocks(string gitDir)
    {
        if (!Directory.Exists(gitDir)) return;

        var found = new List<string>();
        foreach (var name in GitLockNames)
        {
            var p = Path.Combine(gitDir, name);
            if (File.Exists(p)) found.Add(p);
        }
        if (found.Count == 0) return;

        var gitProcs = RunningGitProcesses();
        if (gitProcs.Count > 0)
        {
            Log.Yellow("  Git processes are running:");
            foreach (var (pid, name) in gitProcs)
                Log.Yellow($"    PID {pid}  {name}");
            Log.Yellow("  Not auto-removing. Wait for git processes to finish, then re-run.");
            throw new StaleLockClearAbortedException("StaleLockClearAborted: concurrent git processes running");
        }

        foreach (var lockPath in found)
        {
            var age = (int)(DateTime.Now - File.GetLastWriteTime(lockPath)).TotalSeconds;
            Log.Yellow($"  Removing stale lock: {Path.GetFileName(lockPath)} (age {age}s, no git procs running)");
            try { File.Delete(lockPath); } catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// Run <c>git &lt;args&gt;</c> with up to <paramref name="maxAttempts"/> tries; on a lock-collision
    /// error, sleeps (1s → 2s → 4s exponential backoff), re-runs stale-lock recovery, and retries.
    /// Returns success/failure + the git output; never throws on a git error (the caller keys off
    /// <see cref="RetryResult.Success"/>).
    /// </summary>
    public static RetryResult InvokeGitWithRetry(
        IReadOnlyList<string> gitArgs, string workingDirectory, string gitDir,
        int maxAttempts = 3, string context = "git")
    {
        int delay = 1; // seconds; doubles each retry → 1s, 2s, 4s
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var r = GitCommand.Run(gitArgs, workingDirectory);
            if (r.Success)
                return new RetryResult(r.ExitCode, r.Combined, true);

            bool lockCollision = LockCollision().IsMatch(r.Stderr);
            if (lockCollision && attempt < maxAttempts)
            {
                Log.Yellow($"  {context} attempt {attempt}/{maxAttempts} hit lock; retrying in {delay}s...");
                Thread.Sleep(TimeSpan.FromSeconds(delay));
                try { ClearStaleGitLocks(gitDir); } catch { /* swallow between retries — best-effort */ }
                delay *= 2;
                continue;
            }

            // Non-lock error, or the last attempt: surface stderr (PAT redacted) and report failure.
            if (!string.IsNullOrEmpty(r.Stderr)) Log.Red(Redact(r.Stderr));
            return new RetryResult(r.ExitCode, r.Combined, false);
        }
        return new RetryResult(-1, "", false);
    }

    /// <summary>Redact an embedded PAT (<c>x-access-token:&lt;pat&gt;@</c>) from git output before logging.</summary>
    private static string Redact(string s) =>
        Regex.Replace(s, @"x-access-token:[^@\s]+@", "x-access-token:***@");

    private static List<(int Pid, string Name)> RunningGitProcesses()
    {
        var result = new List<(int, string)>();
        Process[] procs;
        try { procs = Process.GetProcesses(); }
        catch { return result; }

        foreach (var p in procs)
        {
            try
            {
                var name = p.ProcessName;
                if (name.Equals("git", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("git-", StringComparison.OrdinalIgnoreCase))
                {
                    result.Add((p.Id, name));
                }
            }
            catch { /* process may have exited between enumeration and access */ }
            finally { p.Dispose(); }
        }
        return result;
    }
}
