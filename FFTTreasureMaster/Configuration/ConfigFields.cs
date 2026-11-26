using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FFTTreasureMaster.Configuration;

internal enum FieldWrite { Written, Missing, Unparsable, NotObject }

/// <summary>
/// Sets named fields in the settings file and keeps every other field exactly as it was.
/// </summary>
internal static class ConfigFields
{
    internal static FieldWrite Replace(string path, JsonObject fields)
    {
        if (!File.Exists(path)) return FieldWrite.Missing;
        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(File.ReadAllText(path));
            _ = (parsed as JsonObject)?.Count;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            return FieldWrite.Unparsable;
        }
        if (parsed is not JsonObject root) return FieldWrite.NotObject;
        foreach (var field in fields)
            root[field.Key] = field.Value?.DeepClone();
        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return FieldWrite.Written;
    }
}
