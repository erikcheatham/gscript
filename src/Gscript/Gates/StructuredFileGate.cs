using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Gscript.Gates;

/// <summary>
/// GATE (drifted from a downstream consumer fork): parse-validate structured files and regex-scan
/// Razor <c>&lt;style&gt;</c> CSS comments for Razor-parser-breaking tokens — refuse to push
/// malformed content.
/// <para>
/// Native validation for JSON / XML-family / YAML(heuristic) / Razor. <c>.ps1</c> is linted via
/// a graceful shell-out to <c>pwsh</c> when present, and SKIPPED (pass) when absent — the C# tool
/// intentionally carries no PowerShell SDK dependency (per the "evolve past PowerShell"
/// direction), and the set of <c>.ps1</c> files trends toward zero as gscript itself migrates off
/// PowerShell.
/// </para>
/// </summary>
public static partial class StructuredFileGate
{
    public const int YamlTruncationThresholdBytes = 500;
    public const int MaxErrorsShown = 3;

    [GeneratedRegex(@"<style[^>]*>(.*?)</style>", RegexOptions.Singleline)]
    private static partial Regex StyleBlock();

    [GeneratedRegex(@"/\*(.*?)\*/", RegexOptions.Singleline)]
    private static partial Regex CssComment();

    // '<' + (ws+letter | ws+'=' | ws+'/');  Unicode arrows;  '@'-prefixed Razor directives.
    [GeneratedRegex(@"<(?:\s*[a-zA-Z][a-zA-Z0-9]*|\s*=|\s*/)|→|⇒|←|↑|↓|@[a-zA-Z][a-zA-Z]*")]
    private static partial Regex RazorOffender();

    public static GateResult Check(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".ps1" => CheckPs1(path),
            ".json" => CheckJson(path),
            ".xml" or ".csproj" or ".slnx" or ".props" or ".targets" => CheckXml(path),
            ".yaml" or ".yml" => CheckYaml(path),
            ".razor" => CheckRazor(path),
            _ => GateResult.Pass($"no structural check for {ext}"),
        };
    }

    private static GateResult CheckJson(string path)
    {
        string text = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(text)) return GateResult.Fail("JSON empty/whitespace");
        try
        {
            // Lenient (comments + trailing commas) so the tool's own jsonc configs don't
            // false-fail; truncation/corruption is what this gate is really catching, and the
            // trailing-null + size gates cover the mid-file-truncation case.
            using var _ = JsonDocument.Parse(text,
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (JsonException ex) { return GateResult.Fail($"JSON parse failed: {ex.Message}"); }
        return GateResult.Pass("JSON parse OK");
    }

    private static GateResult CheckXml(string path)
    {
        string text = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(text)) return GateResult.Fail("XML empty/whitespace");
        try { XDocument.Parse(text); }
        catch (System.Xml.XmlException ex) { return GateResult.Fail($"XML parse failed: {ex.Message}"); }
        return GateResult.Pass("XML parse OK");
    }

    private static GateResult CheckYaml(string path)
    {
        // No YAML parser dependency in v1 (keeps the tool dep-light). Replicate the PS truncation
        // heuristic exactly: a large file with no trailing newline is a mid-line-truncation signal.
        // (YamlDotNet can be added later as an ADDITIONAL check — but this heuristic catches a
        // truncation a parser would happily accept.)
        string text = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(text)) return GateResult.Fail("YAML empty/whitespace");
        if (text.Length > YamlTruncationThresholdBytes && !text.EndsWith('\n'))
            return GateResult.Fail($"YAML missing trailing newline at {text.Length} bytes (truncation?)");
        return GateResult.Pass("YAML basic shape OK");
    }

    private static GateResult CheckRazor(string path)
    {
        string text = File.ReadAllText(path);
        var hits = new List<string>();

        foreach (Match style in StyleBlock().Matches(text))
        {
            string styleBody = style.Groups[1].Value;
            int styleBodyStart = style.Groups[1].Index;

            foreach (Match comment in CssComment().Matches(styleBody))
            {
                string commentText = comment.Value;

                foreach (Match off in RazorOffender().Matches(commentText))
                {
                    int absIndex = styleBodyStart + comment.Index + off.Index;
                    int line = text.AsSpan(0, absIndex).Count('\n') + 1;
                    int excerptStart = Math.Max(0, off.Index - 12);
                    int excerptLen = Math.Min(40, commentText.Length - excerptStart);
                    string excerpt = commentText.Substring(excerptStart, excerptLen);
                    hits.Add($"[line {line}] in CSS comment: '...{excerpt}...'");
                    if (hits.Count >= MaxErrorsShown) break;
                }
                if (hits.Count >= MaxErrorsShown) break;
            }
            if (hits.Count >= MaxErrorsShown) break;
        }

        if (hits.Count > 0)
            return GateResult.Fail(
                $"Razor CSS-comment offender ({hits.Count} hit(s)): {string.Join(" | ", hits)} -- paraphrase markup descriptions in prose without literal angle brackets.");
        return GateResult.Pass("Razor CSS-comment scan OK");
    }

    private static GateResult CheckPs1(string path)
    {
        // Reuse PowerShell's own parser via a graceful shell-out to `pwsh`. If pwsh isn't on PATH,
        // skip (pass) — no PowerShell SDK dependency is dragged into the tool.
        //
        // The target path travels via an ENVIRONMENT VARIABLE, not a process argument: with
        // `pwsh -Command "<string>"`, trailing process arguments do NOT populate $args (they're
        // treated as part of the command text per about_pwsh), so an $args[0]-based script sees
        // $null and ParseFile reports "[line 0 col 0] The file could not be read: Cannot process
        // argument because the value of argument 'path' is not valid" — for EVERY file, healthy
        // or not. Env-var passing is quoting-proof and pwsh-version-proof. The path is also
        // rooted (ParseFile wants a full path; --files delivers repo-relative shapes).
        const string pathEnvVar = "GSCRIPT_PS1_LINT_PATH";
        const string script =
            "$t=$null;$e=$null;" +
            "[System.Management.Automation.Language.Parser]::ParseFile($env:GSCRIPT_PS1_LINT_PATH,[ref]$t,[ref]$e)|Out-Null;" +
            "if($e -and $e.Count){foreach($x in $e){\"[line $($x.Extent.StartLineNumber) col $($x.Extent.StartColumnNumber)] $($x.Message)\"};exit 1}else{exit 0}";

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "pwsh",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add(script);
            psi.Environment[pathEnvVar] = Path.GetFullPath(path);

            using var proc = Process.Start(psi);
            if (proc is null) return GateResult.Pass("PS1 lint skipped (pwsh not available)");

            string outp = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit();
            if (proc.ExitCode == 0) return GateResult.Pass("PS1 parse OK");

            var errs = outp.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            int more = errs.Length - MaxErrorsShown;
            string joined = string.Join(" | ", errs.Take(MaxErrorsShown));
            string suffix = more > 0 ? $" (+{more} more)" : "";
            return GateResult.Fail($"PS1 parse FAILED ({errs.Length} error(s){suffix}): {joined}");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return GateResult.Pass("PS1 lint skipped (pwsh not on PATH)");
        }
    }
}
