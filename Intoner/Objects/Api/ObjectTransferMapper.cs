using Intoner.Objects.Models;
using Intoner.Objects.Utils;
using Intoner.Scene;

namespace Intoner.Objects.Api;

internal enum ObjectPasteDestinationKind
{
    KeepOrganization,
    Ungrouped,
    Folder,
}

internal readonly record struct ObjectPasteDestination(ObjectPasteDestinationKind Kind, string FolderPath = "")
{
    public static ObjectPasteDestination KeepOrganization { get; } = new(ObjectPasteDestinationKind.KeepOrganization);
    public static ObjectPasteDestination Ungrouped { get; } = new(ObjectPasteDestinationKind.Ungrouped);

    public static ObjectPasteDestination Folder(string folderPath)
        => new(ObjectPasteDestinationKind.Folder, folderPath);
}

internal sealed record ObjectTransferImport(
    IReadOnlyList<ObjectSnapshot> Objects,
    IReadOnlyList<ObjectFolderSnapshot> Folders,
    bool IsFolderTransfer)
{
    public IReadOnlyList<SceneGroupSnapshot> Groups { get; init; } = [];
}

/// <summary> maps scene object transfers and persistent snapshots </summary>
internal static class ObjectTransferMapper
{
    public static bool TryCreateDocument(
        IReadOnlyList<SceneItemSnapshot> snapshots,
        IReadOnlyList<ObjectFolderSnapshot> folders,
        out ObjectTransferDocument document)
        => TryCreateDocument(snapshots, folders, folderName: null, out document);

    public static bool TryCreateFolderDocument(
        IReadOnlyList<SceneItemSnapshot> snapshots,
        string folderPath,
        IReadOnlyList<ObjectFolderSnapshot> folders,
        out ObjectTransferDocument document)
    {
        string normalizedFolder = ObjectFolderUtility.SanitizeFolderPath(folderPath);
        if (normalizedFolder.Length == 0
            || snapshots.Any(snapshot => snapshot is not ObjectSceneItemSnapshot item || !ObjectFolderUtility.IsSameOrDescendant(
                item.FolderPath,
                normalizedFolder)))
        {
            document = null!;
            return false;
        }

        return TryCreateDocument(
            snapshots,
            folders.Where(folder => ObjectFolderUtility.IsSameOrDescendant(folder.Path, normalizedFolder))
                .Append(new ObjectFolderSnapshot(normalizedFolder)),
            normalizedFolder,
            out document);
    }

    public static bool TryPrepareImport(
        ObjectTransferDocument document,
        SceneCreationContext createdIn,
        Guid? layoutId,
        IReadOnlyList<string> existingFolders,
        ObjectPasteDestination destination,
        out ObjectTransferImport import)
    {
        import = null!;
        if (document is not
            {
                Kind: ObjectTransferKind.SceneObjects,
                SceneObjects: { } transfer,
                ObjectLibrary: null,
                Transform: null,
            }
            || transfer.Content is not { } source
            || source.Objects is null
            || source.Folders is null
            || source.FolderColors is null
            || source.Groups is null
            || source.Objects.Count + source.Groups.Count > ObjectTransferCodec.MaximumObjectCount
            || (transfer.FolderName is null && source.Objects.Count == 0 && source.Groups.Count == 0)
            || (transfer.FolderName is { } folderName
                && source.Folders.Any(folder => !ObjectFolderUtility.IsSameOrDescendant(folder, folderName))))
        {
            return false;
        }

        DateTime createdAtUtc = DateTime.UtcNow;
        if (!ObjectApiMapper.TryToPersistentSet(source, layoutId, out List<ObjectSnapshot> sourceSnapshots,
                out _, out List<SceneGroupSnapshot> sourceGroups)
            || !ObjectSnapshotUtility.TryCopyWithNewIds(sourceSnapshots, sourceGroups,
                out List<ObjectSnapshot> snapshots, out List<SceneGroupSnapshot> groups))
        {
            return false;
        }

        for (int index = 0; index < snapshots.Count; ++index)
            snapshots[index] = snapshots[index] with { CreatedAtUtc = createdAtUtc, CreatedIn = createdIn };
        for (int index = 0; index < groups.Count; ++index)
            groups[index] = groups[index] with { CreatedAtUtc = createdAtUtc, CreatedIn = createdIn };

        if (!TryApplyDestination(
                transfer,
                ObjectFolderUtility.FromFolderColorMap(
                    source.Folders.Concat(sourceSnapshots.Select(static snapshot => snapshot.FolderPath))
                        .Concat(sourceGroups.Select(static group => group.FolderPath)).Append(transfer.FolderName ?? string.Empty),
                    source.FolderColors),
                snapshots,
                groups,
                existingFolders,
                destination,
                out IReadOnlyList<ObjectFolderSnapshot> folders)
            || !SceneGroupUtility.TryValidateOwner(layoutId, snapshots, groups, out _))
        {
            return false;
        }

        import = new ObjectTransferImport(snapshots, folders, transfer.FolderName is not null) { Groups = groups };
        return true;
    }

