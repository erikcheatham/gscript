using System.Text.Json;
using System.Text.Json.Serialization;

namespace Gscript.Local;

/// <summary>
/// The MACHINE-level config: <c>%APPDATA%\gscript\config.json</c> (Windows) or
/// <c>~/.config/gscript/config.json</c> (POSIX), holding facts that are true of the machine
/// rather than of any repo — today, exactly one: <c>localmdPath</c>, where the operator's
/// private localmd actually lives.
///
/// <para>Why this tier exists (alpha.17): this is a public tree, so the operator's real localmd
/// location can never be baked into source, and the compiled-in default is profile-relative
/// (<see cref="Localmd.DefaultPath"/>). An operator whose private folder has been RELOCATED could
/// record the real path in each repo's <c>gscript.json</c> — but that file is read from the
/// CURRENT DIRECTORY, so any verb run from a directory without one (the task bus especially,
/// which is machine-global by nature) silently fell through to the stale profile-relative
/// default and reported an empty world. A machine fact belongs in a machine file: the public
/// tree holds the MECHANISM (a well-known, profile-relative location), the machine holds the
/// FACT (the value inside it). A fresh clone without the file gets the old behavior untouched.</para>
///
/// <para>This is a POINTER, not a secret — the same class as <c>GSCRIPT_TASKS_DIR</c>: it cannot
/// go stale in a way that produces a mystery 401, and it never enters any repo. Written once per
/// machine by <c>gscript config set localmdPath &lt;path&gt;</c>; read fresh on every resolve
/// (no cache — rotation-by-edit, same doctrine as the PAT).</para>
/// </summary>
public sealed class MachineConfig
{
    [JsonPropertyName("localmdPath")] public string? LocalmdPath { get; set; }

    /// <summary>Full path of the machine config file. Profile-relative on every OS; never a
    /// literal absolute path in source (public-tree rule).</summary>
    public static string FilePath()
    {
        // ApplicationData = %APPDATA% on Windows, $XDG_CONFIG_HOME / ~/.config on POSIX.
        var root = Environment.GetFolderPath(
            Environment.SpecialFolder.ApplicationData,
            Environment.SpecialFolderOption.DoNotVerify);
        return Path.Combine(root, "gscript", "config.json");
    }

    /// <summary>
    /// Load the machine config, or null when the file doesn't exist (the fresh-clone case —
    /// callers fall through to the compiled-in default). An EXISTING file that fails to parse
    /// throws: the operator wrote it on purpose, so silently ignoring it would resurrect the
    /// exact stale-path behavior this tier was built to end.
    /// </summary>
    public static MachineConfig? TryLoad()
    {
        var path = FilePath();
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<MachineConfig>(
                File.ReadAllText(path),
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    ReadCommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                }) ?? throw new GscriptException($"machine config parse returned null: {path}");
        }
        catch (JsonException ex)
        {
            throw new GscriptException(
                $"machine config at {path} is unparseable ({ex.Message}). "
                + "Fix it or delete it; re-create with 'gscript config set localmdPath <path>'.");
        }
    }

    /// <summary>Write this config to <see cref="FilePath"/>, creating the directory as needed.</summary>
    public void Save()
    {
        var path = FilePath();
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(
            this, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
    }
}
