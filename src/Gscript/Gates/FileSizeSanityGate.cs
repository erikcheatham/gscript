using Gscript.Git;

namespace Gscript.Gates;

/// <summary>
/// GATE (drifted from a downstream consumer fork): refuse to push a file whose working-tree byte size
/// shrank more than <paramref name="maxShrinkPct"/> below its HEAD blob size — catches
/// FUSE-truncation that silently lops off a file's tail (sister of the trailing-null gate).
/// Growth is ALWAYS allowed; new files (not in HEAD) pass — there is no baseline to compare.
/// </summary>
public static class FileSizeSanityGate
{
    public const int DefaultMaxShrinkPct = 10;

    /// <param name="relativePath">repo-relative path, FORWARD-SLASHED (the caller normalizes
    /// <c>\</c> → <c>/</c> before calling, for the <c>HEAD:&lt;path&gt;</c> git revision syntax).</param>
    /// <param name="fullPath">absolute path on disk.</param>
    /// <param name="workingDirectory">repo root (cwd for the git child).</param>
    public static GateResult Check(string relativePath, string fullPath, string workingDirectory,
        int maxShrinkPct = DefaultMaxShrinkPct)
    {
        if (!File.Exists(fullPath))
            return GateResult.Fail("file not found on disk") with { HeadSize = 0, WorkingSize = 0 };

        long workingSize = new FileInfo(fullPath).Length;

        // `git cat-file -s HEAD:<relpath>` → byte size of the blob at HEAD.
        var r = GitCommand.Run(new[] { "cat-file", "-s", $"HEAD:{relativePath}" }, workingDirectory);
        if (!r.Success)
            return GateResult.Pass("new file (not yet in HEAD)") with { HeadSize = 0, WorkingSize = workingSize };

        if (!long.TryParse(r.Stdout.Trim(), out long headSize) || headSize == 0)
            return GateResult.Pass("empty file in HEAD") with { HeadSize = 0, WorkingSize = workingSize };

        double shrinkPct = Math.Round((1 - (double)workingSize / headSize) * 100, 1);

        if (shrinkPct > maxShrinkPct)
            return GateResult.Fail(
                $"working tree is {shrinkPct}% smaller than HEAD ({workingSize} vs {headSize} bytes) -- possible FUSE-truncation; refusing to push")
                with { HeadSize = headSize, WorkingSize = workingSize };

        return GateResult.Pass($"size sane ({shrinkPct}% shrinkage; allowed up to {maxShrinkPct}%)")
            with { HeadSize = headSize, WorkingSize = workingSize };
    }
}
