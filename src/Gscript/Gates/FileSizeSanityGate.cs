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

        var bytes = File.ReadAllBytes(fullPath);

        // Compare apples-to-apples. git stores LF (autocrlf / .gitattributes), so a CRLF working
        // tree reads N bytes "larger" than its own committed blob purely from the \r's. Measure the
        // LF-normalized size so (a) the reported working size matches what git actually commits, and
        // (b) the shrink % reflects real CONTENT change, not line-ending encoding. (This is the
        // 2026-06-22 lesson: the gate reported "5565" while the committed blob was 5454 — the 111-byte
        // gap was exactly 111 CRLFs, which forced a confusing post-hoc reconciliation.)
        long workingSize = NormalizedLength(bytes);

        // `git cat-file -s HEAD:<relpath>` → byte size of the blob at HEAD (already LF).
        var r = GitCommand.Run(new[] { "cat-file", "-s", $"HEAD:{relativePath}" }, workingDirectory);
        if (!r.Success)
            return GateResult.Pass("new file (not yet in HEAD)") with { HeadSize = 0, WorkingSize = workingSize };

        if (!long.TryParse(r.Stdout.Trim(), out long headSize) || headSize == 0)
            return GateResult.Pass("empty file in HEAD") with { HeadSize = 0, WorkingSize = workingSize };

        double shrinkPct = Math.Round((1 - (double)workingSize / headSize) * 100, 1);

        if (shrinkPct > maxShrinkPct)
        {
            // The gate exists to catch FUSE-truncation (tail silently lopped off). A big shrink is
            // NOT proof of that — a legitimate deletion (e.g. emptying a baseline list) shrinks too.
            // Corroborate structurally: a truncated file ends mid-content and/or carries trailing
            // NULs; a clean deletion ends on a real terminator. Report which it smells like so the
            // operator knows whether --allow-shrink is safe or whether to verify the tail first.
            bool looksTruncated = HasTrailingNulls(bytes) || !EndsCleanly(bytes);
            string diag = looksTruncated
                ? "ends mid-content / has trailing NULs -- LIKELY TRUNCATION; verify the tail before --allow-shrink"
                : $"but ends cleanly on '{LastMeaningfulChar(bytes)}' with no trailing NULs -- likely a legitimate deletion; re-run with --allow-shrink \"{relativePath}\" if intended";

            return GateResult.Fail(
                $"working tree is {shrinkPct}% smaller than HEAD ({workingSize} vs {headSize} bytes, LF-normalized) -- {diag}")
                with { HeadSize = headSize, WorkingSize = workingSize };
        }

        return GateResult.Pass($"size sane ({shrinkPct}% shrinkage, LF-normalized; allowed up to {maxShrinkPct}%)")
            with { HeadSize = headSize, WorkingSize = workingSize };
    }

    /// <summary>Byte length after collapsing CRLF → LF — matches git's stored-blob size for text.</summary>
    private static long NormalizedLength(byte[] bytes)
    {
        long n = 0;
        for (int i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] == (byte)'\r' && i + 1 < bytes.Length && bytes[i + 1] == (byte)'\n') continue; // drop \r before \n
            n++;
        }
        return n;
    }

    private static bool HasTrailingNulls(byte[] bytes) => bytes.Length > 0 && bytes[^1] == 0;

    /// <summary>Last non-whitespace byte as a char, or '\0' if the file is all whitespace/empty.</summary>
    private static char LastMeaningfulChar(byte[] bytes)
    {
        for (int i = bytes.Length - 1; i >= 0; i--)
        {
            byte b = bytes[i];
            if (b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n') continue;
            return (char)b;
        }
        return '\0';
    }

    /// <summary>
    /// True if the last meaningful byte is a plausible clean terminator for source/text — a closing
    /// brace/bracket/paren, semicolon, angle bracket (XML/HTML/Razor), quote, backtick, or sentence
    /// period. A truncated file almost always ends elsewhere (mid-identifier, mid-string, on a comma).
    /// Conservative by design: this only shapes the advisory message, never the fail/pass decision —
    /// the gate still REFUSES on a large shrink, requiring an explicit --allow-shrink.
    /// </summary>
    private static bool EndsCleanly(byte[] bytes)
    {
        char c = LastMeaningfulChar(bytes);
        return c is '}' or ']' or ')' or ';' or '>' or '"' or '\'' or '`' or '.';
    }
}
