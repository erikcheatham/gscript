using System.Text;
using System.Text.RegularExpressions;
using Gscript.Ci;
using Gscript.Deploy;
using Gscript.Gates;
using Gscript.Git;
using Gscript.Local;

namespace Gscript;

/// <summary>A pipeline failure. The CLI maps this to a non-zero exit code.</summary>
public sealed class GscriptException : Exception
{
    public GscriptException(string message) : base(message) { }
}

/// <summary>
/// The full ceremony (the C# port of <c>Invoke-Gscript</c>): preflight gates → auth → fetch +
/// divergence guard → stage → audit → leak-check → commit (via tempfile) → push → CI watch → probe.
/// Throws <see cref="GscriptException"/> on any failure; never uses <c>exit</c> (the CLI owns the
/// process exit code). Ordering is load-bearing and must not be reordered — see the inline notes.
/// </summary>
public static partial class GscriptRunner
{
    public sealed record RunResult(bool Success, string CommitSha, string ShortSha);

    // Recognized CI-skip directives (idempotency check before appending [skip ci]).
    [GeneratedRegex(@"\[(skip ci|ci skip|no ci|skip actions|actions skip)\]")]
    private static partial Regex SkipCiDirective();

    public static RunResult Run(GscriptConfig cfg)
    {
        // ── validation ────────────────────────────────────────────
        if (string.IsNullOrEmpty(cfg.RepoOwner)) throw new GscriptException("RepoOwner is required.");
        if (string.IsNullOrEmpty(cfg.RepoName)) throw new GscriptException("RepoName is required.");
        if (cfg.FilesToStage is null || cfg.FilesToStage.Count == 0)
            throw new GscriptException("FilesToStage must contain at least one path.");
        if (string.IsNullOrEmpty(cfg.CommitMessage)) throw new GscriptException("CommitMessage is required.");

        // ── NoDeploy: three coordinated mutations ─────────────────
        bool noDeploy = cfg.NoDeploy || cfg.NoDeployDefault;
        bool watchCi = cfg.WatchCi;
        var probeEndpoints = cfg.ProbeEndpoints;
        string commitMessage = cfg.CommitMessage!;

        if (noDeploy)
        {
            commitMessage = AppendSkipCi(commitMessage);
            watchCi = false;                       // no run will appear; don't waste ~60s polling
            probeEndpoints = new();                // nothing to probe
            Log.DarkCyan("[NODEPLOY mode] commit will carry [skip ci]; no CI deploy will fire.");
            Log.DarkCyan("[NODEPLOY mode] Skipping CI watch + post-deploy probes.");
        }

        // ── working dir + git dir ─────────────────────────────────
        string workingDir = string.IsNullOrEmpty(cfg.WorkingDirectory)
            ? Directory.GetCurrentDirectory() : cfg.WorkingDirectory!;
        string gitDir = Path.Combine(workingDir, ".git");
        if (!Directory.Exists(gitDir))
            throw new GscriptException($"{gitDir} not found. WorkingDirectory must contain a git repo.");

        // 1. stale-lock auto-recovery (BEFORE any git op, so fetch/add/commit don't trip a stale lock)
        GitRunner.ClearStaleGitLocks(gitDir);

        // 2. PAT from localmd (BEFORE fetch — the fetch URL embeds it)
        string pat = Localmd.ResolvePat(cfg.LocalmdPath);

        // 3. preflight gates over the working files (refuse to stage corrupt bytes)
        RunWorkingFileGates(cfg, workingDir);

        // 4. fetch + divergence guard (BEFORE staging — catch "origin ahead" before doing work)
        string pushUrl = $"https://x-access-token:{pat}@github.com/{cfg.RepoOwner}/{cfg.RepoName}.git";
        Log.Cyan("Fetching origin/main via PAT-in-URL...");   // label only — never log the URL
        var fetch = GitRunner.InvokeGitWithRetry(new[] { "fetch", "--quiet", pushUrl, "main" }, workingDir, gitDir, context: "fetch");
        string? ahead = RevListCount(workingDir, "HEAD", "FETCH_HEAD");
        string? behind = RevListCount(workingDir, "FETCH_HEAD", "HEAD");
        if (ahead is not null) Log.DarkGray($"  local is {ahead} ahead, {behind} behind origin/main");
        else if (!fetch.Success) Log.DarkGray("  empty origin (first push)");
        if (behind is not null && behind != "0")
            SyncWithOrigin(cfg, workingDir, gitDir, ahead ?? "0", behind!);

        // Runner-tree hygiene: warn (or fail, with --require-clean) about files loose in the working
        // tree OUTSIDE FilesToStage — on a runner-shared checkout they can block the NEXT deploy's FF.
        CheckLooseFiles(cfg, workingDir);

        // DRY RUN: preflight gates + auth + fetch/divergence verified; stop before touching the index.
        if (cfg.DryRun)
        {
            Log.Green("DRY RUN: preflight gates + divergence check passed; stopping before staging (no commit/push).");
            return new RunResult(true, "", "");
        }

        // 5. stage explicit paths (NEVER `git add .` — defensive against bundling operator scratch)
        Log.Cyan("Staging files...");
        foreach (var f in cfg.FilesToStage)
        {
            string full = Path.Combine(workingDir, f);
            if (!File.Exists(full)) { Log.Yellow($"  SKIP (missing): {f}"); continue; }
            var add = GitRunner.InvokeGitWithRetry(new[] { "add", "--", f }, workingDir, gitDir, context: "add");
            if (!add.Success) throw new GscriptException($"git add failed for {f} after retries");
        }

        // 6. audit the staged set
        var staged = GitCommand.Run(new[] { "diff", "--cached", "--name-only" }, workingDir)
            .Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        Log.Cyan($"Files staged: {staged.Count}");
        foreach (var s in staged) Log.DarkGray($"  {s}");
        if (staged.Count == 0) throw new GscriptException("nothing was staged");

        var normalizedExpected = cfg.FilesToStage.Select(f => f.Replace('\\', '/')).ToHashSet();
        var unexpected = staged.Where(s => !normalizedExpected.Contains(s)).ToList();
        if (unexpected.Count > 0)
        {
            Log.Yellow("WARNING: unexpected files staged (will commit anyway):");
            foreach (var u in unexpected) Log.Yellow($"  {u}");
        }

        // 7. leak-check (scans `git diff --cached` — MUST run post-stage, pre-commit)
        RunLeakCheck(cfg, workingDir);

        // 8. commit via tempfile (UTF-8 no-BOM — avoids quoting hell + a BOM in the message)
        string msgPath = Path.Combine(Path.GetTempPath(), $"git_commit_msg_{Guid.NewGuid():N}.txt");
        File.WriteAllText(msgPath, commitMessage, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        var commit = GitRunner.InvokeGitWithRetry(
            new[] { "-c", $"user.name={cfg.CommitName}", "-c", $"user.email={cfg.CommitEmail}", "commit", "-F", msgPath },
            workingDir, gitDir, context: "commit");
        try { File.Delete(msgPath); } catch { /* best-effort */ }
        if (!commit.Success) throw new GscriptException("git commit failed after retries");

        string commitSha = GitCommand.Run(new[] { "rev-parse", "HEAD" }, workingDir).Stdout.Trim();
        string shortSha = commitSha.Length >= 7 ? commitSha[..7] : commitSha;

        // 9. push
        Log.Cyan("Pushing to origin/main...");                 // label only
        var push = GitRunner.InvokeGitWithRetry(new[] { "push", pushUrl, "main" }, workingDir, gitDir, context: "push");
        if (!push.Success) throw new GscriptException("git push failed after retries");
        Log.Green($"PUSHED: {shortSha} to origin/main.");

        // Refresh the local tracking ref so `git status` is honest. A PAT-in-URL push does NOT update
        // refs/remotes/origin/main (the "ahead by N" phantom + the divergence-trap it sets up for the
        // next push). The `main:refs/remotes/origin/main` refspec forces the just-pushed origin tip
        // onto the local tracking ref despite the embedded-PAT URL. Best-effort — never fails the push.
        var refresh = GitRunner.InvokeGitWithRetry(
            new[] { "fetch", "--quiet", pushUrl, "main:refs/remotes/origin/main" }, workingDir, gitDir, context: "post-push fetch");
        if (refresh.Success) Log.DarkGray("  refreshed refs/remotes/origin/main (tracking ref honest).");
        else Log.Yellow("  WARN: tracking-ref refresh failed (harmless; run `git fetch origin main` to sync).");

        // 10. CI watch (+ nested post-deploy probe — only when CI is watched and green)
        CiWatchResult? ci = null;
        if (watchCi)
        {
            ci = GithubCiWatch.Watch(cfg.RepoOwner!, cfg.RepoName!, cfg.CiWorkflowFile, commitSha, pat,
                cfg.CiWatchMaxMinutes, cfg.CiWatchPollSeconds);
            if (!ci.Success)
                throw new GscriptException($"CI did not complete successfully (conclusion={ci.Conclusion})");

            if (probeEndpoints.Count > 0)
            {
                var endpoints = probeEndpoints.Select(ToProbeEndpoint).ToList();
                var probe = PostDeployProbe.Probe(endpoints);
                if (!probe.Success)
                    throw new GscriptException($"Probe failed for: {string.Join(", ", probe.Failures)}");
            }
        }

        // 11. push-log journal (append-only; only on success, after push + CI verdict)
        AppendPushLog(cfg, shortSha, noDeploy, watchCi, ci);

        Log.Green("SUCCESS: sprint shipped end-to-end.");
        return new RunResult(true, commitSha, shortSha);
    }

    // ── helpers ───────────────────────────────────────────────────

    private static string AppendSkipCi(string commitMessage)
    {
        var parts = commitMessage.Split('\n', 2);
        string subject = parts[0].TrimEnd();
        if (!SkipCiDirective().IsMatch(subject)) subject += " [skip ci]";
        return parts.Length > 1 ? subject + "\n" + parts[1] : subject;
    }

    /// <summary>
    /// Append a one-entry markdown push-log line per SUCCESSFUL push (the self-maintaining dated
    /// journal). No-op when neither --log nor gscript.json logFile is set. Append-only via
    /// File.AppendAllText (NEVER read-modify-write — sidesteps the trailing-line-clip truncation
    /// class). repoName is in the line so multiple repos can point logFile at ONE shared journal.
    /// Best-effort: a log-append failure is warned but never fails the (already-succeeded) push.
    /// </summary>
    private static void AppendPushLog(GscriptConfig cfg, string shortSha, bool noDeploy, bool watchCi, CiWatchResult? ci)
    {
        if (string.IsNullOrWhiteSpace(cfg.LogFile)) return;

        string subject = (cfg.CommitMessage ?? "").Split('\n', 2)[0].Trim();
        string repo = string.IsNullOrEmpty(cfg.RepoName) ? "(repo)" : cfg.RepoName!;
        string tag = noDeploy ? "[nodeploy]" : "[deploy]";
        string stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm");

        string ciLine;
        if (noDeploy) ciLine = "CI: skipped [skip ci]";
        else if (!watchCi) ciLine = "CI: not watched (--no-watch)";
        else if (ci is null) ciLine = "CI: not watched";
        else if (ci.Conclusion == "skipped") ciLine = $"CI: skipped (paths-ignore) ({ci.Url})";
        else ciLine = $"CI: {(ci.Conclusion == "success" ? "green" : ci.Conclusion)} ({ci.Url})";

        string path = cfg.LogFile!;
        var sb = new StringBuilder();
        if (!File.Exists(path))
        {
            try { Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!); } catch { /* best-effort */ }
            sb.Append("# Push log\n\n");
        }
        sb.Append($"- **{stamp}** `{shortSha}` {repo} {tag} -- {subject}\n");
        sb.Append($"  {ciLine}\n");

        try
        {
            File.AppendAllText(path, sb.ToString());
            Log.DarkGray($"  push-log appended -> {path}");
        }
        catch (Exception ex)
        {
            Log.Yellow($"  WARN: push-log append failed ({ex.Message})");
        }
    }

