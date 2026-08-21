using System.Text.Json;
using System.Text.Json.Serialization;

namespace Atalaya.Inventory;

/// <summary>
/// Per-machine configuration (§4): the local clone paths, which are per-user and must NEVER
/// go into the hub. Persisted at <c>%LOCALAPPDATA%/Atalaya/machines.json</c>.
/// </summary>
public sealed class MachineConfig
{
    public int SchemaVersion { get; set; } = 1;

    public string MachineName { get; set; } = Environment.MachineName;

    /// <summary>appSlug → absolute local clone path on this machine.</summary>
    public Dictionary<string, string> ClonePaths { get; set; } = new(StringComparer.Ordinal);

    public string? ClonePathFor(string slug) => ClonePaths.TryGetValue(slug, out string? p) ? p : null;
}

/// <summary>Reads and writes <see cref="MachineConfig"/> locally (outside the hub).</summary>
public sealed class MachineConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _path;

    public MachineConfigStore(string path) => _path = path;

    /// <summary>Default location under LOCALAPPDATA.</summary>
    public static MachineConfigStore Default()
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Atalaya");
        return new MachineConfigStore(Path.Combine(dir, "machines.json"));
    }

    public MachineConfig Load()
    {
        if (!File.Exists(_path))
        {
            return new MachineConfig();
        }

        try
        {
            return JsonSerializer.Deserialize<MachineConfig>(File.ReadAllText(_path), JsonOptions)
                   ?? new MachineConfig();
        }
        catch
        {
            return new MachineConfig();
        }
    }

    public void Save(MachineConfig config)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, JsonSerializer.Serialize(config, JsonOptions));
    }

    public void SetClonePath(string slug, string clonePath)
    {
        MachineConfig config = Load();
        config.ClonePaths[slug] = clonePath;
        Save(config);
    }
}
