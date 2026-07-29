using Gscript.Local;

namespace Gscript.Credentials;

/// <summary>
/// The historical (and still default) source: a <c>github_pat_…</c> read fresh from the operator's
/// private localmd on every call. Behavior is unchanged from pre-seam gscript — this class is a
/// thin adapter over <see cref="Localmd.ResolvePat"/> so the default path stays byte-identical
/// while the seam lands (docs/CREDENTIAL-SOURCE.md Part 1 §4: non-breaking by construction).
///
/// <para>Its known weakness is the reason the seam exists, and is worth stating at the class that
/// embodies it: the value lives in plaintext at rest inside a folder that AI sessions mount, so
/// merely EDITING the file can disclose it. That is not fixable by careful operation — it is
/// fixable only by the value living somewhere a session cannot read.</para>
/// </summary>
public sealed class LocalMdSource : ICredentialSource
{
    private readonly string? _path;

    public LocalMdSource(string? localmdPath) => _path = localmdPath;

    public string Name => "localmd";

    public string Describe() =>
        $"localmd PAT (fresh read per call) from {_path ?? Localmd.DefaultPath()}";

    public string GetPushToken()
    {
        try
        {
            return Localmd.ResolvePat(_path);
        }
        catch (LocalmdException ex)
        {
            // Re-wrap so the resolver can report uniformly across sources; keep the original
            // message, which already names the split-aware search it attempted.
            throw new CredentialSourceException($"localmd source: {ex.Message}", ex);
        }
    }
}
