using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Gscript.Credentials;

/// <summary>Outcome of a <see cref="TpmCertSource"/> probe. All diagnostics, no key material.</summary>
/// <param name="Found">A cert with the configured subject was located in CurrentUser\My.</param>
/// <param name="Subject">The subject DN actually matched (may differ in spacing from config).</param>
/// <param name="Thumbprint">Per-machine and non-secret — useful for matching against the App's key list.</param>
/// <param name="ProviderName">CNG provider backing the private key. MUST be the platform (TPM) provider.</param>
/// <param name="TpmBacked">True when <paramref name="ProviderName"/> is the Microsoft Platform Crypto Provider.</param>
/// <param name="SignVerified">A SHA256/Pkcs1 sign round-tripped and verified against the cert's public key.</param>
/// <param name="Exportable">True means the private key could be exported — the seal FAILED and must be redone.</param>
public sealed record TpmProbeResult(
    bool Found, string? Subject, string? Thumbprint, string? ProviderName,
    bool TpmBacked, bool SignVerified, bool Exportable);

/// <summary>
/// Locates the GitHub App signing key as a non-exportable TPM-resident RSA key, held as a
/// certificate in <c>Cert:\CurrentUser\My</c> and found by SUBJECT rather than thumbprint.
///
/// <para><b>Why subject, not thumbprint:</b> every writer seat generates and seals its OWN key, so
/// thumbprints differ per machine. Subject-based lookup means one <c>gscript.json</c> works
/// identically on every seat with nothing per-machine to track (docs/CREDENTIAL-SOURCE.md
/// Part 2 § "Lookup by subject, not thumbprint").</para>
///
/// <para><b>This is a KEY source, not an <see cref="ICredentialSource"/>.</b> It signs; it does not
/// produce a push token. The App-JWT-then-installation-token minting that turns a signature into a
/// credential is Phase B. Keeping the two apart means the TPM seal can be verified — via
/// <c>gscript cred test</c> — BEFORE any minting code exists to blame.</para>
///
/// <para>Windows-only by nature: the platform crypto provider is a Windows CNG concept. Callers
/// must gate on <see cref="OperatingSystem.IsWindows"/>; the probe returns a clean not-found rather
/// than throwing on other hosts, so a Linux CI build that merely constructs this type stays green.</para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TpmCertSource
{
    /// <summary>The CNG provider name that means "the private key lives in the TPM".</summary>
    public const string PlatformCryptoProvider = "Microsoft Platform Crypto Provider";

    private readonly string _subject;

    /// <param name="subject">Full subject DN, e.g. <c>CN=MyApp</c>. Matched case-insensitively
    /// against both the full DN and the simple name, since certutil and the .NET store can
    /// disagree about spacing after commas in a multi-RDN DN.</param>
    public TpmCertSource(string subject)
    {
        if (string.IsNullOrWhiteSpace(subject))
            throw new CredentialSourceException(
                "githubApp.certSubject is empty. Set it in gscript.json (e.g. \"CN=MyApp\") — it is "
                + "the subject of the cert whose TPM-backed private key signs the App JWT.");
        _subject = subject.Trim();
    }

    public string Subject => _subject;

    /// <summary>
    /// Find the cert and report on its key WITHOUT exposing material. Never throws for the
    /// expected failures (absent cert, wrong provider, exportable key) — those are the findings.
    /// </summary>
    public TpmProbeResult Probe()
    {
        if (!OperatingSystem.IsWindows())
            return new TpmProbeResult(false, null, null, null, false, false, false);

        using var cert = FindCertificate();
        if (cert is null)
            return new TpmProbeResult(false, null, null, null, false, false, false);

        using RSA? rsa = cert.GetRSAPrivateKey();
        if (rsa is null)
            return new TpmProbeResult(true, cert.Subject, cert.Thumbprint, null, false, false, false);

        // The provider name is the load-bearing check: a cert whose key is software-backed will
        // sign perfectly happily, so "signing works" alone does NOT prove the seal landed in the
        // TPM. Only the provider distinguishes a sealed key from a plain imported one.
        string? provider = rsa is RSACng cng ? cng.Key.Provider?.Provider : null;
        bool tpmBacked = string.Equals(provider, PlatformCryptoProvider, StringComparison.Ordinal);

        // Exportability is the inverse check: certutil's NoExport should make this FAIL. If the
        // export succeeds, the key can be stolen and the import must be redone. Nothing is
        // retained — we want the boolean, not the bytes.
        bool exportable;
        try { _ = rsa.ExportParameters(true); exportable = true; }
        catch { exportable = false; }

        bool signVerified = false;
        try
        {
            byte[] payload = Encoding.UTF8.GetBytes("gscript cred test — TPM signing probe");
            byte[] sig = rsa.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using RSA? pub = cert.GetRSAPublicKey();
            signVerified = pub is not null
                && pub.VerifyData(payload, sig, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }
        catch (CryptographicException)
        {
            signVerified = false;   // a UI-policy'd key with no interactive session lands here too
        }

        return new TpmProbeResult(
            true, cert.Subject, cert.Thumbprint, provider, tpmBacked, signVerified, exportable);
    }

    /// <summary>
    /// Open the signing key for use (Phase B: signs the App JWT). The caller owns the returned
    /// <see cref="RSA"/> and must dispose it. Throws with a remediation-shaped message rather than
    /// a raw crypto exception, because every failure here has a specific operator action.
    /// </summary>
    public RSA GetSigningKey()
    {
        if (!OperatingSystem.IsWindows())
            throw new CredentialSourceException(
                "The TPM credential source is Windows-only (the platform crypto provider is a "
                + "Windows CNG concept). Use the localmd source on this host.");

        using var cert = FindCertificate()
            ?? throw new CredentialSourceException(
                $"No certificate with subject '{_subject}' in Cert:\\CurrentUser\\My. Seal the App "
                + "private key into this machine's TPM first (see docs/CREDENTIAL-SOURCE.md "
                + "Part 2 § import mechanism), then verify with 'gscript cred test'.");

        RSA rsa = cert.GetRSAPrivateKey()
            ?? throw new CredentialSourceException(
                $"Certificate '{cert.Subject}' has no accessible RSA private key. The import may "
                + "have placed only the public cert — re-run the certutil importPFX step.");

        if (rsa is RSACng cng)
        {
            // CngKey.Provider is nullable, and a null provider is itself a red flag — it means the
            // key is not attributable to a named CNG provider, so it certainly isn't the TPM's.
            // Treat null and wrong-provider as the same refusal rather than letting null pass.
            string? providerName = cng.Key.Provider?.Provider;
            if (!string.Equals(providerName, PlatformCryptoProvider, StringComparison.Ordinal))
            {
                rsa.Dispose();
                throw new CredentialSourceException(
                    $"Certificate '{cert.Subject}' private key is backed by "
                    + $"'{providerName ?? "(no named CNG provider)"}', not '{PlatformCryptoProvider}' — "
                    + "it is NOT in the TPM and could be exfiltrated. Re-import with "
                    + "-csp \"Microsoft Platform Crypto Provider\" and NoExport.");
            }
        }

        return rsa;
    }

    /// <summary>Subject lookup over CurrentUser\My. Returns null when absent — absence is a
    /// finding for <see cref="Probe"/>, not an exception.</summary>
    private X509Certificate2? FindCertificate()
    {
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly);

        // Exact DN first, then simple-name, then a whitespace-insensitive DN compare. certutil and
        // X509Certificate2.Subject do not always agree about the space after a comma in a
        // multi-RDN subject, and a near-miss here reads to the operator as "the seal failed".
        foreach (var c in store.Certificates)
        {
            if (string.Equals(c.Subject, _subject, StringComparison.OrdinalIgnoreCase))
                return new X509Certificate2(c);
        }
        foreach (var c in store.Certificates)
        {
            if (string.Equals(c.GetNameInfo(X509NameType.SimpleName, false), SimpleNameOf(_subject),
                    StringComparison.OrdinalIgnoreCase))
                return new X509Certificate2(c);
        }
        foreach (var c in store.Certificates)
        {
            if (string.Equals(Squash(c.Subject), Squash(_subject), StringComparison.OrdinalIgnoreCase))
                return new X509Certificate2(c);
        }
        return null;
    }

    private static string Squash(string dn) => dn.Replace(" ", "");

    /// <summary>"CN=Foo, O=Bar" -> "Foo". Returns the input unchanged when there is no CN.</summary>
    private static string SimpleNameOf(string dn)
    {
        foreach (var part in dn.Split(','))
        {
            var t = part.Trim();
            if (t.StartsWith("CN=", StringComparison.OrdinalIgnoreCase)) return t[3..].Trim();
        }
        return dn;
    }
}
