using Intoner.Objects.Models;

namespace Intoner.Objects.Utils;

internal static class ObjectFolderSceneStateUtility
{
    public static ObjectFolderSceneState AddFolder(ObjectFolderSceneState state, string folderPath)
    {
        var sanitizedFolderPath = ObjectFolderUtility.SanitizeFolderPath(folderPath);
        if (string.IsNullOrEmpty(sanitizedFolderPath))
        {
            return state;
        }

        return state.DefaultLayoutId.HasValue
            ? state with
            {
                DefaultLayoutFolders = ObjectFolderUtility.OrderFolders(
                    state.DefaultLayoutFolders.Append(sanitizedFolderPath)),
            }
            : state with
            {
                StandaloneFolders = ObjectFolderUtility.OrderFolders(
                    state.StandaloneFolders.Append(sanitizedFolderPath)),
            };
    }

    public static ObjectFolderSceneState AddFolders(
        ObjectFolderSceneState state,
        IEnumerable<string> folderPaths,
        IReadOnlyDictionary<string, string> folderColors)
    {
        IReadOnlyList<string> currentFolders = state.DefaultLayoutId.HasValue
            ? state.DefaultLayoutFolders
            : state.StandaloneFolders;
        IReadOnlyDictionary<string, string> currentColors = state.DefaultLayoutId.HasValue
            ? state.DefaultLayoutFolderColors
            : state.StandaloneFolderColors;
        IReadOnlyList<string> nextFolders = ObjectFolderUtility.OrderFolders(currentFolders.Concat(folderPaths));
        IReadOnlyDictionary<string, string> nextColors = ObjectFolderUtility.OrderFolderColorMap(
            currentColors.Concat(folderColors.Where(entry => string.IsNullOrEmpty(
                ObjectFolderUtility.GetFolderColorValue(currentColors, entry.Key)))),
            nextFolders);

        return state.DefaultLayoutId.HasValue
            ? state with
            {
                DefaultLayoutFolders = nextFolders,
                DefaultLayoutFolderColors = nextColors,
            }
            : state with
            {
                StandaloneFolders = nextFolders,
                StandaloneFolderColors = nextColors,
            };
    }

    public static ObjectFolderSceneState RenameFolder(ObjectFolderSceneState state, string sourceFolderPath, string nextFolderPath)
    {
        var sanitizedSourceFolderPath = ObjectFolderUtility.SanitizeFolderPath(sourceFolderPath);
        var sanitizedNextFolderPath = ObjectFolderUtility.SanitizeFolderPath(nextFolderPath);
        if (string.IsNullOrEmpty(sanitizedSourceFolderPath)
            || string.IsNullOrEmpty(sanitizedNextFolderPath)
            || string.Equals(sanitizedSourceFolderPath, sanitizedNextFolderPath, StringComparison.OrdinalIgnoreCase))
        {
            return state;
        }

        return TransformFolderPaths(
            state,
            path => ObjectFolderUtility.RebaseFolderPath(
                path,
                sanitizedSourceFolderPath,
                sanitizedNextFolderPath));
    }

    public static ObjectFolderSceneState DissolveFolder(
        ObjectFolderSceneState state,
        string folderPath,
        IReadOnlyDictionary<string, string> pathMap)
    {
        var sanitizedFolderPath = ObjectFolderUtility.SanitizeFolderPath(folderPath);
        if (string.IsNullOrEmpty(sanitizedFolderPath))
        {
            return state;
        }

        return TransformFolderPaths(
            state,
            path => ObjectFolderUtility.ApplyFolderPathMap(path, pathMap),
            path => !string.Equals(path, sanitizedFolderPath, StringComparison.OrdinalIgnoreCase));
    }

    public static ObjectFolderSceneState RemoveFolderSubtree(ObjectFolderSceneState state, string folderPath)
    {
        string sanitizedFolderPath = ObjectFolderUtility.SanitizeFolderPath(folderPath);
        return sanitizedFolderPath.Length == 0
            ? state
            : TransformFolderPaths(
                state,
                path => ObjectFolderUtility.IsSameOrDescendant(path, sanitizedFolderPath) ? null : path);
    }

    public static ObjectFolderSceneState SetFolderColor(ObjectFolderSceneState state, string folderPath, string colorValue)
    {
        var sanitizedFolderPath = ObjectFolderUtility.SanitizeFolderPath(folderPath);
        if (string.IsNullOrEmpty(sanitizedFolderPath))
        {
            return state;
        }

        var sanitizedColorValue = ObjectFolderUtility.SanitizeFolderColorValue(colorValue);
        var nextState = string.IsNullOrEmpty(sanitizedColorValue)
            ? state
            : AddFolder(state, sanitizedFolderPath);
        return nextState with
        {
            StandaloneFolderColors = ApplyFolderColorMapEntry(
                nextState.StandaloneFolderColors,
                nextState.StandaloneFolders,
                sanitizedFolderPath,
                sanitizedColorValue),
            DefaultLayoutFolderColors = ApplyFolderColorMapEntry(
                nextState.DefaultLayoutFolderColors,
                nextState.DefaultLayoutFolders,
                sanitizedFolderPath,
                sanitizedColorValue),
        };
    }

