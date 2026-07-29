namespace Gscript.Credentials;

/// <summary>
/// Resolves the push credential through an ORDERED list of sources from <c>gscript.json</c>'s
/// <c>credentialSource</c>. First source that yields a token wins.
///
/// <para><b>Default is unchanged behavior.</b> An absent or empty <c>credentialSource</c> resolves
/// to <c>["localmd"]</c>, so every existing consumer behaves identically until its config opts in
/// (docs/CREDENTIAL-SOURCE.md § Rollout: "non-breaking by construction").</para>
///
/// <para><b>Fallback is for absence, not for failure.</b> A source that is simply not configured on
/// this machine falls through to the next; a source that IS configured and BROKEN throws. The
/// distinction matters because the silent-downgrade version of this class is a security bug: a
/// mint failure quietly reverting to the standing PAT would defeat the entire exercise. The only
/// fallback is the last source's failure becoming the reported error.</para>
/// </summary>
public static class CredentialResolver
{
    /// <summary>Names accepted in <c>credentialSource</c>, for error messages and validation.</summary>
    public static readonly string[] KnownNames = { "localmd", "githubapp" };

    /// <summary>Build the ordered source list. Unknown names fail fast — a typo in
    /// <c>credentialSource</c> must not silently degrade to the default.</summary>
    public static IReadOnlyList<ICredentialSource> Build(GscriptConfig cfg)
    {
        var names = (cfg.CredentialSource is { Count: > 0 })
            ? cfg.CredentialSource
            : new List<string> { "localmd" };

        var sources = new List<ICredentialSource>(names.Count);
        foreach (var raw in names)
        {
            var name = (raw ?? string.Empty).Trim().ToLowerInvariant();
            switch (name)
            {
                case "localmd":
                    sources.Add(new LocalMdSource(cfg.LocalmdPath ?? cfg.PatFile));
                    break;

                case "githubapp":
                    // Guard-and-throw so the platform analyzer knows the construction below is
                    // Windows-only. The TPM is reached through the Windows platform crypto provider,
                    // so there is no cross-platform form of this source — a POSIX seat keeps localmd.
                    if (!OperatingSystem.IsWindows())
                        throw new CredentialSourceException(
                            "credentialSource 'githubapp' needs Windows (the TPM is reached via the "
                            + "platform crypto provider). Keep 'localmd' in credentialSource on this host.");
                    sources.Add(new GitHubAppSource(cfg.GitHubApp, cfg.RepoName, cfg.FilesToStage));
                    break;

                default:
                    throw new CredentialSourceException(
                        $"unknown credentialSource '{raw}'. Known: {string.Join(", ", KnownNames)}.");
            }
        }
        return sources;
    }

    /// <summary>
    /// Resolve a push token from the configured order. Throws
    /// <see cref="CredentialSourceException"/> naming every source tried when none yields one, so
    /// the failure says which mechanisms were attempted rather than just "no PAT found".
    /// </summary>
    public static string ResolvePushToken(GscriptConfig cfg)
    {
        var sources = Build(cfg);
        var failures = new List<string>();

        foreach (var source in sources)
        {
            try
            {
                var token = source.GetPushToken();
                if (!string.IsNullOrWhiteSpace(token))
                {
                    Log.DarkGray($"  credential: {source.Name}");   // NAME only — never the value
                    return token;
                }
                failures.Add($"{source.Name}: returned an empty token");
            }
            catch (CredentialSourceException ex)
            {
                failures.Add($"{source.Name}: {ex.Message}");
            }
        }

        throw new CredentialSourceException(
            "no credential source produced a push token. Tried -> "
            + string.Join(" | ", failures));
    }
}