    private static string? RevListCount(string workingDir, string a, string b)
    {
        var r = GitCommand.Run(new[] { "rev-list", a, "^" + b, "--count" }, workingDir);
        return r.Success ? r.Stdout.Trim() : null;
    }

    /// <summary>
    /// Concurrent-work sync (alpha.6). origin/main advanced while we were editing. The decision tree:
    /// (a) if the incoming commits touch ANY path in FilesToStage → real content conflict → refuse
    /// (human reconciles); (b) else if --no-sync → refuse with the manual FF hint; (c) else if we
    /// ALSO have local commits ahead (true divergence) → refuse (auto-FF can't apply over local
    /// commits); (d) else (pure fast-forward, DISJOINT from our files) → auto-fast-forward onto origin
    /// so the push is a clean FF and two agents editing different files integrate automatically.
    /// </summary>
    private static void SyncWithOrigin(GscriptConfig cfg, string workingDir, string gitDir, string ahead, string behind)
    {
        var incoming = GitCommand.Run(new[] { "diff", "--name-only", "HEAD", "FETCH_HEAD" }, workingDir)
            .Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet();
        var mine = cfg.FilesToStage.Select(f => f.Replace('\\', '/')).ToHashSet();
        var overlap = incoming.Where(mine.Contains).OrderBy(x => x, StringComparer.Ordinal).ToList();

        if (overlap.Count > 0)
            throw new GscriptException(
                $"origin advanced {behind} commit(s) and the incoming change(s) touch file(s) you're pushing: " +
                $"{string.Join(", ", overlap)}. Real conflict — reconcile manually: git pull --rebase origin main");

        if (cfg.NoSync)
            throw new GscriptException(
                $"origin/main is {behind} commit(s) ahead (disjoint from your files); --no-sync is set. " +
                "Fast-forward manually: git pull --ff-only origin main");

        if (ahead != "0")
            throw new GscriptException(
                $"local and origin/main have diverged (local +{ahead}, origin +{behind}, disjoint files). " +
                "Auto-FF can't apply over local commits — reconcile manually: git pull --rebase origin main");

        Log.Cyan($"Auto-sync: origin advanced {behind} commit(s), disjoint from your files — fast-forwarding...");
        var ff = GitRunner.InvokeGitWithRetry(new[] { "merge", "--ff-only", "FETCH_HEAD" }, workingDir, gitDir, context: "auto-FF");
        if (!ff.Success)
            throw new GscriptException(
                "auto-FF failed (likely loose working-tree changes overlapping the incoming commits). " +
                "Run `git status`, then `git pull --ff-only origin main` manually.");
        Log.Green($"  fast-forwarded {behind} commit(s) from origin (your files untouched).");
    }

