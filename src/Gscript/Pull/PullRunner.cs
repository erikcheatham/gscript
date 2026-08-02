using Gscript.Credentials;
using Gscript.Git;

namespace Gscript;

/// <summary>
/// <c>gscript pull</c> — fetch + fast-forward through the same ceremony machinery as the push,
/// so the operator never runs plain git with credentials again (alpha.14; the operator's ask,
/// 2026-08-01, after a GCM dialog interrupted a routine PAT-URL pull).
///
/// <para><b>What riding the ceremony buys over <c>git pull</c>:</b> the credential is resolved at
/// run time through <see cref="CredentialResolver"/> (localmd PAT today; a TPM-minted
/// <c>contents:read</c> installation token wherever <c>credentialSource</c> opts into githubapp) —
/// no env var to set per session, no credential parked in GCM or <c>.git/config</c>, and no
/// interactive prompt EVER, because <see cref="GitCommand"/> pins
/// <c>credential.interactive=false</c> + <c>GIT_TERMINAL_PROMPT=0</c> on every child git. Stale
/// locks are cleared before the first git op (the same landmine class that wedged three repos on
/// 07-29 blocks pulls too), and the tracking ref is refreshed after integrating, so the
/// PAT-recovery gotcha ("origin/main tracking ref stale after PAT-URL ops") is mechanized away.</para>
///
/// <para><b>Fast-forward ONLY, and never over local commits.</b> A pull that finds unpushed local
/// commits refuses: integrating origin over local work is the push ceremony's decision (it has the
/// divergence guard and the operator's attention), not a pull's. This mirrors
/// <c>SyncWithOrigin</c>'s posture — gscript only ever fast-forwards automatically.</para>
/// </summary>
public static class PullRunner
{
    public static bool Run(GscriptConfig cfg)
    {
        if (string.IsNullOrWhiteSpace(cfg.RepoOwner) || string.IsNullOrWhiteSpace(cfg.RepoName))
            throw new GscriptException(
                "pull needs repoOwner + repoName (gscript.json in the working dir, or --repo-owner/--repo-name).");

        string workingDir = string.IsNullOrEmpty(cfg.WorkingDirectory)
            ? Directory.GetCurrentDirectory() : cfg.WorkingDirectory!;
        string gitDir = Path.Combine(workingDir, ".git");
        if (!Directory.Exists(gitDir))
            throw new GscriptException($"{gitDir} not found. Run from a repo root or pass --working-dir.");

        // 1. Stale-lock recovery BEFORE any git op — a killed editor/agent git leaves index.lock,
        //    and a pull trips over it exactly like a push does.
        GitRunner.ClearStaleGitLocks(gitDir);

        // 2. Read credential. contents:read when githubapp answers; the URL shape is identical
        //    for a PAT and an installation token, and it is never logged.
        string token = CredentialResolver.ResolvePullToken(cfg);
        string url = $"https://x-access-token:{token}@github.com/{cfg.RepoOwner}/{cfg.RepoName}.git";

        Log.Cyan($"Fetching {cfg.RepoOwner}/{cfg.RepoName} main (credential embedded, never logged)...");
        var fetch = GitRunner.InvokeGitWithRetry(
            new[] { "fetch", "--quiet", url, "main" }, workingDir, gitDir, context: "fetch");
        if (!fetch.Success)
            throw new GscriptException(
                "git fetch failed after retries — network, auth, or repo name. See the log above "
                + "(output is redacted; the URL never appears).");

        string before = GitCommand.Run(new[] { "rev-parse", "HEAD" }, workingDir).Stdout.Trim();
        string beforeShort = Short(before);

        string ahead = CountCommits(workingDir, "FETCH_HEAD", "HEAD");   // local-only commits
        string behind = CountCommits(workingDir, "HEAD", "FETCH_HEAD");  // origin-only commits
        Log.DarkGray($"  local {beforeShort}: {ahead} ahead, {behind} behind origin/main");

        if (ahead != "0")
            throw new GscriptException(
                $"local main has {ahead} unpushed commit(s). Pull refuses to integrate over local "
                + "work — ship them via `gscript push` (or reconcile by hand), then pull again.");

        if (behind == "0")
        {
            Log.Green($"Already up to date at {beforeShort}.");
            RefreshTrackingRef(url, workingDir, gitDir);
            return true;
        }

        if (cfg.DryRun)
        {
            Log.Green($"DRY RUN: origin is {behind} commit(s) ahead; would fast-forward "
                + $"{beforeShort} -> {Short(FetchHeadSha(workingDir))}. Nothing changed.");
            return true;
        }

        // 3. Fast-forward. --ff-only is the safety: git refuses on divergence, and refuses to
        //    clobber uncommitted local edits that overlap incoming files. Merge output carries no
        //    URL (FETCH_HEAD is local), so echoing it on failure is safe.
        var merge = GitRunner.InvokeGitWithRetry(
            new[] { "merge", "--ff-only", "FETCH_HEAD" }, workingDir, gitDir, context: "ff-merge");
        if (!merge.Success)
            throw new GscriptException(
                "fast-forward failed — most likely uncommitted local changes overlap the incoming "
                + $"files (stash or commit them), or history diverged. git said: {merge.Output.Trim()}");

        string after = GitCommand.Run(new[] { "rev-parse", "HEAD" }, workingDir).Stdout.Trim();
        Log.Green($"PULLED: {beforeShort} -> {Short(after)} ({behind} commit(s)).");

        // 4. Tracking-ref refresh (best-effort): a URL fetch does not move refs/remotes/origin/main,
        //    which is the stale-tracking-ref trap. Same refspec trick as the post-push refresh.
        RefreshTrackingRef(url, workingDir, gitDir);
        return true;
    }

    private static void RefreshTrackingRef(string url, string workingDir, string gitDir)
    {
        var refresh = GitRunner.InvokeGitWithRetry(
            new[] { "fetch", "--quiet", url, "main:refs/remotes/origin/main" },
            workingDir, gitDir, context: "tracking-ref refresh");
        if (refresh.Success) Log.DarkGray("  refreshed refs/remotes/origin/main (tracking ref honest).");
        else Log.Yellow("  WARN: tracking-ref refresh failed (harmless; `git status` may over-report).");
    }

    /// <summary>rev-list --count from..to — commits reachable from <c>to</c> but not <c>from</c>.
    /// "0" when the count is unreadable (empty repo), which downstream treats as nothing-to-do.</summary>
    private static string CountCommits(string workingDir, string from, string to)
    {
        var r = GitCommand.Run(new[] { "rev-list", "--count", $"{from}..{to}" }, workingDir);
        return r.Success ? r.Stdout.Trim() : "0";
    }

    private static string FetchHeadSha(string workingDir) =>
        GitCommand.Run(new[] { "rev-parse", "FETCH_HEAD" }, workingDir).Stdout.Trim();

    private static string Short(string sha) => sha.Length >= 7 ? sha[..7] : sha;
}
