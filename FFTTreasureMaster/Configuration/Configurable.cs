using System;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Reloaded.Mod.Interfaces;

namespace FFTTreasureMaster.Configuration;

/// <summary>
/// Base for mod config types, mirroring the Reloaded-II mod template. FromFile constructs or
/// loads an instance, Save writes it back, and IUpdatableConfigurable ties it to the launcher
/// property grid through Configurator.
/// </summary>
public class Configurable<TParentType> : IUpdatableConfigurable
    where TParentType : Configurable<TParentType>, new()
{
    public static JsonSerializerOptions SerializerOptions { get; } = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    // ── IConfigurable / IUpdatableConfigurable ───────────────────────────────

    [Browsable(false)]
    public event Action<IUpdatableConfigurable>? ConfigurationUpdated { add { } remove { } }

    [Browsable(false)]
    [JsonIgnore]
    public string? FilePath { get; set; }

    [Browsable(false)]
    [JsonIgnore]
    public string? ConfigName { get; set; }

    [Browsable(false)]
    [JsonIgnore]
    public string? LoadError { get; private set; }

    private JsonObject? _opened;

    Action IConfigurable.Save => Save;

    // ── static factory ────────────────────────────────────────────────────────

    /// <summary>
    /// Load from <paramref name="filePath"/> if it exists, otherwise create a default
    /// instance and write the defaults to disk. Arms nothing, so a later change to the
    /// file never reaches this object.
    /// </summary>
    public static TParentType FromFile(string filePath, string configName)
    {
        var result = File.Exists(filePath) ? Read(filePath) : new TParentType();
        result.FilePath = filePath;
        result.ConfigName = configName;
        if (!File.Exists(filePath))
            result.Save();
        result._opened = result.Snapshot();
        return result;
    }

    internal static TParentType Read(string filePath)
    {
        try
        {
            var json = File.ReadAllText(filePath);
            var parsed = JsonSerializer.Deserialize<TParentType>(json, SerializerOptions);
            if (parsed != null) return parsed;
            return new TParentType { LoadError = "the file holds a JSON null instead of a settings object" };
        }
        catch (Exception ex)
        {
            return new TParentType { LoadError = ex.Message };
        }
    }

    // ── instance methods ──────────────────────────────────────────────────────

    private JsonObject Snapshot() =>
        JsonSerializer.SerializeToNode((TParentType)this, SerializerOptions)!.AsObject();

    // Closing the Configure window saves too, so only fields changed in it are written.
    public void Save()
    {
        if (string.IsNullOrEmpty(FilePath)) return;
        var now = Snapshot();
        if (!File.Exists(FilePath) || _opened == null)
        {
            WriteWhole(now);
            return;
        }
        var changed = new JsonObject();
        foreach (var prop in now)
            if (!JsonNode.DeepEquals(prop.Value, _opened[prop.Key]))
                changed[prop.Key] = prop.Value?.DeepClone();
        if (changed.Count == 0) return;
        if (ConfigFields.Replace(FilePath, changed) == FieldWrite.Written) _opened = now;
        else WriteWhole(now);
    }

    private void WriteWhole(JsonObject now)
    {
        var dir = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(FilePath!, now.ToJsonString(SerializerOptions));
        _opened = now;
    }
}
