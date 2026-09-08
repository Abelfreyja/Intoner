using Intoner.Objects.Assets;
using Intoner.Objects.Models;
using Intoner.Objects.Utils;
using Intoner.Scene;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;

namespace Intoner.Objects.Library;

internal static class ObjectLibraryRules
{
    public const int MaximumNameLength = 96;

    public static string NormalizeName(string? name)
    {
        string normalized = TextUtility.TrimOrEmpty(name);
        return normalized.Length <= MaximumNameLength
            ? normalized
            : normalized[..MaximumNameLength].TrimEnd();
    }

    public static bool NamesMatch(string left, string right)
        => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    public static string ResolveAvailableGroupName(
        ObjectLibrarySnapshot library,
        string preferredName,
        Guid? parentFolderId)
    {
        ObjectLibraryHierarchy hierarchy = ObjectLibraryHierarchy.Create(library);
        HashSet<string> siblingNames = hierarchy.GetChildren(parentFolderId)
            .Select(static group => group.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return ResolveAvailableGroupName(preferredName, siblingNames);
    }

    public static string ResolveAvailableGroupName(
        string preferredName,
        IReadOnlySet<string> unavailableNames)
    {
        string preferred = NormalizeName(preferredName);
        if (preferred.Length == 0 || !unavailableNames.Contains(preferred))
        {
            return preferred;
        }

        for (int number = 2; number < int.MaxValue; ++number)
        {
            string suffix = $" ({number})";
            int baseLength = Math.Min(preferred.Length, MaximumNameLength - suffix.Length);
            string candidate = preferred[..baseLength].TrimEnd() + suffix;
            if (!unavailableNames.Contains(candidate))
            {
                return candidate;
            }
        }

        return string.Empty;
    }

    public static bool TryNormalizePreset(
        ObjectLibraryPreset? preset,
        [NotNullWhen(true)] out ObjectLibraryPreset? normalizedPreset)
    {
        if (preset is null || !IsFinite(preset.Scale))
        {
            normalizedPreset = null;
            return false;
        }

        ObjectData? model = preset.Model switch
        {
            BgObjectModel bgObject when preset.Kind == ObjectKind.BgObject => bgObject with
            {
                ModelPath = GameAssetPathRules.NormalizeGamePath(bgObject.ModelPath),
            },
            FurnitureModel furniture when preset.Kind == ObjectKind.Furniture => furniture with
            {
                SharedGroupPath = GameAssetPathRules.NormalizeGamePath(furniture.SharedGroupPath),
                AttachmentParentId = null,
            },
            VfxModel vfx when preset.Kind == ObjectKind.Vfx => vfx with
            {
                VfxPath = GameAssetPathRules.NormalizeGamePath(vfx.VfxPath),
            },
            LightModel light when preset.Kind == ObjectKind.Light => light,
            _ => null,
        };
        if (model is null || !HasRequiredAsset(model))
        {
            normalizedPreset = null;
            return false;
        }

        normalizedPreset = preset with { Model = model };
        return true;
    }

    public static bool HasSameSource(ObjectLibraryPreset left, ObjectLibraryPreset right)
        => left.Kind == right.Kind
        && (left.Model, right.Model) switch
        {
            (BgObjectModel leftModel, BgObjectModel rightModel)
                => PathsMatch(leftModel.ModelPath, rightModel.ModelPath),
            (FurnitureModel leftModel, FurnitureModel rightModel)
                => leftModel.HousingRowId == rightModel.HousingRowId
                && leftModel.ItemRowId == rightModel.ItemRowId
                && PathsMatch(leftModel.SharedGroupPath, rightModel.SharedGroupPath),
            (VfxModel leftModel, VfxModel rightModel)
                => PathsMatch(leftModel.VfxPath, rightModel.VfxPath),
            (LightModel leftModel, LightModel rightModel)
                => leftModel.LightType == rightModel.LightType,
            _ => false,
        };

    public static bool CanReceiveEntries(IReadOnlyList<ObjectLibraryFolder> folders, Guid? folderId)
        => !folderId.HasValue || folders.Any(folder => folder.Id == folderId.Value);

    public static ObjectLibraryEntry MoveEntry(ObjectLibraryEntry entry, Guid? folderId)
        => ClearPrefabPlacement(entry with { FolderId = folderId });

    public static bool TryNormalizeWorldTransform(
        SceneTransform? transform,
        Vector3 scale,
        [NotNullWhen(true)] out SceneTransform? normalizedTransform)
    {
        if (transform is null
         || !IsFinite(transform.Position)
         || !IsFinite(transform.RotationDegrees)
         || !IsFinite(scale))
        {
            normalizedTransform = null;
            return false;
        }

        normalizedTransform = transform with { Scale = scale };
        return true;
    }

    public static ObjectLibraryContent Normalize(
        IEnumerable<ObjectLibraryEntry> entries,
        IEnumerable<ObjectLibraryFolder> folders,
        IEnumerable<ObjectLibraryPrefab> prefabs)
    {
        ObjectLibraryFolder[] normalizedFolders = NormalizeFolders(folders);
        ObjectLibraryEntry[] normalizedEntries = NormalizeEntries(entries);
        Dictionary<Guid, int> entryIndices = normalizedEntries
            .Select(static (entry, index) => (entry.Id, Index: index))
            .ToDictionary(static entry => entry.Id, static entry => entry.Index);
        HashSet<Guid> assignedEntryIds = [];
        HashSet<Guid> groupIds = normalizedFolders.Select(static folder => folder.Id).ToHashSet();
        Dictionary<Guid, HashSet<string>> groupNames = CreateSiblingNameSets(normalizedFolders);
        HashSet<Guid> folderIds = normalizedFolders.Select(static folder => folder.Id).ToHashSet();
        List<ObjectLibraryPrefab> normalizedPrefabs = [];

        foreach (ObjectLibraryPrefab prefab in prefabs)
        {
            string name = NormalizeName(prefab.Name);
            Guid? parentFolderId = NormalizeParentFolderId(prefab.ParentFolderId);
            HashSet<string> siblingNames = GetSiblingNameSet(groupNames, parentFolderId);
            Guid[] memberIds = prefab.EntryIds.Distinct().ToArray();
            if (prefab.Id == Guid.Empty
             || name.Length == 0
             || prefab.CreatedAtUtc == default
             || !prefab.CapturedIn.Scope.IsValid
             || memberIds.Length == 0
             || groupIds.Contains(prefab.Id)
             || parentFolderId is { } parentId && !folderIds.Contains(parentId)
             || siblingNames.Contains(name)
             || memberIds.Any(id => !entryIndices.ContainsKey(id) || assignedEntryIds.Contains(id)))
            {
                continue;
            }

            ObjectLibraryEntry[] members = new ObjectLibraryEntry[memberIds.Length];
            bool valid = true;
            for (int index = 0; index < memberIds.Length; ++index)
            {
                ObjectLibraryEntry entry = normalizedEntries[entryIndices[memberIds[index]]];
                if (!TryNormalizeWorldTransform(entry.WorldTransform, entry.Preset.Scale, out SceneTransform? transform))
                {
                    valid = false;
                    break;
                }

                members[index] = entry with
                {
                    FolderId = null,
                    WorldTransform = transform,
                };
            }

            if (!valid)
            {
                continue;
            }

            NormalizePrefabRelationships(members);
            if (!ObjectLibraryPrefabPlacementResolver.CanResolve(members))
            {
                continue;
            }

            for (int index = 0; index < members.Length; ++index)
            {
                normalizedEntries[entryIndices[members[index].Id]] = members[index];
                _ = assignedEntryIds.Add(members[index].Id);
            }

            _ = groupIds.Add(prefab.Id);
            _ = siblingNames.Add(name);
            normalizedPrefabs.Add(prefab with
            {
                Name = name,
                ParentFolderId = parentFolderId,
                Color = ObjectFolderUtility.SanitizeFolderColorValue(prefab.Color),
                EntryIds = memberIds,
            });
        }

        for (int index = 0; index < normalizedEntries.Length; ++index)
        {
            ObjectLibraryEntry entry = normalizedEntries[index];
            if (assignedEntryIds.Contains(entry.Id))
            {
                continue;
            }

            Guid? folderId = entry.FolderId is { } candidate && folderIds.Contains(candidate)
                ? candidate
                : null;
            normalizedEntries[index] = MoveEntry(entry, folderId);
        }

        return new ObjectLibraryContent(normalizedEntries, normalizedFolders, normalizedPrefabs.ToArray());
    }

    public static bool ContentEquals(ObjectLibrarySnapshot snapshot, ObjectLibraryContent content)
    {
        if (!snapshot.Entries.SequenceEqual(content.Entries)
         || !snapshot.Folders.SequenceEqual(content.Folders)
         || snapshot.Prefabs.Count != content.Prefabs.Length)
        {
            return false;
        }

        for (int index = 0; index < snapshot.Prefabs.Count; ++index)
        {
            if (!PrefabEquals(snapshot.Prefabs[index], content.Prefabs[index]))
            {
                return false;
            }
        }

        return true;
    }

    public static bool PrefabEquals(ObjectLibraryPrefab left, ObjectLibraryPrefab right)
        => left.Id == right.Id
        && string.Equals(left.Name, right.Name, StringComparison.Ordinal)
        && string.Equals(left.Color, right.Color, StringComparison.Ordinal)
        && left.ParentFolderId == right.ParentFolderId
        && left.CreatedAtUtc == right.CreatedAtUtc
        && left.CapturedIn == right.CapturedIn
        && left.EntryIds.SequenceEqual(right.EntryIds);

    private static ObjectLibraryFolder[] NormalizeFolders(IEnumerable<ObjectLibraryFolder> folders)
    {
        List<ObjectLibraryFolder> normalized = [];
        HashSet<Guid> ids = [];
        foreach (ObjectLibraryFolder folder in folders)
        {
            string name = NormalizeName(folder.Name);
            if (folder.Id == Guid.Empty
             || name.Length == 0
             || folder.CreatedAtUtc == default
             || !ids.Add(folder.Id))
            {
                continue;
            }

            normalized.Add(folder with
            {
                Name = name,
                ParentFolderId = NormalizeParentFolderId(folder.ParentFolderId),
                Color = ObjectFolderUtility.SanitizeFolderColorValue(folder.Color),
            });
        }

        RemoveInvalidHierarchyNodes(normalized);
        Dictionary<Guid, HashSet<string>> siblingNames = [];
        normalized.RemoveAll(folder => !GetSiblingNameSet(siblingNames, folder.ParentFolderId).Add(folder.Name));
        RemoveInvalidHierarchyNodes(normalized);
        return normalized.ToArray();
    }

    private static void RemoveInvalidHierarchyNodes(List<ObjectLibraryFolder> folders)
    {
        while (!ObjectLibraryHierarchy.TryCreate(folders, [], out _, out IReadOnlySet<Guid> invalidIds))
        {
            if (folders.RemoveAll(folder => invalidIds.Contains(folder.Id)) == 0)
            {
                folders.Clear();
                return;
            }
        }
    }

    public static Dictionary<Guid, HashSet<string>> CreateSiblingNameSets(
        IEnumerable<ObjectLibraryGroup> groups)
    {
        Dictionary<Guid, HashSet<string>> names = [];
        foreach (ObjectLibraryGroup group in groups)
        {
            _ = GetSiblingNameSet(names, group.ParentFolderId).Add(group.Name);
        }

        return names;
    }

    public static HashSet<string> GetSiblingNameSet(
        IDictionary<Guid, HashSet<string>> names,
        Guid? parentFolderId)
    {
        Guid parentId = parentFolderId ?? Guid.Empty;
        if (!names.TryGetValue(parentId, out HashSet<string>? siblings))
        {
            siblings = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            names.Add(parentId, siblings);
        }

        return siblings;
    }

    private static Guid? NormalizeParentFolderId(Guid? folderId)
        => folderId is { } value && value != Guid.Empty ? value : null;

    private static ObjectLibraryEntry[] NormalizeEntries(IEnumerable<ObjectLibraryEntry> entries)
    {
        List<ObjectLibraryEntry> normalized = [];
        HashSet<Guid> ids = [];
        foreach (ObjectLibraryEntry entry in entries)
        {
            string name = NormalizeName(entry.Name);
            if (entry.Id == Guid.Empty
             || name.Length == 0
             || entry.CreatedAtUtc == default
             || !ids.Add(entry.Id)
             || !TryNormalizePreset(entry.Preset, out ObjectLibraryPreset? preset))
            {
                continue;
            }

            normalized.Add(entry with
            {
                Name = name,
                Preset = preset,
            });
        }

        return normalized.ToArray();
    }

    private static void NormalizePrefabRelationships(ObjectLibraryEntry[] entries)
    {
        Dictionary<Guid, ObjectLibraryEntry> byId = entries.ToDictionary(static entry => entry.Id);
        for (int index = 0; index < entries.Length; ++index)
        {
            ObjectLibraryEntry entry = entries[index];
            if (entry.AttachmentParentEntryId is not { } parentId)
            {
                continue;
            }

            if (entry.Preset.Model is not FurnitureModel
             || parentId == entry.Id
             || !byId.TryGetValue(parentId, out ObjectLibraryEntry? parent)
             || parent.Preset.Model is not FurnitureModel)
            {
                entries[index] = entry with { AttachmentParentEntryId = null };
            }
        }
    }

    private static ObjectLibraryEntry ClearPrefabPlacement(ObjectLibraryEntry entry)
        => entry with
        {
            AttachmentParentEntryId = null,
            WorldTransform = null,
        };

    private static bool HasRequiredAsset(ObjectData model)
        => model switch
        {
            BgObjectModel bgObject => bgObject.ModelPath.Length > 0,
            FurnitureModel furniture => furniture.SharedGroupPath.Length > 0,
            VfxModel vfx => vfx.VfxPath.Length > 0,
            LightModel => true,
            _ => false,
        };

    private static bool PathsMatch(string left, string right)
        => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static bool IsFinite(Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
