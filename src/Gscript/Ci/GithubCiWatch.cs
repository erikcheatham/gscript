using System.Globalization;
using System.Net.Http;
using System.Text.Json;

namespace Gscript.Ci;

/// <summary>Result of watching a GitHub Actions run for a pushed commit.</summary>
public sealed record CiWatchResult(bool Success, long? RunId, string Url, string Conclusion);

/// <summary>
/// Polls GitHub Actions for the workflow run keyed on a commit SHA, printing live per-step
/// transitions until completion/timeout. Diff-only output (each printed line is one transition).
/// ASCII markers (NOT Unicode arrows — Razor parsers + some terminal fonts mis-render those):
/// <c>&gt;&gt;</c> in-progress, <c>OK </c> success, <c>XX </c> failure, <c>-- </c> skipped.
/// The per-step jobs endpoint needs the PAT's <b>Actions: Read</b> scope; without it the jobs call
/// 403s and is swallowed by the inner try/catch so the run-level loop still works.
/// </summary>
public static class GithubCiWatch
{
    public static CiWatchResult Watch(
        string repoOwner, string repoName, string workflowFile, string commitSha, string pat,
        int maxMinutes = 15, int pollSeconds = 20)
    {
        string apiBase = $"https://api.github.com/repos/{repoOwner}/{repoName}";
        string actionsUrl = $"https://github.com/{repoOwner}/{repoName}/actions";

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {pat}");
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        client.DefaultRequestHeaders.Add("User-Agent", "gscript"); // GitHub API rejects requests with no User-Agent

        var deadline = DateTime.UtcNow.AddMinutes(maxMinutes);
        long? runId = null;
        string? lastStatus = null, lastConclusion = null;
        var printedJobs = new HashSet<string>();
        var stepState = new Dictionary<string, (string Status, string Conclusion)>();

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                string status = "", conclusion = "", htmlUrl = "";

                using (var runsDoc = GetJson(client, $"{apiBase}/actions/workflows/{workflowFile}/runs?head_sha={commitSha}&per_page=1"))
                {
                    var root = runsDoc.RootElement;
                    int totalCount = root.TryGetProperty("total_count", out var tc) ? tc.GetInt32() : 0;
                    if (totalCount > 0 && root.TryGetProperty("workflow_runs", out var runs) && runs.GetArrayLength() > 0)
                    {
                        var run = runs[0];
                        if (run.TryGetProperty("id", out var idEl)) runId = idEl.GetInt64();
                        status = Str(run, "status");
                        conclusion = Str(run, "conclusion");
                        htmlUrl = Str(run, "html_url");

                        // run-level transition print (only on a status/conclusion change)
                        if (status != lastStatus || conclusion != lastConclusion)
                        {
                            string line = $"  [{Now()}] status={status}";
                            if (!string.IsNullOrEmpty(conclusion)) line += $" conclusion={conclusion}";
                            if (conclusion == "success") Log.Green(line);
                            else if (!string.IsNullOrEmpty(conclusion)) Log.Red(line);
                            else Log.Cyan(line);
                            lastStatus = status;
                            lastConclusion = conclusion;
                        }
                    }
                }

                // per-step polling (may 404 on a fresh run / 403 without Actions:Read — swallowed)
                if (runId.HasValue)
                {
                    try
                    {
                        using var jobsDoc = GetJson(client, $"{apiBase}/actions/runs/{runId.Value}/jobs");
                        if (jobsDoc.RootElement.TryGetProperty("jobs", out var jobs) && jobs.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var job in jobs.EnumerateArray())
                            {
                                string jobName = Str(job, "name");
                                string jobStatus = Str(job, "status");
                                if (!printedJobs.Contains(jobName) && jobStatus != "queued")
                                {
                                    Log.Cyan($"  {jobName} job:");
                                    printedJobs.Add(jobName);
                                }

                                if (!job.TryGetProperty("steps", out var steps) || steps.ValueKind != JsonValueKind.Array)
                                    continue;

                                foreach (var step in steps.EnumerateArray())
                                {
                                    int number = step.TryGetProperty("number", out var nm) ? nm.GetInt32() : 0;
                                    string sName = Str(step, "name");
                                    string sStatus = Str(step, "status");
                                    string sConcl = Str(step, "conclusion");
                                    string key = $"{jobName}::{number}";
                                    var newState = (sStatus, sConcl);

                                    if (stepState.TryGetValue(key, out var prev) && prev == newState)
                                        continue; // no change since last poll

                                    if (sStatus == "completed")
                                    {
                                        string dur = FormatStepDuration(Str(step, "started_at"), Str(step, "completed_at"));
                                        string icon = sConcl == "success" ? "OK " : sConcl == "skipped" ? "-- " : "XX ";
                                        string text = $"    [{Now()}] {icon}{sName} ({dur})";
                                        if (sConcl != "success" && sConcl != "skipped")
                                            text += $" [conclusion={sConcl}]";
                                        if (sConcl == "success") Log.Green(text);
                                        else if (sConcl == "skipped") Log.DarkGray(text);
                                        else Log.Red(text);
                                    }
                                    else if (sStatus == "in_progress")
                                    {
                                        Log.Yellow($"    [{Now()}] >> {sName} ...");
                                    }

                                    stepState[key] = newState;
                                }
                            }
                        }
                    }
                    catch { /* jobs endpoint optional; run-level loop continues */ }
                }

                // completion
                if (status == "completed")
                {
                    if (conclusion == "success")
                    {
                        Log.Green($"CI GREEN: {htmlUrl}");
                        return new CiWatchResult(true, runId, htmlUrl, conclusion);
                    }
                    Log.Red($"CI {conclusion.ToUpperInvariant()}: {htmlUrl}");
                    Log.Yellow("Inspect failure logs at the URL above.");
                    return new CiWatchResult(false, runId, htmlUrl, conclusion);
                }
            }
            catch (Exception ex)
            {
                Log.Yellow($"  WARN: CI poll error (will retry): {ex.Message}");
            }

            Thread.Sleep(TimeSpan.FromSeconds(pollSeconds));
        }

        Log.Yellow($"TIMEOUT: CI didn't complete within {maxMinutes} min.");
        Log.Yellow($"  {actionsUrl}");
        return new CiWatchResult(false, runId, actionsUrl, "timeout");
    }

    private static JsonDocument GetJson(HttpClient client, string url)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        using var resp = client.Send(req);
        resp.EnsureSuccessStatusCode();
        using var stream = resp.Content.ReadAsStream();
        return JsonDocument.Parse(stream);
    }

    private static string Str(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";

    private static string Now() => DateTime.Now.ToString("HH:mm:ss");

    /// <summary>Format a step's elapsed time: <c>"4s"</c> under a minute, else <c>"4m 53s"</c>. Empty when either timestamp is missing.</summary>
    private static string FormatStepDuration(string startedAt, string completedAt)
    {
        if (string.IsNullOrEmpty(startedAt) || string.IsNullOrEmpty(completedAt)) return "";
        if (!DateTimeOffset.TryParse(startedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var started)) return "";
        if (!DateTimeOffset.TryParse(completedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var completed)) return "";

        var span = completed - started;
        if (span.TotalSeconds < 60) return $"{(int)span.TotalSeconds}s";
        int m = (int)span.TotalMinutes;
        int s = (int)(span.TotalSeconds - m * 60);
        return $"{m}m {s}s";
    }
}
