using System.Net.Http.Headers;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Gscript.Credentials;

/// <summary>
/// Mints a short-lived GitHub App **installation token** and hands it over as the push credential.
///
/// <para>The custody inversion this class exists for: the standing secret is no longer a pushable
/// PAT sitting in a readable file, it is an App private key sealed in this machine's TPM that does
/// exactly one thing — sign a ≤10-minute App JWT. What reaches the wire is a ~1-hour installation
/// token, minted on demand, scoped down to a single repository and the narrowest permissions this
/// particular push needs (docs/CREDENTIAL-SOURCE.md Part 2).</para>
///
/// <para><b>Least privilege per push, not per install.</b> The App may hold Contents+Workflows R/W
/// across every repo, but each mint asks for one repo and for <c>workflows: write</c> only when the
/// staged files actually include something under <c>.github/workflows/</c>. So the ordinary push
/// carries a token that literally cannot rewrite a workflow — which matters, because a workflow
/// file is the one thing a compromised push could use to escalate into the runner.</para>
///
/// <para><b>The token is never persisted, logged, or length-validated.</b> Not persisted because
/// its whole value is being ephemeral; not logged because <c>GitRunner.Redact</c> should never have
/// to be the last line of defense; not length-validated because GitHub is moving installation
/// tokens to a longer stateless <c>ghs_</c> format and has warned that code with hardcoded length
/// assumptions will break. We treat it as an opaque string of unknown length, deliberately.</para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class GitHubAppSource : ICredentialSource
{
    private readonly long _appId;
    private readonly long _installationId;
    private readonly string _certSubject;
    private readonly string? _repoName;
    private readonly IReadOnlyList<string> _stagedFiles;

    public GitHubAppSource(GitHubAppConfig app, string? repoName, IReadOnlyList<string>? stagedFiles)
    {
        if (app.AppId <= 0)
            throw new CredentialSourceException(
                "githubApp.appId is not set in gscript.json (non-secret — it is the App ID from the App's settings page).");
        if (app.InstallationId <= 0)
            throw new CredentialSourceException(
                "githubApp.installationId is not set in gscript.json (non-secret — the numeric id in the App's install URL).");

        _appId = app.AppId;
        _installationId = app.InstallationId;
        _certSubject = app.CertSubject
            ?? throw new CredentialSourceException(
                "githubApp.certSubject is not set in gscript.json — the subject of the cert whose "
                + "TPM-backed private key signs the App JWT (e.g. \"CN=MyApp\").");
        _repoName = repoName;
        _stagedFiles = stagedFiles ?? Array.Empty<string>();
    }

    public string Name => "githubapp";

    public string Describe() =>
        $"GitHub App {_appId} / installation {_installationId}, JWT signed by the TPM key at "
        + $"cert subject '{_certSubject}'; mints a ~1h installation token scoped to "
        + $"{(_repoName is null ? "the installation's repos" : _repoName)} + {DescribePermissions()}";

    public string GetPushToken()
    {
        if (!OperatingSystem.IsWindows())
            throw new CredentialSourceException(
                "the githubapp source needs the Windows platform crypto provider (TPM). Use localmd on this host.");

        // The signing key is opened, used, and released inside this scope. Nothing about it outlives
        // the mint, and the private material never existed here as bytes to begin with.
        string jwt;
        using (RSA signingKey = new TpmCertSource(_certSubject).GetSigningKey())
        {
            jwt = AppJwt.Create(signingKey, _appId);
        }

        return MintInstallationToken(jwt);
    }

    private string MintInstallationToken(string appJwt)
    {
        var url = $"https://api.github.com/app/installations/{_installationId}/access_tokens";

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", appJwt);
        // GitHub's API requires a User-Agent and answers 403 without one on some paths.
        client.DefaultRequestHeaders.UserAgent.ParseAdd("gscript");

        string body = BuildScopeBody();
        using var content = new StringContent(body, Encoding.UTF8, "application/json");

        HttpResponseMessage resp;
        try
        {
            resp = client.PostAsync(url, content).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            throw new CredentialSourceException(
                $"installation-token mint could not reach {url}: {ex.Message}", ex);
        }

        using (resp)
        {
            string text = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();

            if (!resp.IsSuccessStatusCode)
            {
                // The failure body carries GitHub's reason and never a token. Each status has a
                // distinct operator action, so name it rather than dumping a bare code.
                string hint = (int)resp.StatusCode switch
                {
                    401 => "the App JWT was rejected — check githubApp.appId, and that this machine's "
                           + "TPM key is one of the App's ACTIVE private keys (a deleted key fails here). "
                           + "A skewed system clock also lands on 401.",
                    404 => "installation not found — check githubApp.installationId, and that the App is "
                           + "still installed on this repo.",
                    422 => "the requested scope was refused — the App installation may not grant a "
                           + $"permission this push asked for ({DescribePermissions()}), or the repo name "
                           + "does not match an installed repo.",
                    _ => "see the response body above.",
                };
                throw new CredentialSourceException(
                    $"installation-token mint failed: HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}. "
                    + $"{hint} GitHub said: {Truncate(text, 400)}");
            }

            using var doc = JsonDocument.Parse(text);
            if (!doc.RootElement.TryGetProperty("token", out var tokenEl)
                || tokenEl.GetString() is not { Length: > 0 } token)
            {
                throw new CredentialSourceException(
                    "installation-token mint returned 2xx with no 'token' field — unexpected response shape.");
            }

            // Expiry is logged because it is operationally useful and carries no secret. If a push
            // ever outlives it, this line is what explains the mid-push 401.
            if (doc.RootElement.TryGetProperty("expires_at", out var expEl)
                && expEl.GetString() is { Length: > 0 } expires)
            {
                Log.DarkGray($"  minted installation token, expires {expires}");
            }

            return token;
        }
    }

    /// <summary>
    /// Scope the mint to this repo and to the narrowest permissions the staged files require.
    /// Omitting the body entirely would yield a token with the installation's FULL reach across
    /// every installed repo — correct-but-lazy, and the opposite of the design's intent.
    /// </summary>
    private string BuildScopeBody()
    {
        var permissions = new Dictionary<string, string> { ["contents"] = "write" };
        if (NeedsWorkflowScope()) permissions["workflows"] = "write";

        object payload = _repoName is { Length: > 0 }
            ? new { repositories = new[] { _repoName }, permissions }
            : new { permissions };

        return JsonSerializer.Serialize(payload);
    }

    /// <summary>True when any staged path lives under <c>.github/workflows/</c> (either slash form —
    /// gscript takes repo-relative paths from a Windows CLI, so both shapes arrive in practice).</summary>
    private bool NeedsWorkflowScope() =>
        _stagedFiles.Any(f =>
            f.Replace('\\', '/').TrimStart('.', '/')
             .StartsWith("github/workflows/", StringComparison.OrdinalIgnoreCase));

    private string DescribePermissions() =>
        NeedsWorkflowScope() ? "contents:write + workflows:write" : "contents:write";

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";
}
