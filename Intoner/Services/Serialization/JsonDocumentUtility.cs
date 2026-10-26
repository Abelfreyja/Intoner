using System.Text;
using System.Text.Json;
using Intoner.Services.Storage;

namespace Intoner.Services.Serialization;

internal static class JsonDocumentUtility
{
    internal const long MaximumFileBytes = 64 * 1024 * 1024;

    internal static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        AllowDuplicateProperties = false,
    };

    public static string ReadFile(IPluginFileSystem files, string path)
        => files.ReadAllText(path, MaximumFileBytes);

    public static void WriteFile(IPluginFileSystem files, string path, string json)
    {
        ValidateFileSize(json);
        files.WriteAllTextAtomic(path, json);
    }

    public static void ValidateFileSize(string json)
    {
        if (Encoding.UTF8.GetByteCount(json) > MaximumFileBytes)
        {
            throw new InvalidDataException($"The file exceeds the {MaximumFileBytes} byte limit.");
        }
    }

    public static JsonDocument Parse(string json)
        => JsonDocument.Parse(json, DocumentOptions);

    public static bool TryGetPropertyIgnoreCase(this JsonElement root, string name, out JsonElement value)
    {
        value = default;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        bool found = false;
        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (found)
            {
                throw new JsonException($"Duplicate property '{name}'.");
            }

            found = true;
            value = property.Value;
        }

        return found;
    }

    public static JsonElement GetPropertyIgnoreCase(this JsonElement root, string name)
        => root.TryGetPropertyIgnoreCase(name, out JsonElement value)
            ? value
            : throw new JsonException($"Missing property '{name}'.");
}
