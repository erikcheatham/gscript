using System.Text.RegularExpressions;

namespace Gscript.Im;

/// <summary>
/// The <c>gscript im</c> command family — the IM index/linter ("imindex") over the operator's
/// institutional-memory hub. The hub is a single CLAUDE.md whose location derives from the
/// repo's <c>gscript.json</c> localmdPath (hub = same directory, CLAUDE.md), or <c>--hub</c>.
///
///   lint    stale-path scan + line-budget enforcement + broken cross-ref detection.
///           Exit 1 on any error-severity finding, so it can gate a push ceremony.
///   digest  compact generated index: headings, line spans, budget usage, edit stamp.
///
/// Severity model: budget overrun and broken localmd cross-refs are ERRORS (they mean the
/// hub's contract is broken); archived-location references (old pre-relocation roots) are
/// WARNINGS (they may be deliberate, e.g. a TODO naming the old path); paths outside the
/// canonical work root — other machines, ProgramData, placeholders — are UNVERIFIABLE and
/// only listed under --verbose. Existence checks run only on Windows; elsewhere every path
/// is unverifiable by definition (the linter still enforces budget + cross-refs).
/// </summary>
public static class ImCommands
{
    private const int DefaultBudget = 450;
    private const int DefaultWarnPct = 90;

    /// <summary>
    /// Absolute Windows path token in either slash form — backslash (native) or forward slash
    /// (docker-bind form; gotcha #7: fixers must match BOTH). Normalized to backslash before
    /// classification. The negative lookbehind stops URL-tail fakes (`https://…` matching from
    /// its trailing letter as drive `s:`), and `/(?!/)` rejects `X://` — a drive letter is never
    /// followed by a double slash, only URL schemes are.
    /// </summary>
    private static readonly Regex PathToken = new(
        @"(?<![A-Za-z0-9])[A-Za-z]:(?:\\|/(?!/))[^\s""'`\)\]\|,;>]+",
        RegexOptions.Compiled);

    /// <summary>Backtick-quoted hub-relative cross-ref, e.g. `localmd/gotchas.md`.</summary>
    private static readonly Regex LocalmdRef = new(
        @"`(localmd/[A-Za-z0-9._\-]+\.md)`",
        RegexOptions.Compiled);

    public static int Run(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            PrintUsage();
            return args.Length == 0 ? 1 : 0;
        }

