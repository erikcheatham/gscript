namespace Gscript.Tasks;

/// <summary>
/// Transport-agnostic task store. Two implementations: <see cref="TaskBusClient"/> (the original
/// HTTP bus) and <see cref="FileTaskBus"/> (files in the operator's private repo).
///
/// <para><b>Why the file transport exists.</b> The HTTP bus points at claude-comms on
/// localhost:8767, which is legacy infrastructure that no longer runs — so the verbs were built
/// and the bus was unplugged. The file transport needs no daemon and no port, and because the
/// operator's private repo already replicates between writer seats, a task posted on one seat
/// arrives on the others for free.</para>
///
/// <para><b>The authority property this preserves.</b> A task's payload is a
/// <see cref="TaskTarget"/> — repo, files, message, no-deploy — NOT a shell command. So an agent
/// that can write to the task directory can only ever PROPOSE a gscript push, which still runs
/// every gate, the leak-check, and the divergence guard on approval. It cannot ask the machine to
/// run something arbitrary. The operator's approve step remains the only thing that executes
/// anything: propose and execute stay on opposite sides of the boundary.</para>
/// </summary>
public interface ITaskBus
{
    /// <summary>Post a new task in the pending state. Returns its id.</summary>
    string Create(string createdBy, string? assignedTo, string subject, string? description, TaskTarget target);

    IReadOnlyList<TaskRecord> List(string? status, string? createdBy, string? assignedTo);

    TaskRecord? Get(string id);

    void Approve(string id, string by);
    void Reject(string id, string by, string? reason);
    void Start(string id, string by);
    void RecordResult(string id, string by, string status, TaskResult result);
}
