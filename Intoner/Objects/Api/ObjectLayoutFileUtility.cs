using Intoner.Objects.Utils;
using System.Text.Json;

namespace Intoner.Objects.Api;

internal static class ObjectLayoutFileUtility
{
    public static string ResolveImportedLayoutName(string fileName, string path)
    {
        string sanitizedName = string.IsNullOrWhiteSpace(fileName)
            ? Path.GetFileNameWithoutExtension(path)
            : ObjectStringUtility.TrimOrEmpty(fileName);
        return string.IsNullOrWhiteSpace(sanitizedName)
            ? "Imported Layout"
            : sanitizedName;
    }

    public static string BuildExportFileName(string layoutName)
    {
        string sanitizedName = string.IsNullOrWhiteSpace(layoutName)
            ? "layout"
            : string.Join("_", layoutName.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (string.IsNullOrWhiteSpace(sanitizedName))
        {
            sanitizedName = "layout";
        }

        return sanitizedName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            ? sanitizedName
            : $"{sanitizedName}.json";
    }

    public static bool LooksLikeObjectLayout(JsonElement root)
        => root.ValueKind == JsonValueKind.Object
           && root.TryGetProperty(nameof(ObjectLayoutFileDocument.DocumentKind), out JsonElement documentKind)
           && documentKind.ValueKind == JsonValueKind.String
           && string.Equals(documentKind.GetString(), ObjectLayoutFileDocument.DocumentKindValue, StringComparison.Ordinal)
           && root.TryGetProperty(nameof(ObjectLayoutFileDocument.FormatVersion), out _)
           && root.TryGetProperty(nameof(ObjectLayoutFileDocument.Objects), out _);

    public static bool LooksLikeMakePlaceLayout(JsonElement root)
        => root.ValueKind == JsonValueKind.Object
           && (root.TryGetProperty("interiorFurniture", out _)
               || root.TryGetProperty("exteriorFurniture", out _));
}

