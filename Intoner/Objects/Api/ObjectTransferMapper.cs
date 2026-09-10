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
    bool IsFolderTransfer);

/// <summary> maps scene object transfers and persistent snapshots </summary>
internal static class ObjectTransferMapper
{
    public static bool TryCreateDocument(
        IReadOnlyList<ObjectSnapshot> snapshots,
        IReadOnlyList<ObjectFolderSnapshot> folders,
        out ObjectTransferDocument document)
        => TryCreateDocument(snapshots, folders, folderName: null, out document);

    public static bool TryCreateFolderDocument(
        IReadOnlyList<ObjectSnapshot> snapshots,
        string folderPath,
        IReadOnlyList<ObjectFolderSnapshot> folders,
        out ObjectTransferDocument document)
    {
        string normalizedFolder = ObjectFolderUtility.SanitizeFolderPath(folderPath);
        if (normalizedFolder.Length == 0
            || snapshots.Any(snapshot => !ObjectFolderUtility.IsSameOrDescendant(
                snapshot.FolderPath,
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
            || source.Objects.Count > ObjectTransferCodec.MaximumObjectCount
            || (transfer.FolderName is null && source.Objects.Count == 0)
            || (transfer.FolderName is { } folderName
                && source.Folders.Any(folder => !ObjectFolderUtility.IsSameOrDescendant(folder, folderName))))
        {
            return false;
        }

        DateTime createdAtUtc = DateTime.UtcNow;
        List<ObjectSnapshot> sourceSnapshots = new(source.Objects.Count);
        foreach (PersistentObject? entry in source.Objects)
        {
            if (entry is null || !ObjectApiMapper.TryToPersistentSnapshot(entry, out ObjectSnapshot snapshot))
            {
                return false;
            }

            sourceSnapshots.Add(snapshot with
            {
                LayoutId = layoutId,
                CreatedAtUtc = createdAtUtc,
                CreatedIn = createdIn,
            });
        }

        if (!ObjectSnapshotUtility.TryCopyWithNewIds(sourceSnapshots, out List<ObjectSnapshot> snapshots))
        {
            return false;
        }

        if (!TryApplyDestination(
                transfer,
                ObjectFolderUtility.FromFolderColorMap(
                    source.Folders.Concat(sourceSnapshots.Select(static snapshot => snapshot.FolderPath)).Append(transfer.FolderName ?? string.Empty),
                    source.FolderColors),
                snapshots,
                existingFolders,
                destination,
                out IReadOnlyList<ObjectFolderSnapshot> folders))
        {
            return false;
        }

        import = new ObjectTransferImport(snapshots, folders, transfer.FolderName is not null);
        return true;
    }

    private static bool TryCreateDocument(
        IReadOnlyList<ObjectSnapshot> snapshots,
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

        HashSet<Guid> objectIds = new(snapshots.Count);
        foreach (ObjectSnapshot snapshot in snapshots)
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
            folders.Concat(snapshots.Select(static snapshot => new ObjectFolderSnapshot(snapshot.FolderPath))));
        document = new ObjectTransferDocument(
            ObjectTransferKind.SceneObjects,
            SceneObjects: new SceneObjectTransfer(
                ObjectApiMapper.ToPersistentSet(snapshots, orderedFolders),
                folderName));
        return true;
    }

    private static bool TryApplyDestination(
        SceneObjectTransfer transfer,
        IReadOnlyList<ObjectFolderSnapshot> sourceFolders,
        List<ObjectSnapshot> snapshots,
        IReadOnlyList<string> existingFolders,
        ObjectPasteDestination destination,
        out IReadOnlyList<ObjectFolderSnapshot> folders)
    {
        folders = [];
        if (transfer.FolderName is not null)
        {
            return TryApplyFolderDestination(
                transfer,
                sourceFolders,
                snapshots,
                existingFolders,
                destination,
                out folders);
        }

        switch (destination.Kind)
        {
            case ObjectPasteDestinationKind.Ungrouped:
                ReplaceFolder(snapshots, string.Empty);
                return true;
            case ObjectPasteDestinationKind.Folder:
                string targetFolder = ObjectFolderUtility.SanitizeFolderPath(destination.FolderPath);
                if (targetFolder.Length == 0)
                {
                    return false;
                }

                ReplaceFolder(snapshots, targetFolder);
                folders = [new ObjectFolderSnapshot(targetFolder)];
                return true;
            case ObjectPasteDestinationKind.KeepOrganization:
                folders = sourceFolders;
                return true;
            default:
                return false;
        }
    }

    private static bool TryApplyFolderDestination(
        SceneObjectTransfer transfer,
        IReadOnlyList<ObjectFolderSnapshot> sourceFolders,
        List<ObjectSnapshot> snapshots,
        IReadOnlyList<string> existingFolders,
        ObjectPasteDestination destination,
        out IReadOnlyList<ObjectFolderSnapshot> folders)
    {
        folders = [];
        string sourceRoot = ObjectFolderUtility.SanitizeFolderPath(transfer.FolderName);
        if (sourceRoot.Length == 0
         || snapshots.Any(snapshot => !ObjectFolderUtility.IsSameOrDescendant(snapshot.FolderPath, sourceRoot)))
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

        for (int index = 0; index < snapshots.Count; ++index)
        {
            snapshots[index] = snapshots[index] with
            {
                FolderPath = ObjectFolderUtility.RebaseFolderPath(
                    snapshots[index].FolderPath,
                    sourceRoot,
                    destinationRoot),
            };
        }

        folders = ObjectFolderUtility.OrderFolderEntries(
            sourceFolders
                .Select(folder => folder with { Path = ObjectFolderUtility.RebaseFolderPath(folder.Path, sourceRoot, destinationRoot) })
                .Append(new ObjectFolderSnapshot(destinationRoot)));
        return true;
    }

    private static void ReplaceFolder(List<ObjectSnapshot> snapshots, string folderPath)
    {
        for (int index = 0; index < snapshots.Count; ++index)
        {
            snapshots[index] = snapshots[index] with { FolderPath = folderPath };
        }
    }
}
