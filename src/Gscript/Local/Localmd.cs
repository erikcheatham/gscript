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
    /// no PAT can be found anywhere under the localmd root.
    /// </summary>
    /// <remarks>
    /// SPLIT-AWARE discovery (2026-06-22): localmd was split from a single <c>local.md</c> into
    /// topic files under a <c>localmd/</c> subdir, and the PAT now lives in <c>localmd/githubPAT.md</c>.
    /// So <c>--localmd</c> pointed at the old <c>local.md</c>, at the <c>private/</c> root, at the
    /// <c>localmd/</c> dir, OR directly at <c>githubPAT.md</c> all resolve: we honor the given path
    /// first, then search the localmd root (incl. the <c>localmd/</c> subdir, <c>githubPAT.md</c>
    /// preferred) for the first <c>github_pat_...</c> match. Single-PAT, first-match — the operator
    /// uses one PAT across all repos; multi-account resolve-by-repo-owner stays deferred.
    /// </remarks>
    public static string ResolvePat(string? path = null)
    {
        path ??= DefaultPath();

        var searched = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in LocalmdFiles(path))
        {
            var full = Path.GetFullPath(candidate);
            if (!seen.Add(full)) continue;
            searched.Add(full);

            var m = PatRegex().Match(File.ReadAllText(full));
            if (m.Success) return m.Groups[1].Value;
        }

        if (searched.Count == 0)
            throw new LocalmdException(
                $"localmd not found at '{path}' and no localmd/ split discovered nearby. " +
                "See https://github.com/erikcheatham/gscript/blob/main/docs/LOCALMD.md");

        throw new LocalmdException(
            $"no 'github_pat_...' match found. Searched {searched.Count} file(s): {string.Join(", ", searched)}. " +
            "See https://github.com/erikcheatham/gscript/blob/main/docs/PAT-SETUP.md");
    }

    /// <summary>
    /// Ordered candidate files to scan for localmd content (PAT, leak patterns), tolerant of the
    /// 2026-06-22 single-file → <c>localmd/</c>-split migration. Given a file, the file itself is
    /// yielded first, then its directory becomes a localmd root; given a directory, it IS the root;
    /// given a non-existent path, its parent directory is the root (handles a stale <c>local.md</c>
    /// path post-split). For each root, in priority order: <c>localmd/githubPAT.md</c>,
    /// <c>githubPAT.md</c>, <c>local.md</c>, then every remaining <c>*.md</c> under <c>localmd/</c>
    /// and the root (name-sorted = deterministic). The caller de-dupes.
    /// </summary>
    private static IEnumerable<string> LocalmdFiles(string path)
    {
        var roots = new List<string>();

        if (File.Exists(path))
        {
            yield return path;                                  // honor the explicit file first
            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (dir is not null) roots.Add(dir);
        }
        else if (Directory.Exists(path))
        {
            roots.Add(Path.GetFullPath(path));
        }
        else
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(path));   // stale path (e.g. local.md post-split)
            if (dir is not null && Directory.Exists(dir)) roots.Add(dir);
        }

        foreach (var root in roots)
        {
            foreach (var rel in new[]
                     {
                         Path.Combine("localmd", "githubPAT.md"),
                         "githubPAT.md",
                         "local.md",
                     })
            {
                var p = Path.Combine(root, rel);
                if (File.Exists(p)) yield return p;
            }

            foreach (var sub in new[] { Path.Combine(root, "localmd"), root })
            {
                if (!Directory.Exists(sub)) continue;
                foreach (var f in Directory.EnumerateFiles(sub, "*.md")
                                            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                    yield return f;
            }
        }
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

        // SPLIT-AWARE (2026-06-22): the `## Leak patterns` section may live in ANY localmd file
        // post-split (a topic file, not local.md). Scan the same candidates the PAT uses; first
        // file carrying the section wins. Without this the section silently disappears after the
        // split and leak-check passes VACUOUSLY on public repos — a real security regression.
        Match? m = null;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in LocalmdFiles(path))
        {
            var full = Path.GetFullPath(candidate);
            if (!seen.Add(full)) continue;
            var hit = LeakSection.Match(File.ReadAllText(full));
            if (hit.Success) { m = hit; break; }
        }
        if (m is null) return _leakCache = Array.Empty<LeakPattern>();

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