    /// <summary>
    /// Runner-tree hygiene (alpha.6). On a runner-shared checkout (where the dev/authoring clone IS
    /// the CI runner's deploy tree) files loose OUTSIDE FilesToStage — modified-tracked OR
    /// untracked — don't block THIS push, but can break the NEXT deploy's `git pull --ff-only`.
    /// Warn + list by default; with --require-clean, fail. Best-effort: a `git status` hiccup never
    /// blocks the push.
    /// </summary>
    private static void CheckLooseFiles(GscriptConfig cfg, string workingDir)
    {
        var status = GitCommand.Run(new[] { "status", "--porcelain" }, workingDir);
        if (!status.Success) return;

        var mine = cfg.FilesToStage.Select(f => f.Replace('\\', '/')).ToHashSet();
        var loose = new List<string>();
        foreach (var line in status.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length < 4) continue;                      // porcelain v1: "XY path"
            string path = line[3..].Trim();
            int arrow = path.IndexOf(" -> ", StringComparison.Ordinal);
            if (arrow >= 0) path = path[(arrow + 4)..];          // rename: take the destination path
            path = path.Trim('"').Replace('\\', '/');
            if (path.Length > 0 && !mine.Contains(path)) loose.Add(path);
        }
        if (loose.Count == 0) return;

