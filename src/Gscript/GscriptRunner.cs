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
            throw new GscriptException($"origin/main is {behind} commit(s) ahead. Resolve manually: git pull --rebase origin main");

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

        // 10. CI watch (+ nested post-deploy probe — only when CI is watched and green)
        if (watchCi)
        {
            var ci = GithubCiWatch.Watch(cfg.RepoOwner!, cfg.RepoName!, cfg.CiWorkflowFile, commitSha, pat,
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

    private static string? RevListCount(string workingDir, string a, string b)
    {
        var r = GitCommand.Run(new[] { "rev-list", a, "^" + b, "--count" }, workingDir);
        return r.Success ? r.Stdout.Trim() : null;
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
                int maxPct = cfg.ShrinkageOverrides.TryGetValue(f, out var ov) ? ov : g.FileSizeSanity.MaxShrinkPct;
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
                int maxPct = cfg.ShrinkageOverrides.TryGetValue(f, out var ov) ? ov : g.MarkdownLineCount.MaxShrinkPct;
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
