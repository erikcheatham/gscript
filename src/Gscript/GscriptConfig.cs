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

    // ── concurrent-work / runner-tree hygiene (2.0.0-alpha.6) ─────
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

public sealed class ProbeEndpointConfig
{
    public string Url { get; set; } = "";
    public List<int> ExpectedRange { get; set; } = new();   // [min, max] inclusive
}
