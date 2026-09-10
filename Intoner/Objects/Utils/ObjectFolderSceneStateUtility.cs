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

        return AddFolders(state, [new ObjectFolderSnapshot(sanitizedFolderPath)]);
    }

    public static ObjectFolderSceneState AddFolders(
        ObjectFolderSceneState state,
        IEnumerable<ObjectFolderSnapshot> folders)
    {
        IReadOnlyList<ObjectFolderSnapshot> currentFolders = state.DefaultLayoutId.HasValue
            ? state.DefaultLayoutFolders
            : state.StandaloneFolders;
        IReadOnlyList<ObjectFolderSnapshot> nextFolders = ObjectFolderUtility.OrderFolderEntries(currentFolders.Concat(folders));

        return state.DefaultLayoutId.HasValue
            ? state with
            {
                DefaultLayoutFolders = nextFolders,
            }
            : state with
            {
                StandaloneFolders = nextFolders,
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
            StandaloneFolders = ApplyFolderColor(
                nextState.StandaloneFolders,
                sanitizedFolderPath,
                sanitizedColorValue),
            DefaultLayoutFolders = ApplyFolderColor(
                nextState.DefaultLayoutFolders,
                sanitizedFolderPath,
                sanitizedColorValue),
        };
    }

    public static bool StatesMatch(ObjectFolderSceneState left, ObjectFolderSceneState right)
        => left.DefaultLayoutId == right.DefaultLayoutId
            && ObjectFolderUtility.FolderEntriesMatch(left.StandaloneFolders, right.StandaloneFolders)
            && ObjectFolderUtility.FolderEntriesMatch(left.DefaultLayoutFolders, right.DefaultLayoutFolders);

    private static IReadOnlyList<ObjectFolderSnapshot> ApplyFolderColor(
        IReadOnlyList<ObjectFolderSnapshot> folders,
        string folderPath,
        string colorValue)
    {
        string? color = colorValue.Length > 0 ? colorValue : null;
        return ObjectFolderUtility.OrderFolderEntries(folders.Select(folder =>
            string.Equals(ObjectFolderUtility.SanitizeFolderPath(folder.Path), folderPath, StringComparison.OrdinalIgnoreCase)
                ? folder with { Color = color }
                : folder));
    }

    private static ObjectFolderSceneState TransformFolderPaths(
        ObjectFolderSceneState state,
        Func<string, string?> transform,
        Func<string, bool>? includeColor = null)
        => state with
        {
            StandaloneFolders = TransformFolderSet(state.StandaloneFolders, transform, includeColor),
            DefaultLayoutFolders = TransformFolderSet(state.DefaultLayoutFolders, transform, includeColor),
        };

    private static IReadOnlyList<ObjectFolderSnapshot> TransformFolderSet(
        IReadOnlyList<ObjectFolderSnapshot> folders,
        Func<string, string?> transform,
        Func<string, bool>? includeColor)
    {
        List<ObjectFolderSnapshot> transformedFolders = [];
        foreach (ObjectFolderSnapshot folder in folders)
        {
            string sourcePath = ObjectFolderUtility.SanitizeFolderPath(folder.Path);
            string destinationPath = ObjectFolderUtility.SanitizeFolderPath(transform(sourcePath));
            if (destinationPath.Length == 0)
            {
                continue;
            }

            transformedFolders.Add(new ObjectFolderSnapshot(
                destinationPath,
                (includeColor?.Invoke(sourcePath) ?? true) ? folder.Color : null));
        }

        return ObjectFolderUtility.OrderFolderEntries(transformedFolders);
    }
}

