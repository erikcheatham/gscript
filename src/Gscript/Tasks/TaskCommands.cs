namespace Gscript.Tasks;

/// <summary>
/// The <c>gscript task</c> command family — the operator/AI-facing client over the comms task-bus.
/// post (propose work + payload) → approve (operator gate) → run (execute the push, write result
/// back) → list/show (see the lifecycle). This is the convergence: coordination (comms) and
/// execution (the push pipeline) on one bus, so both AIs and the operator see the whole thing.
/// </summary>
public static class TaskCommands
{
    public static int Run(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            PrintUsage();
            return args.Length == 0 ? 1 : 0;
        }

        var rest = args[1..];
        return args[0] switch
        {
            "post" => Post(rest),
            "list" => ListCmd(rest),
            "show" => Show(rest),
            "approve" => ApproveCmd(rest),
            "reject" => RejectCmd(rest),
            "run" => RunCmd(rest),
            _ => UnknownSub(args[0]),
        };
    }

    // ---- post ----
    private static int Post(string[] args)
    {
        string? to = null, from = null, subject = null, desc = null, repoOwner = null,
                repoName = null, workingDir = null, files = null, message = null,
                url = null, token = null;
        bool noDeploy = false;

        for (int i = 0; i < args.Length; i++)
            switch (args[i])
            {
                case "--to": to = Next(args, ref i); break;
                case "--from" or "--by": from = Next(args, ref i); break;
                case "--subject" or "-s": subject = Next(args, ref i); break;
                case "--description": desc = Next(args, ref i); break;
                case "--repo-owner": repoOwner = Next(args, ref i); break;
                case "--repo-name": repoName = Next(args, ref i); break;
                case "--working-dir": workingDir = Next(args, ref i); break;
                case "--files": files = Next(args, ref i); break;
                case "--message" or "-m": message = Next(args, ref i); break;
                case "--no-deploy": noDeploy = true; break;
                case "--comms-url": url = Next(args, ref i); break;
                case "--comms-token": token = Next(args, ref i); break;
                default: throw new GscriptException($"task post: unknown option '{args[i]}'");
            }

        if (string.IsNullOrEmpty(subject)) throw new GscriptException("task post: --subject is required");
        if (string.IsNullOrEmpty(workingDir)) throw new GscriptException("task post: --working-dir is required (where 'task run' executes)");
        from ??= Environment.GetEnvironmentVariable("GSCRIPT_WHO") ?? "operator";

        var target = new TaskTarget
        {
            RepoOwner = repoOwner,
            RepoName = repoName,
            WorkingDir = workingDir,
            Files = (files ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
            Message = message,
            NoDeploy = noDeploy,
        };

        var bus = TaskBusClient.FromEnv(url, token);
        var id = bus.Create(from!, to, subject!, desc, target);
        Log.Green($"task posted: {id}  (proposed, {from} -> {to ?? "anyone"})");
        return 0;
    }

    // ---- list ----
    private static int ListCmd(string[] args)
    {
        string? status = null, createdBy = null, assignedTo = null, url = null, token = null;
        for (int i = 0; i < args.Length; i++)
            switch (args[i])
            {
                case "--status": status = Next(args, ref i); break;
                case "--created-by": createdBy = Next(args, ref i); break;
                case "--assigned-to" or "--for": assignedTo = Next(args, ref i); break;
                case "--comms-url": url = Next(args, ref i); break;
                case "--comms-token": token = Next(args, ref i); break;
                default: throw new GscriptException($"task list: unknown option '{args[i]}'");
            }

        var bus = TaskBusClient.FromEnv(url, token);
        var tasks = bus.List(status, createdBy, assignedTo);
        if (tasks.Count == 0) { Log.Plain("(no tasks)"); return 0; }
        foreach (var t in tasks) PrintRow(t);
        return 0;
    }

    // ---- show ----
    private static int Show(string[] args)
    {
        var (id, _, url, token, _) = ParseIdAction(args, "show");
        var bus = TaskBusClient.FromEnv(url, token);
        var t = bus.Get(id) ?? throw new GscriptException($"task {id} not found");

        Log.Cyan($"task {t.TaskId}  [{t.Status}]");
        Log.Plain($"  subject:  {t.Subject}");
        Log.Plain($"  by:       {t.CreatedBy} -> {t.AssignedTo ?? "any"}   created {t.CreatedAt}");
        if (!string.IsNullOrEmpty(t.Description)) Log.Plain($"  desc:     {t.Description}");
        if (t.Target is { } tg)
        {
            Log.Plain($"  target:   {tg.RepoOwner}/{tg.RepoName} @ {tg.WorkingDir}{(tg.NoDeploy ? "  [no-deploy]" : "")}");
            Log.Plain($"  files:    {string.Join(", ", tg.Files)}");
            if (!string.IsNullOrEmpty(tg.Message)) Log.Plain($"  message:  {tg.Message.Split('\n')[0]}");
        }
        if (t.Result is { } r) Log.Plain($"  result:   sha={r.Sha}  ci={r.CiStatus}  {r.Detail}");
        Log.Plain("  history:");
        foreach (var h in t.History)
            Log.Plain($"    {h.Ts}  {h.By,-9} {h.Event} -> {h.Status}{(string.IsNullOrEmpty(h.Note) ? "" : $"  ({h.Note})")}");
        return 0;
    }

    // ---- approve / reject ----
    private static int ApproveCmd(string[] args)
    {
        var (id, by, url, token, _) = ParseIdAction(args, "approve");
        var bus = TaskBusClient.FromEnv(url, token);
        bus.Approve(id, by);
        Log.Green($"task {id}: approved");
        return 0;
    }

    private static int RejectCmd(string[] args)
    {
        var (id, by, url, token, reason) = ParseIdAction(args, "reject");
        var bus = TaskBusClient.FromEnv(url, token);
        bus.Reject(id, by, reason);
        Log.Yellow($"task {id}: rejected{(string.IsNullOrEmpty(reason) ? "" : $"  ({reason})")}");
        return 0;
    }

    // ---- run (the executor) ----
    private static int RunCmd(string[] args)
    {
        string? id = null, by = null, url = null, token = null;
        bool force = false;
        for (int i = 0; i < args.Length; i++)
            switch (args[i])
            {
                case "--by": by = Next(args, ref i); break;
                case "--force": force = true; break;
                case "--comms-url": url = Next(args, ref i); break;
                case "--comms-token": token = Next(args, ref i); break;
                default:
                    if (!args[i].StartsWith("--") && id is null) id = args[i];
                    else throw new GscriptException($"task run: unexpected '{args[i]}'");
                    break;
            }
        if (id is null) throw new GscriptException("task run: <task_id> required");
        by ??= "operator";

        var bus = TaskBusClient.FromEnv(url, token);
        var task = bus.Get(id) ?? throw new GscriptException($"task {id} not found");
        if (task.Status != "approved" && !force)
            throw new GscriptException($"task {id} is '{task.Status}', not 'approved'. Approve it first, or pass --force.");

        var t = task.Target ?? throw new GscriptException($"task {id} has no execution target");
        if (string.IsNullOrEmpty(t.WorkingDir))
            throw new GscriptException($"task {id} target has no working_dir");

        // Base config = the target repo's own gscript.json (gates / CI / visibility);
        // overlay the task's sprint fields (files / message / no-deploy).
        string cfgPath = Path.Combine(t.WorkingDir, "gscript.json");
        GscriptConfig cfg = File.Exists(cfgPath) ? GscriptConfig.Load(cfgPath) : new GscriptConfig();
        cfg.WorkingDirectory = t.WorkingDir;
        if (t.Files is { Count: > 0 }) cfg.FilesToStage = t.Files;
        if (!string.IsNullOrEmpty(t.Message)) cfg.CommitMessage = t.Message;
        if (t.NoDeploy) cfg.NoDeploy = true;
        if (!string.IsNullOrEmpty(t.RepoOwner)) cfg.RepoOwner = t.RepoOwner;
        if (!string.IsNullOrEmpty(t.RepoName)) cfg.RepoName = t.RepoName;

        bus.Start(id, by);
        Log.Cyan($"task {id}: executing...");
        try
        {
            var result = GscriptRunner.Run(cfg);
            bus.RecordResult(id, by, "shipped", new TaskResult
            {
                Sha = result.CommitSha,
                CiStatus = cfg.WatchCi ? "green" : "not-watched",
                Detail = $"shipped {result.ShortSha}",
            });
            Log.Green($"task {id}: SHIPPED {result.ShortSha}  (result written to the bus)");
            return 0;
        }
        catch (Exception ex)
        {
            try { bus.RecordResult(id, by, "failed", new TaskResult { Detail = ex.Message }); }
            catch (Exception writeEx) { Log.Red($"  (also failed to write result to the bus: {writeEx.Message})"); }
            Log.Red($"task {id}: FAILED - {ex.Message}  (result written to the bus)");
            return 1;
        }
    }

    // ---- helpers ----

    private static void PrintRow(TaskRecord t)
    {
        string line = $"  {t.TaskId}  [{t.Status,-9}]  {t.CreatedBy}->{t.AssignedTo ?? "any"}  {t.Subject}";
        switch (t.Status)
        {
            case "shipped": Log.Green(line); break;
            case "failed": Log.Red(line); break;
            case "rejected": Log.DarkGray(line); break;
            case "approved" or "executing": Log.Yellow(line); break;
            default: Log.Cyan(line); break; // proposed
        }
    }

    private static string Next(string[] args, ref int i)
    {
        if (i + 1 >= args.Length) throw new GscriptException($"option '{args[i]}' expects a value");
        return args[++i];
    }

    private static (string id, string by, string? url, string? token, string? reason) ParseIdAction(string[] args, string verb)
    {
        string? id = null, by = null, url = null, token = null, reason = null;
        for (int i = 0; i < args.Length; i++)
            switch (args[i])
            {
                case "--by": by = Next(args, ref i); break;
                case "--reason": reason = Next(args, ref i); break;
                case "--comms-url": url = Next(args, ref i); break;
                case "--comms-token": token = Next(args, ref i); break;
                default:
                    if (!args[i].StartsWith("--") && id is null) id = args[i];
                    else throw new GscriptException($"task {verb}: unexpected '{args[i]}'");
                    break;
            }
        if (id is null) throw new GscriptException($"task {verb}: <task_id> required");
        return (id, by ?? "operator", url, token, reason);
    }

    private static int UnknownSub(string sub)
    {
        Log.Red($"gscript task: unknown subcommand '{sub}'. Try 'gscript task --help'.");
        return 1;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("gscript task - the comms task-bus (post -> approve -> run -> result)");
        Console.WriteLine();
        Console.WriteLine("  gscript task post    --to <slug> --subject <s> --working-dir <d> --files <a,b> --message <m>");
        Console.WriteLine("                       [--no-deploy] [--from <slug>] [--repo-owner <o>] [--repo-name <r>]");
        Console.WriteLine("  gscript task list    [--status <s>] [--created-by <slug>] [--for <slug>]");
        Console.WriteLine("  gscript task show    <task_id>");
        Console.WriteLine("  gscript task approve <task_id> [--by <slug>]");
        Console.WriteLine("  gscript task reject  <task_id> [--by <slug>] [--reason <r>]");
        Console.WriteLine("  gscript task run     <task_id> [--by <slug>] [--force]");
        Console.WriteLine();
        Console.WriteLine("Bus: env COMMS_URL (default http://localhost:8767) + COMMS_TOKEN, or --comms-url/--comms-token.");
        Console.WriteLine("'run' loads the target repo's gscript.json for gates/CI, overlays the task's files+message, pushes, writes the result back.");
    }
}
