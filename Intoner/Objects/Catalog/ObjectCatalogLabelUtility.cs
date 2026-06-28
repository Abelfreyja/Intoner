using Intoner.Objects.Utils;

namespace Intoner.Objects.Catalog;

internal static class ObjectCatalogLabelUtility
{
    public static string BuildPathLabel(string source, string path)
    {
        var fileName = Path.GetFileNameWithoutExtension(path);
        var name = NormalizeLabel(fileName);
        return string.IsNullOrWhiteSpace(name)
            ? source
            : name;
    }

    public static string NormalizeLabel(string? text)
    {
        string label = ObjectStringUtility.TrimOrEmpty(text);
        return label.Length == 0
            ? string.Empty
            : ObjectStringUtility.TrimOrEmpty(label.Replace('\n', ' ').Replace('\r', ' '));
    }

    public static string NormalizeFurnitureCategoryLabel(string? text)
    {
        string label = NormalizeLabel(text);
        return label.ToLowerInvariant() switch
        {
            "outdoor furnishings" => "Outdoor Furnishing",
            "tables"              => "Table",
            "rugs"                => "Rug",
            _                     => label,
        };
    }
}

