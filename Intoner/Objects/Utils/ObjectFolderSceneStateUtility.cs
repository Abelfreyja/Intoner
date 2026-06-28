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

        var standaloneRenamed = false;
        var layoutRenamed = false;
        var nextStandaloneFolders = state.StandaloneFolders
            .Select(folder =>
            {
                if (string.Equals(folder, sanitizedSourceFolderPath, StringComparison.OrdinalIgnoreCase))
                {
                    standaloneRenamed = true;
                    return sanitizedNextFolderPath;
                }

                return folder;
            });
        var nextLayoutFolders = state.DefaultLayoutFolders
            .Select(folder =>
            {
                if (string.Equals(folder, sanitizedSourceFolderPath, StringComparison.OrdinalIgnoreCase))
                {
                    layoutRenamed = true;
                    return sanitizedNextFolderPath;
                }

                return folder;
            });
        var nextStandaloneFolderColors = RenameFolderColorMapEntry(
            state.StandaloneFolderColors,
            state.StandaloneFolders,
            sanitizedSourceFolderPath,
            sanitizedNextFolderPath);
        var nextDefaultLayoutFolderColors = RenameFolderColorMapEntry(
            state.DefaultLayoutFolderColors,
            state.DefaultLayoutFolders,
            sanitizedSourceFolderPath,
            sanitizedNextFolderPath);

        var renamedState = state with
        {
            StandaloneFolders = ObjectFolderUtility.OrderFolders(nextStandaloneFolders),
            StandaloneFolderColors = nextStandaloneFolderColors,
            DefaultLayoutFolders = ObjectFolderUtility.OrderFolders(nextLayoutFolders),
            DefaultLayoutFolderColors = nextDefaultLayoutFolderColors,
        };
        if (standaloneRenamed || layoutRenamed)
        {
            return renamedState;
        }

        return AddFolder(renamedState, sanitizedNextFolderPath);
    }

    public static ObjectFolderSceneState RemoveFolder(ObjectFolderSceneState state, string folderPath)
    {
        var sanitizedFolderPath = ObjectFolderUtility.SanitizeFolderPath(folderPath);
        if (string.IsNullOrEmpty(sanitizedFolderPath))
        {
            return state;
        }

        return state with
        {
            StandaloneFolders = ObjectFolderUtility.OrderFolders(
                state.StandaloneFolders.Where(folder => !string.Equals(folder, sanitizedFolderPath, StringComparison.OrdinalIgnoreCase))),
            StandaloneFolderColors = RemoveFolderColorMapEntry(
                state.StandaloneFolderColors,
                state.StandaloneFolders.Where(folder => !string.Equals(folder, sanitizedFolderPath, StringComparison.OrdinalIgnoreCase)),
                sanitizedFolderPath),
            DefaultLayoutFolders = ObjectFolderUtility.OrderFolders(
                state.DefaultLayoutFolders.Where(folder => !string.Equals(folder, sanitizedFolderPath, StringComparison.OrdinalIgnoreCase))),
            DefaultLayoutFolderColors = RemoveFolderColorMapEntry(
                state.DefaultLayoutFolderColors,
                state.DefaultLayoutFolders.Where(folder => !string.Equals(folder, sanitizedFolderPath, StringComparison.OrdinalIgnoreCase)),
                sanitizedFolderPath),
        };
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

    private static IReadOnlyDictionary<string, string> RenameFolderColorMapEntry(
        IReadOnlyDictionary<string, string> folderColors,
        IEnumerable<string> validFolders,
        string sourceFolderPath,
        string nextFolderPath)
    {
        var sourceColorValue = ObjectFolderUtility.GetFolderColorValue(folderColors, sourceFolderPath);
        var nextColors = RemoveFolderColorMapEntry(folderColors, validFolders, sourceFolderPath);
        if (string.IsNullOrEmpty(sourceColorValue))
        {
            return nextColors;
        }

        return ApplyFolderColorMapEntry(nextColors, validFolders, nextFolderPath, sourceColorValue);
    }

    private static IReadOnlyDictionary<string, string> RemoveFolderColorMapEntry(
        IReadOnlyDictionary<string, string> folderColors,
        IEnumerable<string> validFolders,
        string folderPath)
    {
        var sanitizedFolderPath = ObjectFolderUtility.SanitizeFolderPath(folderPath);
        var nextColors = CreateMutableFolderColorMap(folderColors);
        foreach (var entry in folderColors.Keys)
        {
            var path = ObjectFolderUtility.SanitizeFolderPath(entry);
            if (!string.Equals(path, sanitizedFolderPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            nextColors.Remove(path);
        }

        return ObjectFolderUtility.OrderFolderColorMap(nextColors, validFolders);
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

