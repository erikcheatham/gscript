using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Gscript.Tasks;

/// <summary>
/// HTTP client for the comms task-bus (<c>/api/tasks/*</c> on the claude-comms service).
/// Bus URL + token resolve from env (<c>COMMS_URL</c>, default http://localhost:8767, and
/// <c>COMMS_TOKEN</c>) with optional per-call overrides. Sync (<c>HttpClient.Send</c>) to match
/// the rest of the tool. Server errors surface as <see cref="GscriptException"/> with the bus's
/// own error string; a down bus surfaces a clear "unreachable" message.
/// </summary>
public sealed class TaskBusClient
{
    private static readonly JsonSerializerOptions J = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _baseUrl;
    private readonly HttpClient _http;

    private TaskBusClient(string baseUrl, string token)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.Add("X-Comms-Token", token);
        _http.DefaultRequestHeaders.Add("User-Agent", "gscript");
    }

    public static TaskBusClient FromEnv(string? urlOverride, string? tokenOverride)
    {
        var url = urlOverride
            ?? Environment.GetEnvironmentVariable("COMMS_URL")
            ?? "http://localhost:8767";
        var token = tokenOverride ?? Environment.GetEnvironmentVariable("COMMS_TOKEN") ?? "";
        if (string.IsNullOrEmpty(token))
            throw new GscriptException(
                "COMMS_TOKEN not set. Provide it via the COMMS_TOKEN env var or --comms-token (the task bus is token-gated).");
        return new TaskBusClient(url, token);
    }

    public string Create(string createdBy, string? assignedTo, string subject, string? description, TaskTarget target)
    {
        var payload = new { CreatedBy = createdBy, AssignedTo = assignedTo, Subject = subject, Description = description, Target = target };
        using var doc = PostJson("/api/tasks/create", payload);
        return doc.RootElement.TryGetProperty("task_id", out var tid) ? (tid.GetString() ?? "") : "";
    }

    public IReadOnlyList<TaskRecord> List(string? status, string? createdBy, string? assignedTo)
    {
        var qs = new List<string>();
        if (!string.IsNullOrEmpty(status)) qs.Add($"status={Uri.EscapeDataString(status)}");
        if (!string.IsNullOrEmpty(createdBy)) qs.Add($"created_by={Uri.EscapeDataString(createdBy)}");
        if (!string.IsNullOrEmpty(assignedTo)) qs.Add($"assigned_to={Uri.EscapeDataString(assignedTo)}");
        string path = "/api/tasks/list" + (qs.Count > 0 ? "?" + string.Join("&", qs) : "");
        using var doc = GetJson(path);
        var arr = doc.RootElement.GetProperty("tasks");
        return JsonSerializer.Deserialize<List<TaskRecord>>(arr.GetRawText(), J) ?? new List<TaskRecord>();
    }

    public TaskRecord? Get(string id)
    {
        using var resp = Send(HttpMethod.Get, $"/api/tasks/get?id={Uri.EscapeDataString(id)}", null);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        var text = ReadBody(resp);
        ThrowIfError(resp, text);
        return JsonSerializer.Deserialize<TaskRecord>(text, J);
    }

    public void Approve(string id, string by) => Transition("/api/tasks/approve", new { id, by });
    public void Reject(string id, string by, string? reason) => Transition("/api/tasks/reject", new { id, by, reason });
    public void Start(string id, string by) => Transition("/api/tasks/start", new { id, by });

    public void RecordResult(string id, string by, string status, TaskResult result)
        => Transition("/api/tasks/result", new { id, by, status, result });

    private void Transition(string path, object payload)
    {
        using var _ = PostJson(path, payload);
    }

    // ---- http plumbing ----

    private JsonDocument PostJson(string path, object payload)
    {
        var json = JsonSerializer.Serialize(payload, J);
        using var resp = Send(HttpMethod.Post, path, json);
        var text = ReadBody(resp);
        ThrowIfError(resp, text);
        return JsonDocument.Parse(text);
    }

    private JsonDocument GetJson(string path)
    {
        using var resp = Send(HttpMethod.Get, path, null);
        var text = ReadBody(resp);
        ThrowIfError(resp, text);
        return JsonDocument.Parse(text);
    }

    private HttpResponseMessage Send(HttpMethod method, string path, string? jsonBody)
    {
        var req = new HttpRequestMessage(method, _baseUrl + path);
        if (jsonBody is not null)
            req.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        try { return _http.Send(req); }
        catch (HttpRequestException ex)
        {
            throw new GscriptException($"task bus unreachable at {_baseUrl} ({ex.Message}). Is the claude-comms container running?");
        }
        catch (TaskCanceledException)
        {
            throw new GscriptException($"task bus request timed out at {_baseUrl}.");
        }
    }

    private static string ReadBody(HttpResponseMessage resp)
    {
        using var s = resp.Content.ReadAsStream();
        using var r = new StreamReader(s, Encoding.UTF8);
        return r.ReadToEnd();
    }

    private static void ThrowIfError(HttpResponseMessage resp, string text)
    {
        if (resp.IsSuccessStatusCode) return;
        string msg = text;
        try
        {
            using var d = JsonDocument.Parse(text);
            if (d.RootElement.TryGetProperty("error", out var e)) msg = e.GetString() ?? text;
        }
        catch { /* non-JSON error body — use the raw text */ }
        throw new GscriptException($"task bus {(int)resp.StatusCode}: {msg}");
    }
}