        var rest = args[1..];
        return args[0] switch
        {
            "lint" => Lint(rest),
            "digest" => Digest(rest),
            _ => UnknownSub(args[0]),
        };
    }

    // ── lint ──────────────────────────────────────────────────────────────

    private static int Lint(string[] args)
    {
        var (hubPath, budget, warnPct, deep, verbose, cliRoots) = ParseOptions(args);
        if (hubPath is null) return 1;

        var errors = new List<string>();
        var warnings = new List<string>();
        var unverifiable = new List<string>();

        Log.Cyan($"gscript im lint — {hubPath}");

        string[] hubLines;
        try { hubLines = File.ReadAllLines(hubPath); }
        catch (Exception ex) { Log.Red($"  cannot read hub: {ex.Message}"); return 1; }

        // 1. Line budget.
        int count = hubLines.Length;
        int warnAt = budget * warnPct / 100;
        if (count > budget)
            errors.Add($"line budget: {count} lines > {budget} budget — consolidate (move detail to localmd/ or the owning project's docs/)");
        else if (count >= warnAt)
            warnings.Add($"line budget: {count}/{budget} lines ({count * 100 / budget}%) — approaching the ceiling");
        else
            Log.DarkGray($"  budget: {count}/{budget} lines ({count * 100 / budget}%)");

        // Collect scan targets: hub always; localmd/*.md under --deep.
        var targets = new List<(string Label, string[] Lines)> { ("CLAUDE.md", hubLines) };
        string hubDir = Path.GetDirectoryName(Path.GetFullPath(hubPath))!;

        // Archived (pre-relocation) roots are OPERATOR DATA — they name operator paths, so
        // they cannot be hardcoded here or committed in gscript.json (this is a public tree;
        // the leak-check gate rightly refuses them). They load from <hubDir>\im.json —
        // private by construction — plus any --archived-root flags.
        string[] archivedRoots = LoadArchivedRoots(hubDir, cliRoots);
        Log.DarkGray($"  archived roots: {archivedRoots.Length} configured");
        if (deep)
        {
            string localmdDir = Path.Combine(hubDir, "localmd");
            if (Directory.Exists(localmdDir))
                foreach (var f in Directory.EnumerateFiles(localmdDir, "*.md").OrderBy(f => f))
                    targets.Add(($"localmd/{Path.GetFileName(f)}", File.ReadAllLines(f)));
        }

        // 2. Stale-path scan + 3. cross-ref check.
        bool canVerify = OperatingSystem.IsWindows();
        if (!canVerify)
            Log.DarkGray("  (non-Windows host: path existence checks skipped — budget + refs only)");

        foreach (var (label, lines) in targets)
        {
            for (int n = 0; n < lines.Length; n++)
            {
                string line = lines[n];

                foreach (Match m in PathToken.Matches(line))
                {
                    string p = m.Value.Replace('/', '\\').TrimEnd('.', ',', ':', ';', '*');
                    if (p.Contains('<') || p.Contains('{') || p.Contains('*')) continue; // placeholder/glob

                    if (archivedRoots.Any(r => p.StartsWith(r, StringComparison.OrdinalIgnoreCase)))
                    {
                        warnings.Add($"{label}:{n + 1}: archived-location reference: {p}");
                        continue;
                    }

                    bool inWorkRoot = p.StartsWith(@"C:\work\", StringComparison.OrdinalIgnoreCase);
                    if (!inWorkRoot || !canVerify)
                    {
                        unverifiable.Add($"{label}:{n + 1}: {p}");
                        continue;
                    }

                    if (!Path.Exists(p) && !IsSpaceTruncatedPrefix(p))
                        errors.Add($"{label}:{n + 1}: stale path (missing on disk): {p}");
                }

                foreach (Match m in LocalmdRef.Matches(line))
                {
                    string rel = m.Groups[1].Value;
                    if (!File.Exists(Path.Combine(hubDir, rel)))
                        errors.Add($"{label}:{n + 1}: broken cross-ref: `{rel}` not found under {hubDir}");
                }
            }
        }

        // Report.
        foreach (var w in warnings) Log.Yellow($"  WARN  {w}");
        foreach (var e in errors) Log.Red($"  ERROR {e}");
        if (verbose)
            foreach (var u in unverifiable) Log.DarkGray($"  skip  {u}");

        Log.Plain();
        if (errors.Count == 0)
        {
            Log.Green($"  im lint: PASS ({warnings.Count} warning(s), {unverifiable.Count} unverifiable path(s))");
            return 0;
        }
        Log.Red($"  im lint: FAIL — {errors.Count} error(s), {warnings.Count} warning(s)");
        return 1;
    }

    // ── digest ────────────────────────────────────────────────────────────

    private static int Digest(string[] args)
    {
        var (hubPath, budget, _, _, _, _) = ParseOptions(args);
        if (hubPath is null) return 1;

        string[] lines;
        try { lines = File.ReadAllLines(hubPath); }
        catch (Exception ex) { Log.Red($"  cannot read hub: {ex.Message}"); return 1; }

        Log.Cyan($"gscript im digest — {hubPath}");
        Log.DarkGray($"  {lines.Length}/{budget} lines ({lines.Length * 100 / budget}% of budget)");

        // The stamp may wrap: pull continuation lines (indented, not a new bullet/heading)
        // and strip markdown bold markers for clean console output.
        int stampIdx = Array.FindIndex(lines, l => l.Contains("Last substantive edit", StringComparison.OrdinalIgnoreCase));
        if (stampIdx >= 0)
        {
            string stamp = lines[stampIdx];
            for (int k = stampIdx + 1; k < lines.Length; k++)
            {
                string cont = lines[k];
                if (cont.StartsWith("  ") && !cont.TrimStart().StartsWith('-') && !cont.TrimStart().StartsWith('#'))
                    stamp += " " + cont.Trim();
                else break;
            }
            Log.DarkGray($"  {stamp.Replace("**", "").TrimStart('-', ' ').Trim()}");
        }
        Log.Plain();

        // Heading map with line spans + per-section size.
        var headings = new List<(int Line, int Level, string Text)>();
        for (int n = 0; n < lines.Length; n++)
        {
            var m = Regex.Match(lines[n], @"^(#{1,4})\s+(.*)$");
            if (m.Success) headings.Add((n + 1, m.Groups[1].Value.Length, m.Groups[2].Value.Trim()));
        }
        for (int i = 0; i < headings.Count; i++)
        {
            int end = i + 1 < headings.Count ? headings[i + 1].Line - 1 : lines.Length;
            int size = end - headings[i].Line;
            string indent = new(' ', (headings[i].Level - 1) * 2);
            Log.Plain($"  {headings[i].Line,4}  {indent}{headings[i].Text}  ({size})");
        }
        return 0;
    }

    // ── shared plumbing ───────────────────────────────────────────────────

    private static (string? Hub, int Budget, int WarnPct, bool Deep, bool Verbose, List<string> ArchivedRoots) ParseOptions(string[] args)
    {
        string? hub = null, config = null;
        int budget = DefaultBudget, warnPct = DefaultWarnPct;
        bool deep = false, verbose = false;
        var archivedRoots = new List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--hub": hub = Next(args, ref i); break;
                case "--config": config = Next(args, ref i); break;
                case "--budget": budget = int.Parse(Next(args, ref i)); break;
                case "--warn-pct": warnPct = int.Parse(Next(args, ref i)); break;
                case "--archived-root": archivedRoots.Add(Next(args, ref i)); break;
                case "--deep": deep = true; break;
                case "--verbose": verbose = true; break;
                default:
                    Log.Red($"gscript im: unknown option '{args[i]}'");
                    return (null, budget, warnPct, deep, verbose, archivedRoots);
            }
        }

        hub ??= ResolveHubFromConfig(config);
        if (hub is null)
        {
            Log.Red("gscript im: no hub path — pass --hub <path>, or run in a repo whose gscript.json has localmdPath (hub = <that dir>\\CLAUDE.md)");
            return (null, budget, warnPct, deep, verbose, archivedRoots);
        }
        return (hub, budget, warnPct, deep, verbose, archivedRoots);
    }

    /// <summary>
    /// Operator-configured archived roots: <hubDir>\im.json { "archivedRoots": [...] } plus
    /// any --archived-root flags. Missing/unreadable file → no archived-root warnings (the
    /// check degrades to off rather than failing the lint).
    /// </summary>
    private static string[] LoadArchivedRoots(string hubDir, List<string> cliRoots)
    {
        var roots = new List<string>(cliRoots);
        string p = Path.Combine(hubDir, "im.json");
        if (File.Exists(p))
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(p));
                if (doc.RootElement.TryGetProperty("archivedRoots", out var arr))
                    foreach (var e in arr.EnumerateArray())
                        if (e.GetString() is { Length: > 0 } s)
                            roots.Add(s.Replace('/', '\\'));
            }
            catch (Exception ex) { Log.Yellow($"  WARN  im.json unreadable ({ex.Message}) — archived-root check degraded to CLI flags only"); }
        }
        return roots.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>Hub defaults to CLAUDE.md beside the resolved localmd file.</summary>
    private static string? ResolveHubFromConfig(string? configPath)
    {
        string path = configPath ?? Path.Combine(Directory.GetCurrentDirectory(), "gscript.json");
        if (!File.Exists(path)) return null;
        try
        {
            var cfg = GscriptConfig.Load(path);
            string? localmd = cfg.LocalmdPath ?? cfg.PatFile;
            if (localmd is null) return null;
            string? dir = Path.GetDirectoryName(Path.GetFullPath(localmd));
            if (dir is null) return null;
            string hub = Path.Combine(dir, "CLAUDE.md");
            return File.Exists(hub) ? hub : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// The token regex stops at whitespace, so a path with spaces ("…\Chrome for Claude.lnk")
    /// arrives truncated ("…\Chrome") and would false-ERROR. If the parent directory exists and
    /// contains an entry whose name starts with the truncated leaf, treat the token as a
    /// space-truncated prefix of a real path rather than a stale one.
    /// </summary>
    private static bool IsSpaceTruncatedPrefix(string p)
    {
        try
        {
            string? parent = Path.GetDirectoryName(p);
            string leaf = Path.GetFileName(p);
            if (parent is null || leaf.Length == 0 || !Directory.Exists(parent)) return false;
            return Directory.EnumerateFileSystemEntries(parent)
                .Any(e => Path.GetFileName(e).StartsWith(leaf, StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }

    private static string Next(string[] args, ref int i)
    {
        if (i + 1 >= args.Length) throw new GscriptException($"option '{args[i]}' expects a value");
        return args[++i];
    }

    private static int UnknownSub(string sub)
    {
        Log.Red($"gscript im: unknown subcommand '{sub}'. Try 'gscript im lint' or 'gscript im digest'.");
        return 1;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("gscript im — the IM index/linter (imindex) over the operator memory hub");
        Console.WriteLine();
        Console.WriteLine("USAGE:");
        Console.WriteLine("  gscript im lint   [--hub <path>] [--budget N] [--warn-pct N] [--deep] [--verbose]");
        Console.WriteLine("  gscript im digest [--hub <path>] [--budget N]");
        Console.WriteLine();
        Console.WriteLine("OPTIONS:");
        Console.WriteLine("  --hub <path>       the hub CLAUDE.md (default: CLAUDE.md beside gscript.json's localmdPath)");
        Console.WriteLine("  --budget N         hub line budget (default 450); exceeding it is an ERROR");
        Console.WriteLine("  --warn-pct N       warn when usage reaches N% of budget (default 90)");
        Console.WriteLine("  --archived-root R  pre-relocation root to warn on (repeatable; also <hubDir>\\im.json archivedRoots)");
        Console.WriteLine("  --deep             also stale-path-scan localmd/*.md beside the hub");
        Console.WriteLine("  --verbose          list unverifiable paths (non-C:\\work roots, other machines)");
        Console.WriteLine();
        Console.WriteLine("lint exits 1 on any error (budget overrun, stale C:\\work path, broken `localmd/…` ref)");
        Console.WriteLine("so it can gate a ceremony; archived-location references are warnings only.");
    }
}
