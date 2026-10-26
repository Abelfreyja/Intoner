using Intoner.Objects.Models;

namespace Intoner.Objects.Utils;

internal static class ObjectFolderSceneStateUtility
{
    public static IReadOnlyList<ObjectFolderSnapshot> GetFolders(ObjectFolderSceneState state, Guid? layoutId)
        => layoutId.HasValue ? state.LayoutFolders.GetValueOrDefault(layoutId.Value, []) : state.StandaloneFolders;

    private static ObjectFolderSceneState WithFolders(
        ObjectFolderSceneState state,
        Guid? layoutId,
        IReadOnlyList<ObjectFolderSnapshot> folders)
    {
        if (!layoutId.HasValue)
        {
            return state with { StandaloneFolders = folders };
        }

        Dictionary<Guid, IReadOnlyList<ObjectFolderSnapshot>> owners = new(state.LayoutFolders)
        {
            [layoutId.Value] = folders,
        };
        return state with { LayoutFolders = owners };
    }

    public static ObjectFolderSceneState AddFolder(ObjectFolderSceneState state, Guid? layoutId, string folderPath)
    {
        var sanitizedFolderPath = ObjectFolderUtility.SanitizeFolderPath(folderPath);
        if (string.IsNullOrEmpty(sanitizedFolderPath))
        {
            return state;
        }

        return AddFolders(state, layoutId, [new ObjectFolderSnapshot(sanitizedFolderPath)]);
    }

    public static ObjectFolderSceneState AddFolders(
        ObjectFolderSceneState state,
        Guid? layoutId,
        IEnumerable<ObjectFolderSnapshot> folders)
    {
        IReadOnlyList<ObjectFolderSnapshot> currentFolders = GetFolders(state, layoutId);
        IReadOnlyList<ObjectFolderSnapshot> nextFolders = ObjectFolderUtility.OrderFolderEntries(currentFolders.Concat(folders));

        return WithFolders(state, layoutId, nextFolders);
    }

    public static ObjectFolderSceneState RenameFolder(ObjectFolderSceneState state, Guid? layoutId, string sourceFolderPath, string nextFolderPath)
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
            layoutId,
            path => ObjectFolderUtility.RebaseFolderPath(
                path,
                sanitizedSourceFolderPath,
                sanitizedNextFolderPath));
    }

    public static ObjectFolderSceneState DissolveFolder(
        ObjectFolderSceneState state,
        Guid? layoutId,
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
            layoutId,
            path => ObjectFolderUtility.ApplyFolderPathMap(path, pathMap),
            path => !string.Equals(path, sanitizedFolderPath, StringComparison.OrdinalIgnoreCase));
    }

    public static ObjectFolderSceneState RemoveFolderSubtree(ObjectFolderSceneState state, Guid? layoutId, string folderPath)
    {
        string sanitizedFolderPath = ObjectFolderUtility.SanitizeFolderPath(folderPath);
        return sanitizedFolderPath.Length == 0
            ? state
            : TransformFolderPaths(
                state,
                layoutId,
                path => ObjectFolderUtility.IsSameOrDescendant(path, sanitizedFolderPath) ? null : path);
    }

    public static ObjectFolderSceneState SetFolderColor(ObjectFolderSceneState state, Guid? layoutId, string folderPath, string colorValue)
    {
        var sanitizedFolderPath = ObjectFolderUtility.SanitizeFolderPath(folderPath);
        if (string.IsNullOrEmpty(sanitizedFolderPath))
        {
            return state;
        }

        var sanitizedColorValue = ObjectFolderUtility.SanitizeFolderColorValue(colorValue);
        var nextState = string.IsNullOrEmpty(sanitizedColorValue)
            ? state
            : AddFolder(state, layoutId, sanitizedFolderPath);
        return WithFolders(nextState, layoutId,
            ApplyFolderColor(GetFolders(nextState, layoutId), sanitizedFolderPath, sanitizedColorValue));
    }

    public static bool StatesMatch(ObjectFolderSceneState left, ObjectFolderSceneState right)
        => ObjectFolderUtility.FolderEntriesMatch(left.StandaloneFolders, right.StandaloneFolders)
            && left.LayoutFolders.Count == right.LayoutFolders.Count
            && left.LayoutFolders.All(owner => right.LayoutFolders.TryGetValue(owner.Key, out IReadOnlyList<ObjectFolderSnapshot>? folders)
                && ObjectFolderUtility.FolderEntriesMatch(owner.Value, folders));

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
        Guid? layoutId,
        Func<string, string?> transform,
        Func<string, bool>? includeColor = null)
        => WithFolders(state, layoutId, TransformFolderSet(GetFolders(state, layoutId), transform, includeColor));

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
    public static bool TryMergeChanges(ObjectFolderSceneState current, ObjectFolderSceneState before,
        ObjectFolderSceneState after, out ObjectFolderSceneState merged)
    {
        merged = current;

        if (!ObjectFolderUtility.FolderEntriesMatch(before.StandaloneFolders, after.StandaloneFolders))
        {
            if (!ObjectFolderUtility.FolderEntriesMatch(current.StandaloneFolders, before.StandaloneFolders))
            {
                return false;
            }

            merged = merged with { StandaloneFolders = after.StandaloneFolders };
        }

        Dictionary<Guid, IReadOnlyList<ObjectFolderSnapshot>> folders = new(current.LayoutFolders);
        foreach (Guid id in before.LayoutFolders.Keys.Concat(after.LayoutFolders.Keys).Distinct())
        {
            IReadOnlyList<ObjectFolderSnapshot> expected = before.LayoutFolders.GetValueOrDefault(id, []);
            IReadOnlyList<ObjectFolderSnapshot> next = after.LayoutFolders.GetValueOrDefault(id, []);

            if (ObjectFolderUtility.FolderEntriesMatch(expected, next))
            {
                continue;
            }

            if (!current.LayoutFolders.TryGetValue(id, out IReadOnlyList<ObjectFolderSnapshot>? saved)
                || !ObjectFolderUtility.FolderEntriesMatch(saved, expected))
            {
                return false;
            }

            folders[id] = next;
        }

        merged = merged with { LayoutFolders = folders };
        return true;
    }
}
