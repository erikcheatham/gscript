namespace Gscript.Gates;

/// <summary>
/// GATE (canonical, always-on) — "the bug that made gscript what it is."
/// Detects trailing <c>0x00</c> bytes appended by FUSE-mount write corruption: sandbox AI
/// agents writing through mount layers occasionally append 1–1143 trailing nulls, and the
/// AI's own Read tool (a different IPC path) never sees the corruption. JSON/YAML parsers
/// reject at the first null. Binary extensions are intentionally excluded — they legitimately
/// end in non-printable bytes.
/// Operator recovery on failure:
/// <c>python -c "import pathlib;p='&lt;file&gt;';pathlib.Path(p).write_bytes(pathlib.Path(p).read_bytes().rstrip(b'\x00'))"</c>
/// </summary>
public static class TrailingNullGate
{
    /// <summary>
    /// Extensions the trailing-null check runs on (compared case-insensitively). This is the
    /// canonical module list, which includes <c>.psm1</c>/<c>.psd1</c> (the older 461-line
    /// template omitted them). Extensionless <c>.gitignore</c> is handled in <see cref="IsTextFile"/>.
    /// </summary>
    public static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".razor", ".css", ".js", ".ts", ".html", ".md", ".json",
        ".yml", ".yaml", ".xml", ".csproj", ".props", ".targets", ".sln",
        ".slnx", ".ps1", ".psm1", ".psd1", ".py", ".sql", ".txt", ".sh",
        ".gitignore", ".editorconfig", ".env", ".rb", ".go", ".rs", ".java",
        ".kt", ".swift", ".c", ".h", ".cpp", ".hpp", ".jsx", ".tsx", ".vue",
        ".svelte", ".toml", ".ini", ".cfg", ".conf",
    };

    /// <summary>
    /// True if the path should be trailing-null-checked: a known text extension, OR the
    /// special-cased extensionless <c>.gitignore</c> (which IS text but has no extension).
    /// </summary>
    public static bool IsTextFile(string path)
    {
        if (string.Equals(Path.GetFileName(path), ".gitignore", StringComparison.OrdinalIgnoreCase))
            return true;
        var ext = Path.GetExtension(path);
        return !string.IsNullOrEmpty(ext) && TextExtensions.Contains(ext);
    }

    /// <summary>Check a single file for trailing <c>0x00</c> bytes. Missing/empty files pass.</summary>
    public static GateResult Check(string fullPath)
    {
        if (!File.Exists(fullPath))
            return GateResult.Pass("file not found (skipped)") with { Count = 0 };

        var bytes = File.ReadAllBytes(fullPath);
        if (bytes.Length == 0)
            return GateResult.Pass("empty file") with { Count = 0 };

        int count = 0;
        for (int i = bytes.Length - 1; i >= 0 && bytes[i] == 0x00; i--)
            count++;

        return count > 0
            ? GateResult.Fail($"{count} trailing 0x00 bytes") with { Count = count }
            : GateResult.Pass("no trailing nulls") with { Count = 0 };
    }
}