    private static bool TryCreateDocument(
        IReadOnlyList<SceneItemSnapshot> snapshots,
        IEnumerable<ObjectFolderSnapshot> folders,
        string? folderName,
        out ObjectTransferDocument document)
    {
        document = null!;
        if (snapshots.Count > ObjectTransferCodec.MaximumObjectCount
            || (folderName is null && snapshots.Count == 0))
        {
            return false;
        }

        List<SceneGroupSnapshot> groups = snapshots.OfType<SceneGroupSnapshot>().ToList();
        HashSet<Guid> groupIds = groups.Select(static group => group.Id).ToHashSet();
        List<ObjectSnapshot> objects = snapshots.OfType<ObjectSnapshot>().Select(snapshot =>
            snapshot.GroupId is Guid id && !groupIds.Contains(id) ? snapshot with { GroupId = null } : snapshot).ToList();
        if (objects.Count + groups.Count != snapshots.Count)
            return false;
        HashSet<Guid> objectIds = new(snapshots.Count);
        foreach (SceneItemSnapshot snapshot in snapshots)
        {
            if (snapshot.Id == Guid.Empty
                || !objectIds.Add(snapshot.Id)
                || !NumericsUtility.IsFinite(snapshot.Transform.Position)
                || !NumericsUtility.IsFinite(snapshot.Transform.RotationDegrees)
                || !NumericsUtility.IsFinite(snapshot.Transform.Scale))
            {
                return false;
            }
        }

        IReadOnlyList<ObjectFolderSnapshot> orderedFolders = ObjectFolderUtility.OrderFolderEntries(
            folders.Concat(snapshots.Select(static snapshot => new ObjectFolderSnapshot(ObjectFolderUtility.GetItemFolderPath(snapshot)))));
        document = new ObjectTransferDocument(
            ObjectTransferKind.SceneObjects,
            SceneObjects: new SceneObjectTransfer(
                ObjectApiMapper.ToPersistentSet(objects, orderedFolders, groups),
                folderName));
        return true;
    }

    private static bool TryApplyDestination(
        SceneObjectTransfer transfer,
        IReadOnlyList<ObjectFolderSnapshot> sourceFolders,
        List<ObjectSnapshot> snapshots,
        List<SceneGroupSnapshot> groups,
        IReadOnlyList<string> existingFolders,
        ObjectPasteDestination destination,
        out IReadOnlyList<ObjectFolderSnapshot> folders)
    {
        folders = [];

        if (transfer.FolderName is null)
        {
            switch (destination.Kind)
            {
                case ObjectPasteDestinationKind.Ungrouped:
                    ReplaceFolders(snapshots, groups, static _ => string.Empty);
                    return true;
                case ObjectPasteDestinationKind.Folder:
                    string destinationFolder = ObjectFolderUtility.SanitizeFolderPath(destination.FolderPath);
                    if (destinationFolder.Length == 0)
                    {
                        return false;
                    }

                    ReplaceFolders(snapshots, groups, _ => destinationFolder);
                    folders = [new ObjectFolderSnapshot(destinationFolder)];
                    return true;
                case ObjectPasteDestinationKind.KeepOrganization:
                    folders = sourceFolders;
                    return true;
                default:
                    return false;
            }
        }

        string sourceRoot = ObjectFolderUtility.SanitizeFolderPath(transfer.FolderName);
        if (sourceRoot.Length == 0
         || snapshots.Any(snapshot => !ObjectFolderUtility.IsSameOrDescendant(snapshot.FolderPath, sourceRoot))
         || groups.Any(group => !ObjectFolderUtility.IsSameOrDescendant(group.FolderPath, sourceRoot)))
        {
            return false;
        }

        string targetFolder = destination.Kind == ObjectPasteDestinationKind.Folder
            ? ObjectFolderUtility.SanitizeFolderPath(destination.FolderPath)
            : string.Empty;
        if (destination.Kind == ObjectPasteDestinationKind.Folder && targetFolder.Length == 0)
        {
            return false;
        }

        string preferredRoot = destination.Kind switch
        {
            ObjectPasteDestinationKind.KeepOrganization => sourceRoot,
            ObjectPasteDestinationKind.Ungrouped => ObjectFolderUtility.GetFolderName(sourceRoot),
            ObjectPasteDestinationKind.Folder => ObjectFolderUtility.CombineFolderPath(
                targetFolder,
                ObjectFolderUtility.GetFolderName(sourceRoot)),
            _ => string.Empty,
        };
        string destinationRoot = ObjectFolderUtility.ResolveAvailableFolderPath(preferredRoot, existingFolders);
        if (destinationRoot.Length == 0)
        {
            return false;
        }

        ReplaceFolders(snapshots, groups, path => ObjectFolderUtility.RebaseFolderPath(path, sourceRoot, destinationRoot));

        folders = ObjectFolderUtility.OrderFolderEntries(
            sourceFolders
                .Select(folder => folder with { Path = ObjectFolderUtility.RebaseFolderPath(folder.Path, sourceRoot, destinationRoot) })
                .Append(new ObjectFolderSnapshot(destinationRoot)));
        return true;
    }

    private static void ReplaceFolders(List<ObjectSnapshot> snapshots, List<SceneGroupSnapshot> groups, Func<string, string> map)
    {
        for (int index = 0; index < snapshots.Count; ++index)
        {
            snapshots[index] = snapshots[index] with { FolderPath = map(snapshots[index].FolderPath) };
        }
        for (int index = 0; index < groups.Count; ++index)
            groups[index] = groups[index] with { FolderPath = map(groups[index].FolderPath) };
    }
}