    public static bool StatesMatch(ObjectFolderSceneState left, ObjectFolderSceneState right)
        => left.DefaultLayoutId == right.DefaultLayoutId
            && ObjectFolderUtility.FolderListsMatch(left.StandaloneFolders, right.StandaloneFolders)
            && ObjectFolderUtility.FolderColorMapsMatch(left.StandaloneFolderColors, right.StandaloneFolderColors)
            && ObjectFolderUtility.FolderListsMatch(left.DefaultLayoutFolders, right.DefaultLayoutFolders)
            && ObjectFolderUtility.FolderColorMapsMatch(left.DefaultLayoutFolderColors, right.DefaultLayoutFolderColors);

    private static IReadOnlyDictionary<string, string> ApplyFolderColorMapEntry(
        IReadOnlyDictionary<string, string> folderColors,
        IEnumerable<string> validFolders,
        string folderPath,
        string colorValue)
    {
        var sanitizedFolderPath = ObjectFolderUtility.SanitizeFolderPath(folderPath);
        if (string.IsNullOrEmpty(sanitizedFolderPath))
        {
            return ObjectFolderUtility.OrderFolderColorMap(folderColors, validFolders);
        }

        var nextColors = CreateMutableFolderColorMap(folderColors);

        if (string.IsNullOrEmpty(colorValue))
        {
            nextColors.Remove(sanitizedFolderPath);
        }
        else
        {
            nextColors[sanitizedFolderPath] = colorValue;
        }

        return ObjectFolderUtility.OrderFolderColorMap(nextColors, validFolders);
    }

    private static ObjectFolderSceneState TransformFolderPaths(
        ObjectFolderSceneState state,
        Func<string, string?> transform,
        Func<string, bool>? includeColor = null)
    {
        (IReadOnlyList<string> StandaloneFolders, IReadOnlyDictionary<string, string> StandaloneColors) standalone =
            TransformFolderSet(state.StandaloneFolders, state.StandaloneFolderColors, transform, includeColor);
        (IReadOnlyList<string> LayoutFolders, IReadOnlyDictionary<string, string> LayoutColors) layout =
            TransformFolderSet(state.DefaultLayoutFolders, state.DefaultLayoutFolderColors, transform, includeColor);
        return state with
        {
            StandaloneFolders = standalone.StandaloneFolders,
            StandaloneFolderColors = standalone.StandaloneColors,
            DefaultLayoutFolders = layout.LayoutFolders,
            DefaultLayoutFolderColors = layout.LayoutColors,
        };
    }

    private static (IReadOnlyList<string> Folders, IReadOnlyDictionary<string, string> Colors) TransformFolderSet(
        IReadOnlyList<string> folders,
        IReadOnlyDictionary<string, string> colors,
        Func<string, string?> transform,
        Func<string, bool>? includeColor)
    {
        List<string> transformedFolders = [];
        Dictionary<string, string> transformedColors = new(StringComparer.OrdinalIgnoreCase);
        foreach (string folder in folders)
        {
            string sourcePath = ObjectFolderUtility.SanitizeFolderPath(folder);
            string destinationPath = ObjectFolderUtility.SanitizeFolderPath(transform(sourcePath));
            if (destinationPath.Length == 0)
            {
                continue;
            }

            transformedFolders.Add(destinationPath);
            string color = ObjectFolderUtility.GetFolderColorValue(colors, sourcePath);
            if (color.Length > 0 && (includeColor?.Invoke(sourcePath) ?? true))
            {
                transformedColors.TryAdd(destinationPath, color);
            }
        }

        IReadOnlyList<string> orderedFolders = ObjectFolderUtility.OrderFolders(transformedFolders);
        return (orderedFolders, ObjectFolderUtility.OrderFolderColorMap(transformedColors, orderedFolders));
    }

    private static Dictionary<string, string> CreateMutableFolderColorMap(IReadOnlyDictionary<string, string> folderColors)
    {
        Dictionary<string, string> nextColors = new(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in folderColors)
        {
            nextColors[ObjectFolderUtility.SanitizeFolderPath(entry.Key)] = ObjectFolderUtility.SanitizeFolderColorValue(entry.Value);
        }

        return nextColors;
    }
}

