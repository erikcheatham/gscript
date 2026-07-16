using Gscript;
using Gscript.Git;
using Gscript.Im;
using Gscript.Local;
using Gscript.Tasks;

return Cli.Run(args);

/// <summary>
/// CLI front-end. Repo-level config lives in <c>gscript.json</c> (auto-loaded from the working dir,
/// or <c>--config</c>); per-sprint data (files, message) is supplied on the CLI or in the config.
/// Maps a successful run to exit 0 and any failure to exit 1 — so a calling wrapper can detect
/// failure (and, in the Phase-3 task-bus, write the result back to comms).
/// </summary>
internal static class Cli
{
    private const string Version = "gscript 2.0.0-alpha.9";

    public static int Run(string[] args)
    {
        if (args.Length == 0) { PrintUsage(); return 1; }
        if (args[0] is "-h" or "--help" or "help") { PrintUsage(); return 0; }
        if (args[0] is "-v" or "--version") { Console.WriteLine(Version); return 0; }

        try
        {
            switch (args[0])
            {
                case "push":
                {
                    if (args[1..].Any(a => a is "-h" or "--help")) { PrintUsage(); return 0; }
                    var cfg = BuildConfig(args[1..]);
                    return GscriptRunner.Run(cfg).Success ? 0 : 1;
                }
                case "task":
                    return TaskCommands.Run(args[1..]);
                case "im":
                    return ImCommands.Run(args[1..]);
                default:
                    Log.Red($"gscript: unknown command '{args[0]}'. Try 'gscript push', 'gscript task', 'gscript im', or 'gscript --help'.");
                    return 1;
            }
        }
        catch (GscriptException ex) { Log.Red($"gscript: {ex.Message}"); return 1; }
        catch (LocalmdException ex) { Log.Red($"gscript: {ex.Message}"); return 1; }
        catch (StaleLockClearAbortedException ex) { Log.Red($"gscript: {ex.Message}"); return 1; }
        catch (Exception ex) { Log.Red($"gscript: unexpected error: {ex.Message}"); return 1; }
    }

    private static GscriptConfig BuildConfig(string[] args)
    {
        string? configPath = null, files = null, message = null, repoOwner = null,
                repoName = null, workflow = null, workingDir = null, localmd = null, logFile = null;
        bool noDeploy = false, noWatch = false, dryRun = false, noSync = false, requireClean = false;
        int? maxShrinkPct = null;
        var allowShrink = new List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--config": configPath = Next(args, ref i); break;
                case "--files": files = Next(args, ref i); break;
                case "--message" or "-m": message = Next(args, ref i); break;
                case "--repo-owner": repoOwner = Next(args, ref i); break;
                case "--repo-name": repoName = Next(args, ref i); break;
                case "--workflow": workflow = Next(args, ref i); break;
                case "--working-dir": workingDir = Next(args, ref i); break;
                case "--localmd": localmd = Next(args, ref i); break;
                case "--log": logFile = Next(args, ref i); break;
                case "--no-deploy": noDeploy = true; break;
                case "--no-watch": noWatch = true; break;
                case "--dry-run": dryRun = true; break;
                case "--no-sync": noSync = true; break;
                case "--require-clean": requireClean = true; break;
                case "--max-shrink-pct": maxShrinkPct = int.Parse(Next(args, ref i)); break;
                case "--allow-shrink": allowShrink.Add(Next(args, ref i)); break;
                default: throw new GscriptException($"unknown option '{args[i]}'");
            }
        }

        string effWorkingDir = workingDir ?? Directory.GetCurrentDirectory();
        string defaultConfig = Path.Combine(effWorkingDir, "gscript.json");

        GscriptConfig cfg =
            configPath is not null ? GscriptConfig.Load(configPath)
            : File.Exists(defaultConfig) ? GscriptConfig.Load(defaultConfig)
            : new GscriptConfig();

        // CLI overrides win over config values.
        if (files is not null)
            cfg.FilesToStage = files.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (message is not null) cfg.CommitMessage = message;
        if (repoOwner is not null) cfg.RepoOwner = repoOwner;
        if (repoName is not null) cfg.RepoName = repoName;
        if (workflow is not null) cfg.CiWorkflowFile = workflow;
        if (workingDir is not null) cfg.WorkingDirectory = workingDir;
        if (localmd is not null) cfg.LocalmdPath = localmd;
        if (logFile is not null) cfg.LogFile = logFile;
        cfg.LocalmdPath ??= cfg.PatFile;   // patFile json field is an alias; --localmd + localmdPath win over it
        if (noDeploy) cfg.NoDeploy = true;
        if (noWatch) cfg.WatchCi = false;
        if (dryRun) cfg.DryRun = true;
        if (noSync) cfg.NoSync = true;
        if (requireClean) cfg.RequireClean = true;
        if (maxShrinkPct is int mp) cfg.MaxShrinkPctOverride = mp;
        foreach (var p in allowShrink) cfg.ShrinkageOverrides[p] = 100;

        return cfg;
    }

    private static string Next(string[] args, ref int i)
    {
        if (i + 1 >= args.Length) throw new GscriptException($"option '{args[i]}' expects a value");
        return args[++i];
    }

    private static void PrintUsage()
    {
        Console.WriteLine(Version + " - self-healing, cross-platform git-push + dev-ops ceremony");
        Console.WriteLine();
        Console.WriteLine("USAGE:");
        Console.WriteLine("  gscript push [options]");
        Console.WriteLine("  gscript task <post|list|show|approve|reject|run>   (the comms task-bus)");
        Console.WriteLine("  gscript im <lint|digest>                           (the IM index/linter)");
        Console.WriteLine();
        Console.WriteLine("OPTIONS:");
        Console.WriteLine("  --files <a,b,c>      comma-separated paths to stage (explicit; never `git add .`)");
        Console.WriteLine("  --message, -m <msg>  commit message");
        Console.WriteLine("  --no-deploy          append [skip ci]; skip CI watch + probes");
        Console.WriteLine("  --no-watch           push but don't watch CI");
        Console.WriteLine("  --dry-run            run gates + divergence check, then stop (no commit/push)");
        Console.WriteLine("  --no-sync            disable the pre-push auto-fast-forward when origin advanced disjointly");
        Console.WriteLine("  --require-clean      fail if files outside --files are modified/untracked (runner-tree hygiene)");
        Console.WriteLine("  --max-shrink-pct N   relax file-size + markdown shrink gates to N% for this push");
        Console.WriteLine("  --allow-shrink <p>   exempt one path from the shrink gates (repeatable)");
        Console.WriteLine("  --config <path>      gscript.json (default: ./gscript.json if present)");
        Console.WriteLine("  --repo-owner <o>     override repo owner");
        Console.WriteLine("  --repo-name <r>      override repo name");
        Console.WriteLine("  --workflow <f>       CI workflow filename (default: deploy.yml)");
        Console.WriteLine("  --working-dir <d>    repo root (default: current directory)");
        Console.WriteLine("  --localmd <path>     localmd/PAT path (default: %USERPROFILE%\\private\\local.md; or gscript.json localmdPath/patFile)");
        Console.WriteLine("  --log <path>         append a markdown push-log entry per successful push (or gscript.json logFile)");
        Console.WriteLine("  -h, --help           this help");
        Console.WriteLine("  -v, --version        version");
        Console.WriteLine();
        Console.WriteLine("Repo-level config (owner, gates, probes, visibility) lives in gscript.json;");
        Console.WriteLine("per-sprint data (files, message) is given on the CLI or in gscript.json.");
    }
}
