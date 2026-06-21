using System.Text.RegularExpressions;
using Gscript.Git;

namespace Gscript.Gates;

/// <summary>An operator-configured leak pattern (loaded from localmd; never hardcoded here).</summary>
public sealed record LeakPattern(string Name, string Severity, string Regex);

/// <summary>
/// GATE (ported from a consumer's repo-policy library): scan staged content (<c>git diff --cached</c>) for
/// matches against operator-configured leak patterns — refuse to ship operator-identifying
/// content (Team IDs, machine codenames, usernames, LAN IPs, home paths, vault entry names) into
/// a PUBLIC tree. The caller runs this gate only when the repo classifies as needing a leak check
/// (visibility == public, OR an explicit <c>leakCheckRequired</c> flag). Patterns live in localmd
/// (operator-private); only the MECHANISM lives here — keeping the patterns out of the public tree
/// is itself the leak-defense (abstraction-as-leak-defense).
/// </summary>
public static partial class LeakCheckGate
{
    [GeneratedRegex(@"^\+\+\+ b/(.+)$")]
    private static partial Regex FileHeader();

    [GeneratedRegex(@"^\+[^+]")]
    private static partial Regex AddedLine();

    /// <param name="patterns">operator leak patterns (from localmd). Empty ⇒ pass (no patterns configured).</param>
    /// <param name="workingDirectory">repo root.</param>
    /// <param name="warn">optional sink for non-fatal warnings (a bad pattern regex is logged + skipped, never crashes).</param>
    public static GateResult Check(IReadOnlyList<LeakPattern> patterns, string workingDirectory, Action<string>? warn = null)
    {
        if (patterns is null || patterns.Count == 0)
            return GateResult.Pass("no leak patterns configured") with { PatternsScanned = 0 };

        var diff = GitCommand.Run(new[] { "diff", "--cached" }, workingDirectory);
        if (string.IsNullOrWhiteSpace(diff.Stdout))
            return GateResult.Pass("no staged content to scan") with { PatternsScanned = patterns.Count };

        // Pre-compile once; a bad pattern regex warns + is skipped (never crashes the push).
        var compiled = new List<(LeakPattern p, Regex rx)>();
        foreach (var p in patterns)
        {
            if (p is null || string.IsNullOrEmpty(p.Regex)) continue;
            try { compiled.Add((p, new Regex(p.Regex))); }
            catch (ArgumentException ex) { warn?.Invoke($"leak pattern '{p.Name}' regex failed to compile: {ex.Message}"); }
        }

        var matches = new List<LeakMatch>();
        string currentFile = "";

        foreach (var raw in diff.Stdout.Split('\n'))
        {
            var line = raw.TrimEnd('\r');

            var fh = FileHeader().Match(line);
            if (fh.Success) { currentFile = fh.Groups[1].Value; continue; }

            // Only added/modified lines (single leading '+'); excludes the '+++' header.
            if (!AddedLine().IsMatch(line)) continue;

            string content = line.Substring(1);
            foreach (var (p, rx) in compiled)
            {
                var m = rx.Match(content);
                if (m.Success)
                    matches.Add(new LeakMatch(p.Name, p.Severity, currentFile, content.Trim(), m.Value));
            }
        }

        return matches.Count == 0
            ? GateResult.Pass($"clean ({patterns.Count} pattern(s) scanned)")
                with { PatternsScanned = patterns.Count, Matches = matches }
            : GateResult.Fail($"{matches.Count} leak match(es) in staged content")
                with { PatternsScanned = patterns.Count, Matches = matches };
    }
}
