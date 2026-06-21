using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Gscript.Gates;

namespace Gscript.Local;

/// <summary>Thrown when localmd is missing or doesn't contain a required value (e.g. the PAT).</summary>
public sealed class LocalmdException : Exception
{
    public LocalmdException(string message) : base(message) { }
}

/// <summary>
/// Reads the operator's private <c>local.md</c> memo — the source of truth for the GitHub PAT and
/// the leak-check patterns. Kept OUT of every source tree (operator-private), so the tool reads
/// fresh from disk rather than baking secrets/patterns into config. The PAT is read fresh on every
/// call (rotation = edit the file — the GOTCHAS #3 env-var-staleness defense); leak patterns are
/// cached for the process lifetime.
/// </summary>
public static partial class Localmd
{
    // GitHub fine-grained PAT: literal prefix + 40-or-more [A-Za-z0-9_]. First match wins.
    [GeneratedRegex(@"(github_pat_[A-Za-z0-9_]{40,})")]
    private static partial Regex PatRegex();

    // The `## Leak patterns` heading, then the next ```json ... ``` fenced block. Multiline so
    // ^/$ are per-line; [\s\S]*? spans newlines non-greedily to the nearest fence.
    private static readonly Regex LeakSection = new(
        @"^##\s+Leak patterns\s*$[\s\S]*?```json\s*([\s\S]*?)```",
        RegexOptions.Multiline);

    private static IReadOnlyList<LeakPattern>? _leakCache;

    /// <summary>Default localmd path: <c>%USERPROFILE%\private\local.md</c> (Windows) or <c>$HOME/private/local.md</c> (POSIX).</summary>
    public static string DefaultPath()
    {
        var userProfile = Environment.GetEnvironmentVariable("USERPROFILE");
        if (!string.IsNullOrEmpty(userProfile))
            return Path.Combine(userProfile, "private", "local.md");

        var home = Environment.GetEnvironmentVariable("HOME")
                   ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, "private", "local.md");
    }

    /// <summary>
    /// Resolve the GitHub PAT (fresh read every call). Throws <see cref="LocalmdException"/> if
    /// localmd or the PAT is missing.
    /// </summary>
    /// <remarks>
    /// FAITHFUL first-match behavior (the PS limitation). The multi-account improvement
    /// (resolve-by-repo-owner) is deferred until the localmd multi-PAT layout is pinned, so we
    /// don't invent a convention the operator's file doesn't use.
    /// </remarks>
    public static string ResolvePat(string? path = null)
    {
        path ??= DefaultPath();
        if (!File.Exists(path))
            throw new LocalmdException($"localmd not found at {path}. See https://github.com/erikcheatham/gscript/blob/main/docs/LOCALMD.md");

        var content = File.ReadAllText(path);
        var m = PatRegex().Match(content);
        if (!m.Success)
            throw new LocalmdException($"no 'github_pat_...' match found in {path}. See https://github.com/erikcheatham/gscript/blob/main/docs/PAT-SETUP.md");

        return m.Groups[1].Value;
    }

    /// <summary>
    /// Load leak patterns from the localmd <c>## Leak patterns</c> section (a ```json fenced block:
    /// <c>{ "leak_patterns": [ { "name", "regex", "severity" } ] }</c>). Cached. Returns empty on
    /// missing file / missing section / parse failure (fail-OPEN — the caller's repo-visibility
    /// gating decides whether a repo actually REQUIRES patterns).
    /// </summary>
    public static IReadOnlyList<LeakPattern> LeakPatterns(string? path = null, Action<string>? warn = null)
    {
        if (_leakCache is not null) return _leakCache;

        path ??= DefaultPath();
        if (!File.Exists(path)) return _leakCache = Array.Empty<LeakPattern>();

        var content = File.ReadAllText(path);
        var m = LeakSection.Match(content);
        if (!m.Success) return _leakCache = Array.Empty<LeakPattern>();

        try
        {
            var doc = JsonSerializer.Deserialize<LeakDoc>(
                m.Groups[1].Value.Trim(),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            var list = new List<LeakPattern>();
            foreach (var p in doc?.LeakPatterns ?? new List<RawPattern>())
            {
                if (string.IsNullOrEmpty(p.Regex)) continue;
                list.Add(new LeakPattern(p.Name ?? "(unnamed)", p.Severity ?? "medium", p.Regex));
            }
            return _leakCache = list;
        }
        catch (JsonException ex)
        {
            warn?.Invoke($"localmd Leak patterns section JSON failed to parse: {ex.Message}");
            return _leakCache = Array.Empty<LeakPattern>();
        }
    }

    /// <summary>Reset the leak-pattern cache (test seam).</summary>
    public static void ClearCache() => _leakCache = null;

    private sealed class LeakDoc
    {
        [JsonPropertyName("leak_patterns")] public List<RawPattern>? LeakPatterns { get; set; }
    }

    private sealed class RawPattern
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("regex")] public string? Regex { get; set; }
        [JsonPropertyName("severity")] public string? Severity { get; set; }
    }
}
