namespace Gscript.Tasks;

// Wire models for the comms task-bus. JSON keys are snake_case on the wire
// (task_id, created_by, working_dir, no_deploy, ci_status); the TaskBusClient's
// JsonSerializerOptions use JsonNamingPolicy.SnakeCaseLower so these PascalCase
// properties map both directions without per-property attributes.

public sealed class TaskRecord
{
    public string TaskId { get; set; } = "";
    public string CreatedAt { get; set; } = "";
    public string CreatedBy { get; set; } = "";
    public string? AssignedTo { get; set; }
    public string Subject { get; set; } = "";
    public string Description { get; set; } = "";
    public string Status { get; set; } = "";
    public TaskTarget? Target { get; set; }
    public TaskResult? Result { get; set; }
    public List<TaskHistoryEntry> History { get; set; } = new();
}

/// <summary>The execution payload: WHAT to ship. The HOW (gates, CI, visibility)
/// comes from the target repo's own gscript.json at run time.</summary>
public sealed class TaskTarget
{
    public string? RepoOwner { get; set; }
    public string? RepoName { get; set; }
    public string? WorkingDir { get; set; }
    public List<string> Files { get; set; } = new();
    public string? Message { get; set; }
    public bool NoDeploy { get; set; }
}

public sealed class TaskResult
{
    public string? Sha { get; set; }
    public string? CiStatus { get; set; }
    public string? Detail { get; set; }
}

public sealed class TaskHistoryEntry
{
    public string Ts { get; set; } = "";
    public string By { get; set; } = "";
    public string Event { get; set; } = "";
    public string Status { get; set; } = "";
    public string? Note { get; set; }
}
