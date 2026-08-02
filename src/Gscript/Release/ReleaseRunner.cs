using System.Text.RegularExpressions;
using Gscript.Ci;
using Gscript.Credentials;
using Gscript.Git;

namespace Gscript;

/// <summary>
/// <c>gscript release</c> — tag + tag-push + publish-workflow report through the ceremony
/// (alpha.16). Retires the LAST plain-git-with-credentials operation: after pull (alpha.14)
/// joined push under the ceremony, the release tag was still a raw <c>git tag</c> + PAT-URL
/// push in the operator's shell. Now the whole git surface rides one tool.
///
/// <para><b>The lockstep gate is the point, not a nicety.</b> alpha.13 shipped with the CLI's
/// version const lagging the csproj; the drift was caught by hand at the alpha.14 bump and a
/// "lint for this" was noted as debt. This verb IS that lint, run at the exact moment it
/// matters: the tag name comes from the csproj's <c>&lt;Version&gt;</c>, and every configured
/// lockstep file (the CLI const's file, the CHANGELOG) must literally contain that version
/// string or the release refuses before anything is tagged. A version that ships is a version
/// that agrees with itself.</para>
///
/// <para><b>Refuses, in order:</b> no resolvable version · lockstep file missing the version ·
/// working tree has local commits not on origin (release what is SHIPPED, not what is local) ·
/// tag already exists. The publish-workflow report at the end is bounded and never gated —
/// same contract as the secondary CI report, because a red publish needs the operator's eyes,
/// not a half-rolled-back tag.</para>
/// </summary>
public static class ReleaseRunner
{
    public static bool Run(GscriptConfig cfg)
    {
        if (string.IsNullOrWhiteSpace(cfg.RepoOwner) || string.IsNullOrWhiteSpace(cfg.RepoName))
            throw new GscriptException(
                "release needs repoOwner + repoName (gscript.json in the working dir, or --repo-owner/--repo-name).");

        string workingDir = string.IsNullOrEmpty(cfg.WorkingDirectory)
            ? Directory.GetCurrentDirectory() : cfg.WorkingDirectory!;
        string gitDir = Path.Combine(workingDir, ".git");
        if (!Directory.Exists(gitDir))
            throw new GscriptException($"{gitDir} not found. Run from a repo root or pass --working-dir.");

        // ── 1. Resolve the version (--tag wins; else the csproj is the truth) ──
        string version = ResolveVersion(cfg, workingDir, out string source);
        string tag = version.StartsWith('v') ? version : "v" + version;
        Log.Cyan($"Release {tag} (version from {source})");

        // ── 2. Lockstep gate — the alpha.13 drift, red-built ──
        foreach (var rel in cfg.ReleaseLockstepFiles)
        {
            string full = Path.Combine(workingDir, rel);
            if (!File.Exists(full))
                throw new GscriptException($"lockstep file missing: {rel}");
            if (!File.ReadAllText(full).Contains(version.TrimStart('v'), StringComparison.Ordinal))
                throw new GscriptException(
                    $"LOCKSTEP: {rel} does not contain \"{version.TrimStart('v')}\". The version must agree with "
                    + "itself everywhere it is stated before it becomes a tag (the alpha.13 drift, refused mechanically).");
            Log.DarkGray($"  lockstep OK: {rel}");
        }

        // ── 3. Git preconditions ──
        GitRunner.ClearStaleGitLocks(gitDir);
        string token = CredentialResolver.ResolvePushToken(cfg); // a tag push is a write
        string url = $"https://x-access-token:{token}@github.com/{cfg.RepoOwner}/{cfg.RepoName}.git";

        var fetch = GitRunner.InvokeGitWithRetry(
            new[] { "fetch", "--quiet", url, "main" }, workingDir, gitDir, context: "fetch");
        if (fetch.Success)
        {
            string ahead = GitCommand.Run(new[] { "rev-list", "--count", "FETCH_HEAD..HEAD" }, workingDir).Stdout.Trim();
            if (ahead != "0" && ahead.Length > 0)
                throw new GscriptException(
                    $"local main has {ahead} commit(s) origin does not — release what is SHIPPED. "
                    + "Push first (gscript push / the task bus), then release.");
        }

        string sha = GitCommand.Run(new[] { "rev-parse", "HEAD" }, workingDir).Stdout.Trim();
        string shortSha = sha.Length >= 7 ? sha[..7] : sha;

        if (cfg.DryRun)
        {
            Log.Green($"DRY RUN: would tag {shortSha} as {tag} and push it. Nothing changed.");
            return true;
        }

        // ── 4. Tag + push the tag ──
        var mktag = GitCommand.Run(new[] { "tag", tag }, workingDir);
        if (!mktag.Success)
            throw new GscriptException(
                $"git tag {tag} refused: {mktag.Stderr.Trim()} (an existing tag means this version already "
                + "released — bump the version, don't move the tag).");

        var push = GitRunner.InvokeGitWithRetry(
            new[] { "push", url, $"refs/tags/{tag}" }, workingDir, gitDir, context: "tag push");
        if (!push.Success)
        {
            GitCommand.Run(new[] { "tag", "-d", tag }, workingDir); // local tag is ours to clean; the remote never saw it
            throw new GscriptException("tag push failed after retries — local tag removed; nothing released. See the log above.");
        }

        Log.Green($"RELEASED: {tag} at {shortSha}.");

        // ── 5. Publish-workflow report — bounded, never gated ──
        GithubCiWatch.ReportSecondary(
            cfg.RepoOwner!, cfg.RepoName!, cfg.ReleaseWorkflowFile, sha, token,
            cfg.CiSecondaryMaxSeconds);

        return true;
    }

    private static string ResolveVersion(GscriptConfig cfg, string workingDir, out string source)
    {
        if (!string.IsNullOrWhiteSpace(cfg.ReleaseTag))
        {
            source = "--tag";
            return cfg.ReleaseTag!.Trim();
        }

        if (string.IsNullOrWhiteSpace(cfg.ReleaseProjectFile))
            throw new GscriptException(
                "release needs a version: pass --tag vX.Y.Z, or set releaseProjectFile in gscript.json "
                + "to the csproj whose <Version> names the release.");

        string proj = Path.Combine(workingDir, cfg.ReleaseProjectFile);
        if (!File.Exists(proj))
            throw new GscriptException($"releaseProjectFile not found: {cfg.ReleaseProjectFile}");

        var m = Regex.Match(File.ReadAllText(proj), @"<Version>\s*([^<\s]+)\s*</Version>");
        if (!m.Success)
            throw new GscriptException($"no <Version> element in {cfg.ReleaseProjectFile}");

        source = cfg.ReleaseProjectFile;
        return m.Groups[1].Value;
    }
}
