using Gscript.Local;

namespace Gscript.Tasks;

/// <summary>
/// Picks the task transport. File-backed by DEFAULT; HTTP only when a bus URL is explicitly
/// supplied.
///
/// <para>That default is a deliberate inversion of the original design. The HTTP bus was the only
/// transport, and it pointed at claude-comms on localhost:8767 — infrastructure that no longer
/// runs, which left the whole <c>gscript task</c> verb family built but unreachable. Defaulting to
/// files makes it work with no daemon, no port and no setup, while an explicit
/// <c>--comms-url</c>/<c>COMMS_URL</c> still selects the HTTP bus for anyone who stands one up.</para>
/// </summary>
public static class TaskBus
{
    /// <summary>Env var naming the task directory. A PATH, not a secret — so unlike the credential
    /// env vars LOCALMD.md warns about, it cannot go stale in a way that produces a mystery 401.</summary>
    public const string DirEnvVar = "GSCRIPT_TASKS_DIR";

    public static ITaskBus Resolve(string? urlOverride, string? tokenOverride)
    {
        var url = urlOverride ?? Environment.GetEnvironmentVariable("COMMS_URL");
        if (!string.IsNullOrWhiteSpace(url))
            return TaskBusClient.FromEnv(urlOverride, tokenOverride);

        return new FileTaskBus(ResolveDir());
    }

    /// <summary>
    /// Resolution order: <c>GSCRIPT_TASKS_DIR</c> → a <c>tasks/</c> folder beside the localmd named
    /// by the repo's own <c>gscript.json</c> → beside <see cref="Localmd.DefaultPath"/> (which, as
    /// of alpha.17, consults the MACHINE config first — see <see cref="MachineConfig"/>).
    ///
    /// <para>The gscript.json step is a per-repo override, but it reads from the CURRENT DIRECTORY —
    /// which made the task bus CWD-dependent: the same <c>task list</c> answered differently from a
    /// repo root and from anywhere else, and a relocated operator standing outside a repo got an
    /// empty list from a stale profile-relative path (the alpha.17 bug). The machine config closes
    /// that: one <c>gscript config set localmdPath</c> per machine and the fallback is correct from
    /// ANY directory, with no env var and no operator path in this public tree.</para>
    ///
    /// <para>Wherever it lands, the folder is the operator's private, already-synced repo: tasks
    /// replicate between writer seats for free and never enter a public tree.</para>
    /// </summary>
    public static string ResolveDir()
    {
        var env = Environment.GetEnvironmentVariable(DirEnvVar);
        if (!string.IsNullOrWhiteSpace(env)) return env!;

        // The repo's own config knows where localmd actually lives.
        try
        {
            if (File.Exists("gscript.json"))
            {
                var cfg = GscriptConfig.Load("gscript.json");
                var configured = cfg.LocalmdPath ?? cfg.PatFile;
                if (!string.IsNullOrWhiteSpace(configured))
                {
                    var dir = Directory.Exists(configured)
                        ? configured
                        : Path.GetDirectoryName(configured);
                    if (!string.IsNullOrWhiteSpace(dir))
                        return Path.Combine(dir!, "tasks");
                }
            }
        }
        catch (GscriptException)
        {
            // An unparseable gscript.json is the push path's problem to report, not this one's.
        }

        var localmdDir = Path.GetDirectoryName(Localmd.DefaultPath());
        if (string.IsNullOrWhiteSpace(localmdDir))
            throw new GscriptException(
                $"cannot resolve a task directory: set {DirEnvVar} to a private folder "
                + "(e.g. the tasks/ folder inside your private repo).");

        return Path.Combine(localmdDir, "tasks");
    }
}
