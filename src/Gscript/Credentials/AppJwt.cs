using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Gscript.Credentials;

/// <summary>
/// Builds the short-lived RS256 JWT that authenticates gscript AS the GitHub App — the only thing
/// the App private key ever signs. It is not a push credential: it buys exactly one API call, the
/// installation-token mint (<see cref="GitHubAppSource"/>).
///
/// <para>Hand-rolled rather than pulled from a JWT library on purpose. The token is three
/// base64url segments and one signature; a dependency here would be a supply-chain edge on the
/// component that holds the source-writing authority, which is the specific risk this whole design
/// exists to shrink. gscript is dependency-free and stays that way.</para>
/// </summary>
public static class AppJwt
{
    /// <summary>GitHub refuses an App JWT claiming more than 10 minutes. We ask for less.</summary>
    public static readonly TimeSpan MaxLifetime = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Backdate <c>iat</c> to absorb clock skew between this machine and GitHub. Without it, a seat
    /// running slightly fast gets "'iat' is in the future" — which reads like a broken key rather
    /// than a wrong clock, and would send an operator hunting the TPM for a time problem.
    /// </summary>
    private static readonly TimeSpan SkewAllowance = TimeSpan.FromSeconds(60);

    /// <param name="signingKey">The App private key. In production this is the TPM-backed
    /// <see cref="System.Security.Cryptography.RSACng"/> from <see cref="TpmCertSource"/> — the key
    /// material never enters process memory as bytes; only the signature comes back.</param>
    /// <param name="appId">The App ID (non-secret).</param>
    /// <param name="lifetime">Clamped to <see cref="MaxLifetime"/>. Default 9 minutes.</param>
    public static string Create(RSA signingKey, long appId, TimeSpan? lifetime = null)
    {
        if (appId <= 0)
            throw new CredentialSourceException(
                "githubApp.appId is missing or invalid in gscript.json. It is the App ID from the "
                + "App's settings page — non-secret, so it belongs in config.");

        var life = lifetime ?? TimeSpan.FromMinutes(9);
        if (life > MaxLifetime) life = MaxLifetime;

        var now = DateTimeOffset.UtcNow;
        long iat = now.Subtract(SkewAllowance).ToUnixTimeSeconds();
        long exp = now.Add(life).ToUnixTimeSeconds();

        string header = Segment(new { alg = "RS256", typ = "JWT" });
        string payload = Segment(new { iat, exp, iss = appId.ToString() });
        string signingInput = $"{header}.{payload}";

        byte[] signature = signingKey.SignData(
            Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        return $"{signingInput}.{Base64Url(signature)}";
    }

    private static string Segment(object o) =>
        Base64Url(JsonSerializer.SerializeToUtf8Bytes(o));

    /// <summary>base64url: standard base64, '+'/'/' swapped, '=' padding stripped (RFC 7515 §2).</summary>
    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
