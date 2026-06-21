using System.Net.Http;

namespace Gscript.Deploy;

/// <summary>A post-deploy smoke target: a URL and the inclusive status-code range that counts as healthy.</summary>
public sealed record ProbeEndpoint(string Url, int RangeMin, int RangeMax);

/// <summary>Result of the post-deploy probe sweep.</summary>
public sealed record ProbeResult(bool Success, IReadOnlyList<string> Failures);

/// <summary>
/// Probes HTTP endpoints after a successful deploy, verifying each returns a status code inside its
/// expected range. CI-green ≠ "staging actually responds" (GOTCHAS #7) — a deploy can pass CI and
/// still 502 (tunnel dropped, container crashlooping post-smoke). Auto-redirect is OFF, so a 302
/// counts as the terminal code (passes when 302 is within the expected range, e.g. 200–399).
/// </summary>
public static class PostDeployProbe
{
    public static ProbeResult Probe(IReadOnlyList<ProbeEndpoint> endpoints, int timeoutSec = 30)
    {
        Log.Cyan("Probing post-deploy endpoints...");
        var failures = new List<string>();

        using var handler = new HttpClientHandler { AllowAutoRedirect = false }; // 3xx is terminal, not followed
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(timeoutSec) };
        client.DefaultRequestHeaders.Add("User-Agent", "gscript");

        foreach (var ep in endpoints)
        {
            int code;
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, ep.Url);
                using var resp = client.Send(req); // does NOT throw on 3xx/4xx/5xx — only on transport errors
                code = (int)resp.StatusCode;
            }
            catch (HttpRequestException ex) when (ex.StatusCode.HasValue)
            {
                code = (int)ex.StatusCode.Value;
            }
            catch
            {
                code = 0; // transport failure (DNS, refused, timeout) → out of range
            }

            bool inRange = code >= ep.RangeMin && code <= ep.RangeMax;
            string line = $"  {ep.Url} -> {code} (expected {ep.RangeMin}-{ep.RangeMax})";
            if (inRange) Log.Green(line);
            else { Log.Red(line); failures.Add(ep.Url); }
        }

        if (failures.Count > 0)
            Log.Red($"PROBE FAIL: {failures.Count} endpoint(s) outside expected range.");
        else
            Log.Green("ALL PROBES GREEN.");

        return new ProbeResult(failures.Count == 0, failures);
    }
}
