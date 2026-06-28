using System.Numerics;

namespace Intoner.Objects.Utils;

internal static class ObjectFolderUtility
{
    public static string SanitizeFolderPath(string? folderPath)
        => string.IsNullOrWhiteSpace(folderPath)
            ? string.Empty
            : string.Join(
                "/",
                folderPath
                    .Replace('\\', '/')
                    .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    public static IReadOnlyList<string> OrderFolders(IEnumerable<string> folderPaths)
        => folderPaths
            .Select(SanitizeFolderPath)
            .Where(static folderPath => !string.IsNullOrWhiteSpace(folderPath))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static folderPath => folderPath, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static bool FolderListsMatch(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left.Count != right.Count)
        {
            return false;
        }

        for (var i = 0; i < left.Count; ++i)
        {
            if (!string.Equals(left[i], right[i], StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    public static string SanitizeFolderColorValue(string? colorValue)
    {
        if (!TryParseFolderColorValue(colorValue, out var parsedColor))
        {
            return string.Empty;
        }

        return FormatFolderColorValue(parsedColor);
    }

    public static IReadOnlyDictionary<string, string> OrderFolderColorMap(
        IEnumerable<KeyValuePair<string, string>> folderColors,
        IEnumerable<string>? validFolders = null)
    {
        var validFolderSet = validFolders is null
            ? null
            : new HashSet<string>(OrderFolders(validFolders), StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> orderedColors = new(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in folderColors
                     .Select(entry => new KeyValuePair<string, string>(SanitizeFolderPath(entry.Key), SanitizeFolderColorValue(entry.Value)))
                     .Where(static entry => !string.IsNullOrWhiteSpace(entry.Key) && !string.IsNullOrWhiteSpace(entry.Value))
                     .OrderBy(static entry => entry.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (validFolderSet is not null && !validFolderSet.Contains(entry.Key))
            {
                continue;
            }

            orderedColors[entry.Key] = entry.Value;
        }

        return orderedColors;
    }

    public static bool FolderColorMapsMatch(IReadOnlyDictionary<string, string> left, IReadOnlyDictionary<string, string> right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left.Count != right.Count)
        {
            return false;
        }

        foreach (var entry in left)
        {
            var leftPath = SanitizeFolderPath(entry.Key);
            var leftColorValue = SanitizeFolderColorValue(entry.Value);
            var rightColorValue = GetFolderColorValue(right, leftPath);
            if (!string.Equals(leftColorValue, rightColorValue, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    public static string GetFolderColorValue(IReadOnlyDictionary<string, string> folderColors, string folderPath)
    {
        var sanitizedFolderPath = SanitizeFolderPath(folderPath);
        if (string.IsNullOrWhiteSpace(sanitizedFolderPath))
        {
            return string.Empty;
        }

        if (folderColors.TryGetValue(sanitizedFolderPath, out var colorValue))
        {
            return SanitizeFolderColorValue(colorValue);
        }

        foreach (var entry in folderColors)
        {
            if (string.Equals(entry.Key, sanitizedFolderPath, StringComparison.OrdinalIgnoreCase))
            {
                return SanitizeFolderColorValue(entry.Value);
            }
        }

        return string.Empty;
    }

    public static bool TryParseFolderColorValue(string? colorValue, out Vector4 color)
    {
        if (string.IsNullOrWhiteSpace(colorValue))
        {
            color = default;
            return false;
        }

        if (ObjectColorUtility.TryParseHexColor(colorValue.Trim(), out color))
        {
            return true;
        }

        color = default;
        return false;
    }

    public static string FormatFolderColorValue(Vector4 color)
    {
        var byteColor = ObjectColorUtility.ToByteColor(color);
        return byteColor.A >= 255
            ? $"#{byteColor.R:X2}{byteColor.G:X2}{byteColor.B:X2}"
            : $"#{byteColor.R:X2}{byteColor.G:X2}{byteColor.B:X2}{byteColor.A:X2}";
    }
}