        Log.Yellow($"Heads-up: {loose.Count} file(s) loose in the working tree, outside --files:");
        foreach (var l in loose) Log.Yellow($"    {l}");
        Log.Yellow("  (On a runner-shared checkout these can block the NEXT deploy's `git pull --ff-only`.)");
        if (cfg.RequireClean)
            throw new GscriptException("--require-clean: working tree has files outside --files. Commit, stash, or .gitignore them first.");
    }

    private static ProbeEndpoint ToProbeEndpoint(ProbeEndpointConfig p)
    {
        int min = p.ExpectedRange.Count > 0 ? p.ExpectedRange[0] : 200;
        int max = p.ExpectedRange.Count > 0 ? p.ExpectedRange[^1] : 399;
        return new ProbeEndpoint(p.Url, min, max);
    }

    private static void RunWorkingFileGates(GscriptConfig cfg, string workingDir)
    {
        var g = cfg.Gates;

        // trailing-null (canonical, always-on when enabled)
        if (g.TrailingNull)
        {
            Log.Cyan("Pre-flight: trailing-null check...");
            var fails = new List<string>();
            foreach (var f in cfg.FilesToStage)
            {
                string full = Path.Combine(workingDir, f);
                if (!File.Exists(full)) continue;
                if (!TrailingNullGate.IsTextFile(f)) continue;
                var r = TrailingNullGate.Check(full);
                if (!r.Ok) { Log.Red($"  FAIL {f} ({r.Reason})"); fails.Add(f); }
                else Log.Green($"  OK  {f}");
            }
            if (fails.Count > 0)
            {
                Log.Yellow(@"Recovery: python -c ""import pathlib;p='<file>';pathlib.Path(p).write_bytes(pathlib.Path(p).read_bytes().rstrip(b'\x00'))""");
                throw new GscriptException($"Trailing nulls in {fails.Count} file(s).");
            }
        }

        // size + structured + markdown (drifted gates), per file
        bool anySize = g.FileSizeSanity.Enabled, anyStruct = g.StructuredFile, anyMd = g.MarkdownLineCount.Enabled;
        if (!(anySize || anyStruct || anyMd)) return;

        Log.Cyan("Pre-flight: size + structure + markdown gates...");
        foreach (var f in cfg.FilesToStage)
        {
            string full = Path.Combine(workingDir, f);
            if (!File.Exists(full)) continue;
            string rel = f.Replace('\\', '/');

            if (anySize)
            {
                int maxPct = cfg.MaxShrinkPctOverride
                             ?? (cfg.ShrinkageOverrides.TryGetValue(f, out var ov) ? ov : g.FileSizeSanity.MaxShrinkPct);
                var r = FileSizeSanityGate.Check(rel, full, workingDir, maxPct);
                if (!r.Ok) { Log.Red($"  FAIL {f} ({r.Reason})"); throw new GscriptException($"File-size gate failed on {f}: {r.Reason}"); }
            }
            if (anyStruct)
            {
                var r = StructuredFileGate.Check(full);
                if (!r.Ok) { Log.Red($"  FAIL {f} ({r.Reason})"); throw new GscriptException($"Structured-file gate failed on {f}: {r.Reason}"); }
            }
            if (anyMd)
            {
                int maxPct = cfg.MaxShrinkPctOverride
                             ?? (cfg.ShrinkageOverrides.TryGetValue(f, out var ov) ? ov : g.MarkdownLineCount.MaxShrinkPct);
                var r = MarkdownLineCountGate.Check(rel, full, workingDir, maxPct, g.MarkdownLineCount.MinHeadLines);
                if (!r.Ok) { Log.Red($"  FAIL {f} ({r.Reason})"); throw new GscriptException($"Markdown-line gate failed on {f}: {r.Reason}"); }
            }
            Log.Green($"  OK  {f}");
        }
    }

    private static void RunLeakCheck(GscriptConfig cfg, string workingDir)
    {
        if (!ShouldLeakCheck(cfg)) return;

        Log.Cyan("Pre-flight: leak-check (public tree)...");
        var patterns = Localmd.LeakPatterns(cfg.LocalmdPath, msg => Log.Yellow($"  WARN: {msg}"));
        var r = LeakCheckGate.Check(patterns, workingDir, msg => Log.Yellow($"  WARN: {msg}"));
        if (!r.Ok)
        {
            foreach (var m in r.Matches) Log.Red($"  LEAK [{m.Severity}] {m.PatternName} in {m.File}: {m.Excerpt}");
            throw new GscriptException($"Leak-check failed: {r.Matches.Count} match(es) in staged content. Refusing to push to a public tree.");
        }
        Log.Green($"  OK  leak-check clean ({r.PatternsScanned} pattern(s) scanned)");
    }

    private static bool ShouldLeakCheck(GscriptConfig cfg)
    {
        var mode = (cfg.Gates.LeakCheck.Enabled ?? "auto").ToLowerInvariant();
        if (mode == "true") return true;
        if (mode == "false") return false;
        // auto: explicit leakCheckRequired wins, else derive from visibility (public ⇒ required)
        if (cfg.LeakCheckRequired.HasValue) return cfg.LeakCheckRequired.Value;
        return string.Equals(cfg.RepoVisibility, "public", StringComparison.OrdinalIgnoreCase);
    }
}
