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
    IReadOnlyList<string> Folders,
    IReadOnlyDictionary<string, string> FolderColors,
    bool IsFolderTransfer);

/// <summary> maps scene object transfers and persistent snapshots </summary>
internal static class ObjectTransferMapper
{
    public static bool TryCreateDocument(
        IReadOnlyList<ObjectSnapshot> snapshots,
        IReadOnlyList<string> folders,
        IReadOnlyDictionary<string, string> folderColors,
        out ObjectTransferDocument document)
        => TryCreateDocument(snapshots, folders, folderColors, folderName: null, out document);

    public static bool TryCreateFolderDocument(
        IReadOnlyList<ObjectSnapshot> snapshots,
        string folderPath,
        IReadOnlyList<string> folders,
        IReadOnlyDictionary<string, string> folderColors,
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

        IReadOnlyList<string> subtreeFolders = ObjectFolderUtility.OrderFolders(
            folders
                .Where(folder => ObjectFolderUtility.IsSameOrDescendant(folder, normalizedFolder))
                .Concat(snapshots.Select(static snapshot => snapshot.FolderPath))
                .Append(normalizedFolder));
        IReadOnlyDictionary<string, string> subtreeColors = ObjectFolderUtility.OrderFolderColorMap(
            folderColors,
            subtreeFolders);
        return TryCreateDocument(snapshots, subtreeFolders, subtreeColors, normalizedFolder, out document);
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
            || (transfer.FolderName is null && source.Objects.Count == 0))
        {
            return false;
        }

        Dictionary<Guid, Guid> importedIds = new(source.Objects.Count);
        foreach (PersistentObject? entry in source.Objects)
        {
            if (entry?.Object is null
                || entry.Object.Id == Guid.Empty
                || !importedIds.TryAdd(entry.Object.Id, Guid.NewGuid()))
            {
                return false;
            }
        }

        DateTime createdAtUtc = DateTime.UtcNow;
        List<ObjectSnapshot> snapshots = new(source.Objects.Count);
        foreach (PersistentObject? entry in source.Objects)
        {
            if (entry is null || !ObjectApiMapper.TryToPersistentSnapshot(entry, out ObjectSnapshot snapshot))
            {
                return false;
            }

            ObjectData model = snapshot.Model;
            if (model is FurnitureModel furniture)
            {
                model = furniture with
                {
                    AttachmentParentId = furniture.AttachmentParentId is { } parentId
                        && importedIds.TryGetValue(parentId, out Guid importedParentId)
                            ? importedParentId
                            : null,
                };
            }

            snapshots.Add(snapshot with
            {
                Id = importedIds[snapshot.Id],
                LayoutId = layoutId,
                CreatedAtUtc = createdAtUtc,
                CreatedIn = createdIn,
                Model = model,
            });
        }

        if (!TryApplyDestination(
                transfer,
                source,
                snapshots,
                existingFolders,
                destination,
                out IReadOnlyList<string> folders,
                out IReadOnlyDictionary<string, string> colors))
        {
            return false;
        }

        import = new ObjectTransferImport(snapshots, folders, colors, transfer.FolderName is not null);
        return true;
    }

    private static bool TryCreateDocument(
        IReadOnlyList<ObjectSnapshot> snapshots,
        IReadOnlyList<string> folders,
        IReadOnlyDictionary<string, string> folderColors,
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

        IReadOnlyList<string> orderedFolders = ObjectFolderUtility.OrderFolders(
            folders.Concat(snapshots.Select(static snapshot => snapshot.FolderPath)));
        IReadOnlyDictionary<string, string> orderedColors = ObjectFolderUtility.OrderFolderColorMap(
            folderColors,
            orderedFolders);
        document = new ObjectTransferDocument(
            ObjectTransferKind.SceneObjects,
            SceneObjects: new SceneObjectTransfer(
                ObjectApiMapper.ToPersistentSet(snapshots, orderedFolders, orderedColors),
                folderName));
        return true;
    }

    private static bool TryApplyDestination(
        SceneObjectTransfer transfer,
        PersistentObjectSet source,
        List<ObjectSnapshot> snapshots,
        IReadOnlyList<string> existingFolders,
        ObjectPasteDestination destination,
        out IReadOnlyList<string> folders,
        out IReadOnlyDictionary<string, string> colors)
    {
        folders = [];
        colors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (transfer.FolderName is not null)
        {
            return TryApplyFolderDestination(
                transfer,
                source,
                snapshots,
                existingFolders,
                destination,
                out folders,
                out colors);
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
                folders = [targetFolder];
                return true;
            case ObjectPasteDestinationKind.KeepOrganization:
                folders = ObjectFolderUtility.OrderFolders(
                    source.Folders.Concat(snapshots.Select(static snapshot => snapshot.FolderPath)));
                colors = ObjectFolderUtility.OrderFolderColorMap(source.FolderColors, folders);
                return true;
            default:
                return false;
        }
    }

    private static bool TryApplyFolderDestination(
        SceneObjectTransfer transfer,
        PersistentObjectSet source,
        List<ObjectSnapshot> snapshots,
        IReadOnlyList<string> existingFolders,
        ObjectPasteDestination destination,
        out IReadOnlyList<string> folders,
        out IReadOnlyDictionary<string, string> colors)
    {
        folders = [];
        colors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string sourceRoot = ObjectFolderUtility.SanitizeFolderPath(transfer.FolderName);
        if (sourceRoot.Length == 0
         || source.Folders.Any(folder => !ObjectFolderUtility.IsSameOrDescendant(folder, sourceRoot))
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

        folders = ObjectFolderUtility.OrderFolders(
            source.Folders
                .Select(folder => ObjectFolderUtility.RebaseFolderPath(folder, sourceRoot, destinationRoot))
                .Concat(snapshots.Select(static snapshot => snapshot.FolderPath))
                .Append(destinationRoot));
        Dictionary<string, string> rebasedColors = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string path, string color) in source.FolderColors)
        {
            string rebasedPath = ObjectFolderUtility.RebaseFolderPath(path, sourceRoot, destinationRoot);
            if (rebasedPath.Length > 0)
            {
                rebasedColors[rebasedPath] = color;
            }
        }

        colors = ObjectFolderUtility.OrderFolderColorMap(rebasedColors, folders);
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
