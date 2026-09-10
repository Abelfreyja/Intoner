using Intoner.Objects.Models;
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

    public static string GetFolderName(string? folderPath)
    {
        string path = SanitizeFolderPath(folderPath);
        int separatorIndex = path.LastIndexOf('/');
        return separatorIndex < 0 ? path : path[(separatorIndex + 1)..];
    }

    public static string GetParentFolderPath(string? folderPath)
    {
        string path = SanitizeFolderPath(folderPath);
        int separatorIndex = path.LastIndexOf('/');
        return separatorIndex < 0 ? string.Empty : path[..separatorIndex];
    }

    public static string CombineFolderPath(string? parentPath, string? folderName)
        => SanitizeFolderPath($"{SanitizeFolderPath(parentPath)}/{folderName?.Trim()}");

    public static bool IsDescendantOf(string? candidatePath, string? ancestorPath)
    {
        string candidate = SanitizeFolderPath(candidatePath);
        string ancestor = SanitizeFolderPath(ancestorPath);
        return candidate.Length > ancestor.Length && IsSameOrDescendantPath(candidate, ancestor);
    }

    public static bool IsSameOrDescendant(string? candidatePath, string? ancestorPath)
    {
        string candidate = SanitizeFolderPath(candidatePath);
        string ancestor = SanitizeFolderPath(ancestorPath);
        return IsSameOrDescendantPath(candidate, ancestor);
    }

    private static bool IsSameOrDescendantPath(string candidate, string ancestor)
        => ancestor.Length > 0
        && candidate.StartsWith(ancestor, StringComparison.OrdinalIgnoreCase)
        && (candidate.Length == ancestor.Length || candidate[ancestor.Length] == '/');

    public static string RebaseFolderPath(string? folderPath, string? sourcePath, string? destinationPath)
    {
        string path = SanitizeFolderPath(folderPath);
        string source = SanitizeFolderPath(sourcePath);
        string destination = SanitizeFolderPath(destinationPath);
        if (!IsSameOrDescendantPath(path, source))
        {
            return path;
        }

        if (path.Length == source.Length)
        {
            return destination;
        }

        string relative = path[(source.Length + 1)..];
        return destination.Length == 0 ? relative : $"{destination}/{relative}";
    }

    public static IReadOnlyDictionary<string, string> CreateDissolveFolderPathMap(
        string folderPath,
        IEnumerable<string> sceneFolders)
    {
        string source = SanitizeFolderPath(folderPath);
        Dictionary<string, string> pathMap = new(StringComparer.OrdinalIgnoreCase);
        if (source.Length == 0)
        {
            return pathMap;
        }

        IReadOnlyList<string> folders = ExpandFolders(sceneFolders.Append(source));
        string parent = GetParentFolderPath(source);
        pathMap.Add(source, parent);
        HashSet<string> occupied = folders
            .Where(path => !IsSameOrDescendant(path, source))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string child in folders.Where(path => string.Equals(
                     GetParentFolderPath(path),
                     source,
                     StringComparison.OrdinalIgnoreCase)))
        {
            string destination = ResolveAvailableFolderPath(
                CombineFolderPath(parent, GetFolderName(child)),
                occupied);
            foreach (string descendant in folders.Where(path => IsSameOrDescendant(path, child)))
            {
                string mappedPath = RebaseFolderPath(descendant, child, destination);
                pathMap.Add(descendant, mappedPath);
                _ = occupied.Add(mappedPath);
            }
        }

        return pathMap;
    }

    public static string ApplyFolderPathMap(
        string? folderPath,
        IReadOnlyDictionary<string, string> pathMap)
    {
        string path = SanitizeFolderPath(folderPath);
        return pathMap.TryGetValue(path, out string? mappedPath) ? mappedPath : path;
    }

    public static IReadOnlyList<string> EnumerateFolderAncestors(string? folderPath, bool includeSelf = false)
    {
        string path = SanitizeFolderPath(folderPath);
        if (path.Length == 0)
        {
            return [];
        }

        string[] segments = path.Split('/');
        int count = includeSelf ? segments.Length : segments.Length - 1;
        string[] ancestors = new string[Math.Max(0, count)];
        for (int index = 0; index < ancestors.Length; ++index)
        {
            ancestors[index] = string.Join('/', segments, 0, index + 1);
        }

        return ancestors;
    }

    public static IReadOnlyList<string> OrderFolders(IEnumerable<string> folderPaths)
        => CreateFolderSet(folderPaths)
            .OrderBy(static folderPath => folderPath, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static HashSet<string> CreateFolderSet(IEnumerable<string> folderPaths)
        => folderPaths
            .Select(SanitizeFolderPath)
            .Where(static folderPath => folderPath.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<string> ExpandFolders(IEnumerable<string> folderPaths)
        => OrderFolders(folderPaths.SelectMany(static path => EnumerateFolderAncestors(path, includeSelf: true)));

    public static IReadOnlyList<ObjectFolderSnapshot> OrderFolderEntries(IEnumerable<ObjectFolderSnapshot> folders)
    {
        Dictionary<string, ObjectFolderSnapshot> entries = new(StringComparer.OrdinalIgnoreCase);
        foreach (ObjectFolderSnapshot folder in folders)
        {
            string path = SanitizeFolderPath(folder.Path);
            if (path.Length == 0)
            {
                continue;
            }

            string color = SanitizeFolderColorValue(folder.Color);
            if (!entries.TryGetValue(path, out ObjectFolderSnapshot? existing))
            {
                entries.Add(path, new ObjectFolderSnapshot(path, color.Length > 0 ? color : null));
            }
            else if (existing.Color is null && color.Length > 0)
            {
                // the first explicit color wins when folder sources overlap
                entries[path] = existing with { Color = color };
            }
        }

        return entries.Values.OrderBy(static folder => folder.Path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static IReadOnlyList<ObjectFolderSnapshot> ExpandFolderEntries(IEnumerable<ObjectFolderSnapshot> folders)
    {
        List<ObjectFolderSnapshot> entries = [];
        foreach (ObjectFolderSnapshot folder in folders)
        {
            entries.Add(folder);
            foreach (string parent in EnumerateFolderAncestors(folder.Path))
            {
                entries.Add(new ObjectFolderSnapshot(parent));
            }
        }

        return OrderFolderEntries(entries);
    }

    public static bool FolderEntriesMatch(IReadOnlyList<ObjectFolderSnapshot> left, IReadOnlyList<ObjectFolderSnapshot> right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left.Count != right.Count)
        {
            return false;
        }

        for (int index = 0; index < left.Count; ++index)
        {
            if (!string.Equals(left[index].Path, right[index].Path, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(left[index].Color, right[index].Color, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    public static IReadOnlyList<ObjectFolderSnapshot> FromFolderColorMap(
        IEnumerable<string> paths,
        IReadOnlyDictionary<string, string> colors)
    {
        Dictionary<string, string> normalizedColors = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string key, string value) in colors)
        {
            string path = SanitizeFolderPath(key);
            string color = SanitizeFolderColorValue(value);
            if (path.Length > 0 && color.Length > 0)
            {
                normalizedColors[path] = color;
            }
        }

        return OrderFolderEntries(paths.Select(path => new ObjectFolderSnapshot(
            path, normalizedColors.GetValueOrDefault(SanitizeFolderPath(path)))));
    }

    public static IReadOnlyDictionary<string, string> ToFolderColorMap(IEnumerable<ObjectFolderSnapshot> folders)
    {
        Dictionary<string, string> colors = new(StringComparer.OrdinalIgnoreCase);
        foreach (ObjectFolderSnapshot folder in folders.Where(static folder => !string.IsNullOrEmpty(folder.Color)))
        {
            colors[folder.Path] = folder.Color!;
        }

        return colors;
    }

    public static string ResolveAvailableFolderPath(string preferredPath, IEnumerable<string> existingPaths)
        => ResolveAvailableFolderPath(
            preferredPath,
            CreateFolderSet(existingPaths));

    private static string ResolveAvailableFolderPath(string preferredPath, IReadOnlySet<string> existingPaths)
    {
        string preferred = SanitizeFolderPath(preferredPath);
        if (preferred.Length == 0)
        {
            return string.Empty;
        }

        if (!existingPaths.Contains(preferred))
        {
            return preferred;
        }

        int suffix = 2;
        string candidate = $"{preferred} ({suffix})";
        while (existingPaths.Contains(candidate))
        {
            candidate = $"{preferred} ({++suffix})";
        }

        return candidate;
    }

    public static string SanitizeFolderColorValue(string? colorValue)
    {
        if (!TryParseFolderColorValue(colorValue, out var parsedColor))
        {
            return string.Empty;
        }

        return FormatFolderColorValue(parsedColor);
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

        if (ColorUtility.TryParseHexColor(colorValue.Trim(), out color))
        {
            return true;
        }

        color = default;
        return false;
    }

    public static string FormatFolderColorValue(Vector4 color)
    {
        var byteColor = ColorUtility.ToByteColor(color);
        return byteColor.A >= 255
            ? $"#{byteColor.R:X2}{byteColor.G:X2}{byteColor.B:X2}"
            : $"#{byteColor.R:X2}{byteColor.G:X2}{byteColor.B:X2}{byteColor.A:X2}";
    }
}

