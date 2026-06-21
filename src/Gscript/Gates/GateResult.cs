namespace Gscript.Gates;

/// <summary>
/// Uniform result for every preflight gate. <see cref="Ok"/> = false means the pipeline
/// refuses to stage/commit (fail-loud). <see cref="Reason"/> is operator-facing. The optional
/// metric properties carry the gate-specific detail the PowerShell hashtables returned
/// (file/line sizes, trailing-null count, leak matches).
/// </summary>
public sealed record GateResult(bool Ok, string Reason)
{
    public long HeadSize { get; init; }
    public long WorkingSize { get; init; }
    public int HeadLines { get; init; }
    public int WorkingLines { get; init; }
    public int Count { get; init; }                       // trailing-null byte count
    public int PatternsScanned { get; init; }             // leak-check
    public IReadOnlyList<LeakMatch> Matches { get; init; } = Array.Empty<LeakMatch>();

    public static GateResult Pass(string reason) => new(true, reason);
    public static GateResult Fail(string reason) => new(false, reason);
}

/// <summary>A single leak-check hit (operator-identifying content found in staged diff).</summary>
public sealed record LeakMatch(string PatternName, string Severity, string File, string Excerpt, string MatchedText);
