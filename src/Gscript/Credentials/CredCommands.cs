
namespace Gscript.Credentials;

/// <summary>
/// <c>gscript cred test</c> — a read-back / sign check over the configured credential machinery.
///
/// <para>There is deliberately NO <c>cred set</c>. Sealing the App key is a certutil importPFX
/// performed out-of-band, once per machine, and it must stay that way: a tool subcommand that
/// accepted key material would have to receive it as an argument or a file, which is exactly the
/// disclosure surface this whole design removes (docs/CREDENTIAL-SOURCE.md Part 2 § Build
/// sequence, Phase A).</para>
///
/// <para>What this command is FOR: the import sequence ends with "verify signing, THEN delete the
/// PEM" — and verify-before-delete is load-bearing, because a lost TPM key is only recoverable by
/// generating a fresh App key. This is that verification, runnable before the plaintext is gone.</para>
/// </summary>
public static class CredCommands
{
    public static int Run(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            PrintUsage();
            return args.Length == 0 ? 1 : 0;
        }
        if (args[0] != "test")
        {
            Log.Red($"gscript cred: unknown subcommand '{args[0]}'. Only 'test' exists (there is no 'set' — see --help).");
            return 1;
        }

        string configPath = "gscript.json";
        string? subjectOverride = null;
        string? localmdOverride = null;
        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--config": configPath = Next(args, ref i); break;
                case "--subject": subjectOverride = Next(args, ref i); break;
                case "--localmd": localmdOverride = Next(args, ref i); break;
                default:
                    Log.Red($"gscript cred test: unknown option '{args[i]}'");
                    return 1;
            }
        }

        GscriptConfig cfg;
        if (File.Exists(configPath))
        {
            cfg = GscriptConfig.Load(configPath);
        }
        else
        {
            Log.Yellow($"  no {configPath} here — testing with defaults (localmd only).");
            cfg = new GscriptConfig();
        }
        if (localmdOverride is not null) cfg.LocalmdPath = localmdOverride;

        int failures = 0;

        // ── 1. the configured order, and whether it even parses ──
        Log.Cyan("Credential source order...");
        var order = (cfg.CredentialSource is { Count: > 0 }) ? cfg.CredentialSource : new List<string> { "localmd" };
        Log.DarkGray($"  {string.Join(" -> ", order)}"
            + (cfg.CredentialSource is { Count: > 0 } ? "" : "   (default — credentialSource unset)"));

        // ── 2. localmd, if it is in the order ──
        if (order.Any(n => string.Equals(n?.Trim(), "localmd", StringComparison.OrdinalIgnoreCase)))
        {
            Log.Cyan("localmd source...");
            var src = new LocalMdSource(cfg.LocalmdPath ?? cfg.PatFile);
            Log.DarkGray($"  {src.Describe()}");
            try
            {
                var token = src.GetPushToken();
                // Length only. The value never reaches stdout, a log, or a variable that outlives this scope.
                Log.Green($"  OK  a PAT resolved ({token.Length} chars)");
            }
            catch (CredentialSourceException ex)
            {
                Log.Red($"  FAIL  {ex.Message}");
                failures++;
            }
        }

        // ── 3. the TPM seal — the reason this command exists ──
        string? subject = subjectOverride ?? cfg.GitHubApp.CertSubject;
        Log.Cyan("TPM signing key (GitHub App)...");
        if (string.IsNullOrWhiteSpace(subject))
        {
            Log.Yellow("  SKIP  githubApp.certSubject is not set in gscript.json (and no --subject given).");
        }
        else if (!OperatingSystem.IsWindows())
        {
            Log.Yellow($"  SKIP  not Windows — the platform crypto provider is Windows-only. Subject would be '{subject}'.");
        }
        else
        {
            failures += ProbeTpm(subject!);
        }

        if (failures > 0)
        {
            Log.Red($"cred test: {failures} check(s) FAILED.");
            return 1;
        }
        Log.Green("cred test: all configured checks passed.");
        return 0;
    }

    /// <summary>Split out so the Windows-only probe sits behind one platform guard.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static int ProbeTpm(string subject)
    {
        var tpm = new TpmCertSource(subject);
        var r = tpm.Probe();

        if (!r.Found)
        {
            Log.Red($"  FAIL  no certificate with subject '{subject}' in Cert:\\CurrentUser\\My.");
            Log.DarkGray("        Seal this machine's App key first (docs/CREDENTIAL-SOURCE.md Part 2).");
            return 1;
        }

        Log.DarkGray($"  subject     {r.Subject}");
        Log.DarkGray($"  thumbprint  {r.Thumbprint}    (per-machine, non-secret)");
        Log.DarkGray($"  provider    {r.ProviderName ?? "(none — no CNG key)"}");

        int bad = 0;

        if (r.TpmBacked)
            Log.Green($"  OK  key is TPM-resident ({TpmCertSource.PlatformCryptoProvider})");
        else
        {
            Log.Red("  FAIL  key is NOT in the TPM. It signs, but it can be exfiltrated — which is the");
            Log.Red("        exact gap the TPM was chosen to close. Re-import with");
            Log.Red($"        -csp \"{TpmCertSource.PlatformCryptoProvider}\" and NoExport.");
            bad++;
        }

        if (r.SignVerified)
            Log.Green("  OK  SHA256/Pkcs1 sign round-tripped and verified against the cert's public key");
        else
        {
            Log.Red("  FAIL  signing did not verify. If the key was sealed with a UI policy it may be");
            Log.Red("        prompting — that is fine for interactive pushes but blocks unattended ones;");
            Log.Red("        reseal without the UI policy if this seat must push unattended.");
            bad++;
        }

        if (r.Exportable)
        {
            Log.Red("  FAIL  the private key EXPORTED successfully — NoExport did not take. Redo the");
            Log.Red("        certutil importPFX with NoExport and re-verify before deleting the PEM.");
            bad++;
        }
        else
            Log.Green("  OK  private key refuses export (non-exportable, as sealed)");

        return bad;
    }

    private static string Next(string[] args, ref int i)
    {
        if (i + 1 >= args.Length) throw new GscriptException($"option {args[i]} needs a value");
        return args[++i];
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            gscript cred test [--config <gscript.json>] [--subject CN=<app>] [--localmd <path>]

              Read-back / sign check over the configured credential machinery. Prints provenance
              and diagnostics ONLY — never a credential value.

              Checks, in order:
                1. the credentialSource order from gscript.json (default: localmd)
                2. localmd  — that a PAT resolves (reports its length, never its value)
                3. TPM      — that githubApp.certSubject names a cert in Cert:\CurrentUser\My
                              whose private key is TPM-resident, can sign, and refuses export

              Exit 1 if any configured check fails, so it can gate a provisioning runbook.

              There is no 'cred set'. Sealing the App key is a one-time out-of-band certutil
              importPFX; a subcommand that accepted key material would reintroduce the very
              disclosure surface this design removes.
            """);
    }
}
