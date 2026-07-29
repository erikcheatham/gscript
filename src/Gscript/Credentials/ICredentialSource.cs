namespace Gscript.Credentials;

/// <summary>Thrown when a configured credential source cannot produce a usable credential.</summary>
public sealed class CredentialSourceException : Exception
{
    public CredentialSourceException(string message) : base(message) { }
    public CredentialSourceException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// A source of the git push credential — the value gscript injects as
/// <c>https://x-access-token:&lt;token&gt;@github.com/...</c>. The URL shape does NOT vary by
/// source: <c>x-access-token</c> is a placeholder username for a PAT and the REQUIRED username
/// for a GitHub App installation token, so both ride the same URL (docs/CREDENTIAL-SOURCE.md
/// Part 2).
///
/// <para>Why a seam at all: the credential moved from "a standing secret in a file we read" to
/// "a short-lived token we mint", and those have different failure modes but the same call site
/// (<c>GscriptRunner</c>, one line). The seam keeps that call site ignorant of which it got.</para>
///
/// <para><b>Implementations must never log, echo, or persist the returned value</b> — the whole
/// point of the exercise is that the credential stops appearing in readable places. Use
/// <see cref="Describe"/> for anything human-facing; it returns provenance, never the secret.</para>
/// </summary>
public interface ICredentialSource
{
    /// <summary>Config token that selects this source in <c>gscript.json</c>'s
    /// <c>credentialSource</c> array (e.g. <c>localmd</c>). Lowercase, stable — it is config API.</summary>
    string Name { get; }

    /// <summary>Where this source reads from, safe to print. NEVER the credential itself.</summary>
    string Describe();

    /// <summary>
    /// Produce the push token. Called once per push, immediately before the fetch that embeds it.
    /// Throws <see cref="CredentialSourceException"/> when this source is configured but unusable,
    /// so a misconfiguration is a one-line error rather than a silent fallback to a weaker source.
    /// </summary>
    string GetPushToken();
}
