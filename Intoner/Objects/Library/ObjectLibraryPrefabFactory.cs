using Intoner.Objects.Models;
using Intoner.Objects.Utils;
using Intoner.Scene;

namespace Intoner.Objects.Library;

/// <summary> creates reusable prefab aggregates from placed scene objects </summary>
internal static class ObjectLibraryPrefabFactory
{
    public static bool TryCreate(
        string name,
        string color,
        SceneCreationContext capturedIn,
        IReadOnlyList<ObjectSnapshot> snapshots,
        out ObjectLibraryPrefab prefab,
        out ObjectLibraryEntry[] entries)
    {
        prefab = default!;
        entries = [];
        string normalizedName = ObjectLibraryRules.NormalizeName(name);
        if (normalizedName.Length == 0 || !capturedIn.Scope.IsValid || snapshots.Count == 0)
        {
            return false;
        }

        Dictionary<Guid, Guid> entryIds = new(snapshots.Count);
        for (int index = 0; index < snapshots.Count; ++index)
        {
            ObjectSnapshot snapshot = snapshots[index];
            if (snapshot.Id == Guid.Empty || !entryIds.TryAdd(snapshot.Id, Guid.NewGuid()))
            {
                return false;
            }
        }

        DateTimeOffset createdAt = DateTimeOffset.UtcNow;
        entries = new ObjectLibraryEntry[snapshots.Count];
        for (int index = 0; index < snapshots.Count; ++index)
        {
            ObjectSnapshot snapshot = snapshots[index];
            if (!TryCreateEntry(snapshot, entryIds, createdAt, out entries[index]))
            {
                entries = [];
                return false;
            }
        }

        if (!ObjectLibraryPrefabPlacementResolver.CanResolve(entries))
        {
            entries = [];
            return false;
        }

        prefab = new ObjectLibraryPrefab
        {
            Id = Guid.NewGuid(),
            Name = normalizedName,
            Color = ObjectFolderUtility.SanitizeFolderColorValue(color),
            CreatedAtUtc = createdAt,
            CapturedIn = capturedIn,
            EntryIds = entries.Select(static entry => entry.Id).ToArray(),
        };
        return true;
    }

    private static bool TryCreateEntry(
        ObjectSnapshot snapshot,
        IReadOnlyDictionary<Guid, Guid> entryIds,
        DateTimeOffset createdAt,
        out ObjectLibraryEntry entry)
    {
        entry = default!;
        string name = ObjectLibraryRules.NormalizeName(snapshot.Name);
        ObjectLibraryPreset preset = new()
        {
            Kind = snapshot.Kind,
            Visible = snapshot.Visible,
            Scale = snapshot.Transform.Scale,
            Model = snapshot.Model,
        };
        if (name.Length == 0
         || !ObjectLibraryRules.TryNormalizePreset(preset, out ObjectLibraryPreset? normalizedPreset)
         || !ObjectLibraryRules.TryNormalizeWorldTransform(
             snapshot.Transform,
             normalizedPreset.Scale,
             out SceneTransform? worldTransform))
        {
            return false;
        }

        Guid? parentEntryId = snapshot.Model is FurnitureModel { AttachmentParentId: { } parentId }
            && entryIds.TryGetValue(parentId, out Guid resolvedParentId)
                ? resolvedParentId
                : null;
        entry = new ObjectLibraryEntry
        {
            Id = entryIds[snapshot.Id],
            Name = name,
            FolderId = null,
            AttachmentParentEntryId = parentEntryId,
            CreatedAtUtc = createdAt,
            Preset = normalizedPreset,
            WorldTransform = worldTransform,
        };
        return true;
    }
}
