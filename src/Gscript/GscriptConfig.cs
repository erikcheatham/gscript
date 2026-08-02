using System.Text.Json;

namespace Gscript;

/// <summary>
/// The <c>gscript.json</c> model. Repo-level fields (owner, gates, probes, visibility) are stable
/// and committed once; sprint-level fields (files, message) are typically supplied per-push on the
/// CLI but may also live in the config. CLI options override config values.
/// Deserialized case-insensitively, so the documented camelCase keys map onto these PascalCase
/// properties; jsonc (comments + trailing commas) is tolerated.
/// </summary>
public sealed class GscriptConfig
{
    // ── repo-level ────────────────────────────────────────────────
    public string? RepoOwner { get; set; }
    public string? RepoName { get; set; }
    public string CiWorkflowFile { get; set; } = "deploy.yml";

    /// <summary>
    /// A SECOND workflow to report on after the push — reported, never gated. Null disables it.
    ///
    /// <para>Exists because of a specific and expensive failure on 2026-07-30: a repo's test
    /// workflow was deliberately non-gating, gscript watched only <see cref="CiWorkflowFile"/>,
    /// and so "CI GREEN" was printed all day while the unit suite had not compiled since morning.
    /// Every claim that the tests passed was false and nothing contradicted it, because nobody is
    /// going to open the Actions tab when the tool just said green. Three defects shipped behind
    /// that impression.</para>
    ///
    /// <para>The fix is visibility rather than gating: a non-gating suite is a deliberate choice
    /// (slow, flaky, or still stabilising), but a suite whose result is never SHOWN is the same as
    /// no suite. Its verdict never changes the exit code.</para>
    /// </summary>
    public string? CiSecondaryWorkflowFile { get; set; }

    /// <summary>How long to wait for the secondary workflow before printing "still running" and
    /// moving on. Short by design: this must never become the reason a push feels slow.</summary>
    public int CiSecondaryMaxSeconds { get; set; } = 240;

    public bool WatchCi { get; set; } = true;
    public int CiWatchMaxMinutes { get; set; } = 15;
    public int CiWatchPollSeconds { get; set; } = 20;
    public string CommitName { get; set; } = "ai-bot";
    public string CommitEmail { get; set; } = "ai-bot@example.com";
    public string? WorkingDirectory { get; set; }
    public string? LocalmdPath { get; set; }
    public string? PatFile { get; set; }   // alias for LocalmdPath (operator-requested alias name); used iff LocalmdPath unset. Resolution: --localmd > localmdPath > patFile > default.
    public string? LogFile { get; set; }    // append-only markdown push-log journal; --log flag overrides. Absent on both => no logging (backward-compatible).
    public bool NoDeployDefault { get; set; }
    public List<ProbeEndpointConfig> ProbeEndpoints { get; set; } = new();

    // ── gates ─────────────────────────────────────────────────────
    public GatesConfig Gates { get; set; } = new();

    // ── leak-check sourcing ───────────────────────────────────────
    public string RepoVisibility { get; set; } = "private";   // public | private
    public bool? LeakCheckRequired { get; set; }              // null = derive from visibility

    // ── sprint-level ──────────────────────────────────────────────
    public List<string> FilesToStage { get; set; } = new();
    public string? CommitMessage { get; set; }
    public bool NoDeploy { get; set; }
    public bool DryRun { get; set; }   // run gates + fetch/divergence, then stop before staging/commit/push
    public Dictionary<string, int> ShrinkageOverrides { get; set; } = new(); // relpath -> maxPct (per-file shrink exemption; CLI --allow-shrink sets 100)
    public int? MaxShrinkPctOverride { get; set; }   // CLI --max-shrink-pct: global shrink-gate relax for this push (wins over per-file + default)

    // ── credential source (2.0.0-alpha.11) ────────────────────────
    /// <summary>Ordered credential sources, e.g. <c>["githubapp", "localmd"]</c>. Empty/absent =
    /// <c>["localmd"]</c>, so existing consumers are unaffected until they opt in. First source that
    /// yields a token wins; a source that is configured and BROKEN fails rather than downgrading.</summary>
    public List<string> CredentialSource { get; set; } = new();

    /// <summary>GitHub App identifiers + TPM cert subject. All three are NON-SECRET by design —
    /// the secret is the private key, which never leaves the machine's TPM.</summary>
    public GitHubAppConfig GitHubApp { get; set; } = new();

    // ── concurrent-work / runner-tree hygiene (2.0.0-alpha.6) ─────
    // ── Release (gscript release, alpha.16) ─────────────────────────────
    public string? ReleaseProjectFile { get; set; }              // csproj whose <Version> names the tag (v<Version>); --tag overrides
    public List<string> ReleaseLockstepFiles { get; set; } = new(); // files that must CONTAIN the version string verbatim (the drift lint, as code)
    public string ReleaseWorkflowFile { get; set; } = "publish.yml"; // tag-triggered workflow to report (bounded, never gated)
    public string? ReleaseTag { get; set; }                      // CLI --tag vX.Y.Z

    public bool NoSync { get; set; }       // CLI --no-sync: disable the pre-push auto-fast-forward when origin advanced DISJOINTLY from FilesToStage. Default false = auto-FF on.
    public bool RequireClean { get; set; } // CLI --require-clean: fail (not just warn) when files OUTSIDE FilesToStage are modified/untracked — the runner-shared-checkout hygiene gate.

    public static GscriptConfig Load(string path)
    {
        if (!File.Exists(path)) throw new GscriptException($"config not found: {path}");
        var json = File.ReadAllText(path);
        var opts = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };
        return JsonSerializer.Deserialize<GscriptConfig>(json, opts)
               ?? throw new GscriptException($"config parse returned null: {path}");
    }
}

public sealed class GatesConfig
{
    public bool TrailingNull { get; set; } = true;
    public GateToggleWithPct FileSizeSanity { get; set; } = new();
    public bool StructuredFile { get; set; } = true;
    public MarkdownGateConfig MarkdownLineCount { get; set; } = new();
    public LeakGateConfig LeakCheck { get; set; } = new();
}

public sealed class GateToggleWithPct
{
    public bool Enabled { get; set; } = true;
    public int MaxShrinkPct { get; set; } = 10;
}

public sealed class MarkdownGateConfig
{
    public bool Enabled { get; set; } = true;
    public int MaxShrinkPct { get; set; } = 50;
    public int MinHeadLines { get; set; } = 100;
}

public sealed class LeakGateConfig
{
    /// <summary>"auto" (on when visibility==public or leakCheckRequired), "true", or "false".</summary>
    public string Enabled { get; set; } = "auto";
}

/// <summary>
/// The non-secret half of the GitHub App credential source. App id and installation id are public
/// identifiers; the signing key is a non-exportable TPM key found by <see cref="CertSubject"/>, so
/// one config works on every writer seat with nothing per-machine to track.
/// </summary>
public sealed class GitHubAppConfig
{
    public long AppId { get; set; }
    public long InstallationId { get; set; }

    /// <summary>Subject DN of the cert whose TPM-backed key signs the App JWT, e.g. <c>CN=MyApp</c>.</summary>
    public string? CertSubject { get; set; }
}

public sealed class ProbeEndpointConfig
{
    public string Url { get; set; } = "";
    public List<int> ExpectedRange { get; set; } = new();   // [min, max] inclusive
}
