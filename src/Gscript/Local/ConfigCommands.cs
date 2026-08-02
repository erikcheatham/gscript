using Gscript.Tasks;

namespace Gscript.Local;

/// <summary>
/// <c>gscript config</c> — read-back and single-writer for the machine-level config
/// (<see cref="MachineConfig"/>). Two subcommands, in the <c>cred test</c> spirit:
/// <c>show</c> prints the file, the value, and what actually RESOLVES from it (localmd default +
/// task directory), so a wrong setup is visible before it bites; <c>set localmdPath &lt;path&gt;</c>
/// writes the one machine fact, refusing a path that doesn't exist (writing a stale pointer is
/// the exact bug this tier retires).
/// </summary>
public static class ConfigCommands
{
    public static int Run(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            PrintUsage();
            return args.Length == 0 ? 1 : 0;
        }

        switch (args[0])
        {
            case "show": return Show();
            case "set": return Set(args[1..]);
            default:
                Log.Red($"gscript config: unknown subcommand '{args[0]}'. Try 'show' or 'set localmdPath <path>'.");
                return 1;
        }
    }

    private static int Show()
    {
        var file = MachineConfig.FilePath();
        Console.WriteLine($"machine config: {file} {(File.Exists(file) ? "" : "(not present — compiled-in defaults apply)")}");

        var cfg = MachineConfig.TryLoad();   // throws with a fix-it message if present-but-broken
        Console.WriteLine($"  localmdPath : {cfg?.LocalmdPath ?? "(unset)"}");
        Console.WriteLine();
        Console.WriteLine("effective resolution (any verb, any directory):");

        var localmd = Localmd.DefaultPath();
        Console.WriteLine($"  localmd default : {localmd} {(File.Exists(localmd) || Directory.Exists(Path.GetDirectoryName(localmd) ?? "") ? "" : "MISSING")}");

        var env = Environment.GetEnvironmentVariable(TaskBus.DirEnvVar);
        if (!string.IsNullOrWhiteSpace(env))
            Console.WriteLine($"  NOTE: {TaskBus.DirEnvVar} is set and overrides the task directory below.");
        if (File.Exists("gscript.json"))
            Console.WriteLine("  NOTE: ./gscript.json exists here; its localmdPath (if set) overrides for THIS directory.");

        var tasks = TaskBus.ResolveDir();
        Console.WriteLine($"  task directory  : {tasks} {(Directory.Exists(tasks) ? "" : "MISSING (task list would be empty here)")}");
        return 0;
    }

    private static int Set(string[] args)
    {
        if (args.Length != 2 || !string.Equals(args[0], "localmdPath", StringComparison.OrdinalIgnoreCase))
        {
            Log.Red("gscript config set: usage is 'gscript config set localmdPath <path>' (the only machine key today).");
            return 1;
        }

        var value = Path.GetFullPath(args[1]);
        if (!Directory.Exists(value) && !File.Exists(value))
        {
            Log.Red($"gscript config set: '{value}' does not exist. A machine config exists to END stale paths, not to write one.");
            return 1;
        }

        var cfg = MachineConfig.TryLoad() ?? new MachineConfig();
        cfg.LocalmdPath = value;
        cfg.Save();

        Console.WriteLine($"wrote {MachineConfig.FilePath()}");
        Console.WriteLine($"  localmdPath = {value}");
        Console.WriteLine($"  task directory now resolves to: {TaskBus.ResolveDir()}");
        return 0;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("gscript config — the machine-level config (a pointer, not a secret; never enters any repo)");
        Console.WriteLine();
        Console.WriteLine("USAGE:");
        Console.WriteLine("  gscript config show                     print the file, the value, and what resolves from it");
        Console.WriteLine("  gscript config set localmdPath <path>   record where the private localmd lives on THIS machine");
        Console.WriteLine();
        Console.WriteLine("Resolution order everywhere: explicit flag/env > ./gscript.json (repo) > machine config > profile default.");
    }
}
