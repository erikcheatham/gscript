using System.Text.RegularExpressions;
using Gscript.Git;

namespace Gscript.Gates;

/// <summary>
/// GATE (drifted from a downstream consumer fork): refuse to push a <c>.md</c> file whose line count
/// shrank more than <paramref name="maxShrinkPct"/> below HEAD — catches FUSE-truncation of
/// large markdown (IM / docs) where byte-shrinkage alone might not trip (a truncated-but-still-
/// large file). Only applies to HEAD files of at least <paramref name="minHeadLines"/> lines;
/// smaller markdown is exempt. New files pass.
/// </summary>
public static partial class MarkdownLineCountGate
{
    public const int DefaultMaxShrinkPct = 50;
    public const int DefaultMinHeadLines = 100;

    [GeneratedRegex(@"\r?\n")]
    private static partial Regex LineSplit();

    /// <param name="relativePath">repo-relative path, FORWARD-SLASHED (for <c>HEAD:&lt;path&gt;</c>).</param>
    public static GateResult Check(string relativePath, string fullPath, string workingDirectory,
        int maxShrinkPct = DefaultMaxShrinkPct, int minHeadLines = DefaultMinHeadLines)
    {
        if (!File.Exists(fullPath))
            return GateResult.Fail("missing") with { HeadLines = 0, WorkingLines = 0 };

        if (!string.Equals(Path.GetExtension(fullPath), ".md", StringComparison.OrdinalIgnoreCase))
            return GateResult.Pass("not markdown");

        var r = GitCommand.Run(new[] { "show", $"HEAD:{relativePath}" }, workingDirectory);
        if (!r.Success || string.IsNullOrEmpty(r.Stdout))
            return GateResult.Pass("new markdown (not yet in HEAD)");

        int headLineCount = LineSplit().Split(r.Stdout).Length;
        if (headLineCount < minHeadLines)
            return GateResult.Pass($"small markdown (HEAD={headLineCount} lines)")
                with { HeadLines = headLineCount };

        string workingContent = File.ReadAllText(fullPath);
        int workingLineCount = LineSplit().Split(workingContent).Length;

        double shrinkPct = Math.Round((1 - (double)workingLineCount / headLineCount) * 100, 1);

        if (shrinkPct > maxShrinkPct)
            return GateResult.Fail(
                $"working tree is {shrinkPct}% fewer lines than HEAD ({workingLineCount} vs {headLineCount} lines) -- possible FUSE-truncation; refusing to push")
                with { HeadLines = headLineCount, WorkingLines = workingLineCount };

        return GateResult.Pass($"line count sane ({shrinkPct}% shrinkage; allowed up to {maxShrinkPct}%)")
            with { HeadLines = headLineCount, WorkingLines = workingLineCount };
    }
}
