using System.Security.Cryptography;
using System.Text.Json;

namespace Gscript.Tasks;

/// <summary>
/// File-backed task store: one JSON document per task in a directory, no daemon, no port.
///
/// <para>Intended home is the operator's private repo, which is already synced between writer
/// seats — so posting a task on one seat and approving it on another needs no new infrastructure,
/// and git is the replication. Cross-seat concurrent edits surface as ordinary merge conflicts,
/// which is the honest failure mode rather than a silent last-writer-wins.</para>
///
/// <para>Every transition is read-modify-write with an appended history entry, so a task carries
/// its own audit trail: who posted it, who approved it, what the run returned. That trail is the
/// point — the operator should be able to answer "why did this get pushed" from the file alone.</para>
/// </summary>
public sealed class FileTaskBus : ITaskBus
{
    private static readonly JsonSerializerOptions J = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,   // these files are READ BY HUMANS before approval
    };

    private readonly string _dir;

    public FileTaskBus(string directory)
    {
        _dir = directory;
        Directory.CreateDirectory(_dir);
    }

    public string Root => _dir;

    public string Create(string createdBy, string? assignedTo, string subject, string? description, TaskTarget target)
    {
        var id = NewId();
        var now = Timestamp();
        var rec = new TaskRecord
        {
            TaskId = id,
            CreatedAt = now,
            CreatedBy = createdBy,
            AssignedTo = assignedTo,
            Subject = subject,
            Description = description ?? "",
            Status = "pending",
            Target = target,
            History = { new TaskHistoryEntry { Ts = now, By = createdBy, Event = "created", Status = "pending" } },
        };
        Write(rec);
        return id;
    }

    public IReadOnlyList<TaskRecord> List(string? status, string? createdBy, string? assignedTo)
    {
        var results = new List<TaskRecord>();
        foreach (var path in System.IO.Directory.EnumerateFiles(_dir, "*.json"))
        {
            var rec = TryRead(path);
            if (rec is null) continue;   // a half-written or conflicted file must not break `list`
            if (status is { Length: > 0 } && !string.Equals(rec.Status, status, StringComparison.OrdinalIgnoreCase)) continue;
            if (createdBy is { Length: > 0 } && !string.Equals(rec.CreatedBy, createdBy, StringComparison.OrdinalIgnoreCase)) continue;
            if (assignedTo is { Length: > 0 } && !string.Equals(rec.AssignedTo, assignedTo, StringComparison.OrdinalIgnoreCase)) continue;
            results.Add(rec);
        }
        results.Sort((a, b) => string.CompareOrdinal(a.CreatedAt, b.CreatedAt));
        return results;
    }

    public TaskRecord? Get(string id) => TryRead(PathFor(id));

    public void Approve(string id, string by) => Transition(id, by, "approved", "approved", null);
    public void Reject(string id, string by, string? reason) => Transition(id, by, "rejected", "rejected", reason);
    public void Start(string id, string by) => Transition(id, by, "started", "in_progress", null);

    public void RecordResult(string id, string by, string status, TaskResult result)
    {
        var rec = Require(id);
        rec.Result = result;
        rec.Status = status;
        rec.History.Add(new TaskHistoryEntry
        {
            Ts = Timestamp(), By = by, Event = "result", Status = status,
            Note = result.Sha is { Length: > 0 } ? $"sha={result.Sha} ci={result.CiStatus}" : result.Detail,
        });
        Write(rec);
    }

    private void Transition(string id, string by, string evt, string status, string? note)
    {
        var rec = Require(id);
        rec.Status = status;
        rec.History.Add(new TaskHistoryEntry { Ts = Timestamp(), By = by, Event = evt, Status = status, Note = note });
        Write(rec);
    }

    private TaskRecord Require(string id) =>
        Get(id) ?? throw new GscriptException($"task '{id}' not found in {_dir}");

    private string PathFor(string id)
    {
        // Ids are tool-generated, but this is a filesystem path built from an argument that can
        // reach us through a synced file — so refuse anything that could escape the directory.
        if (id.Contains('/') || id.Contains('\\') || id.Contains("..") || Path.IsPathRooted(id))
            throw new GscriptException($"invalid task id '{id}'");
        return Path.Combine(_dir, id + ".json");
    }

    private void Write(TaskRecord rec)
    {
        var path = PathFor(rec.TaskId);
        // Write-then-move so a reader never sees a half-written task.
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(rec, J));
        File.Move(tmp, path, overwrite: true);
    }

    private static TaskRecord? TryRead(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<TaskRecord>(File.ReadAllText(path), J);
        }
        catch (JsonException)
        {
            return null;   // conflicted / hand-edited: skip rather than crash the whole command
        }
    }

    private static string Timestamp() => DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");

    private static string NewId() =>
        $"t-{DateTime.UtcNow:yyyyMMddHHmmss}-{Convert.ToHexString(RandomNumberGenerator.GetBytes(2)).ToLowerInvariant()}";
}
