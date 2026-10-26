using Intoner.Objects.Models;
using Intoner.Objects.Utils;
using Intoner.Scene;
using Intoner.Services.Serialization;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Intoner.Objects.Api;

internal static class ObjectLayoutJsonSerializer
{
    public static readonly JsonSerializerOptions JsonOptions = JsonSerializerOptionsUtility.CreateStrictIndented();

    internal static readonly JsonSerializerOptions ReleasedJsonOptions = LegacyObjectLocation.CreateJsonOptions(JsonOptions);

    public static string SerializeLayout(ObjectLayoutSnapshot layout)
        => JsonSerializer.Serialize(BuildLayoutDocument(layout), JsonOptions);

    public static string SerializeAutosave(ObjectLayoutAutosaveDocument document)
        => JsonSerializer.Serialize(document, JsonOptions);

    public static bool TryDeserializeLayout(
        string json,
        out ObjectLayoutSnapshot layout,
        out string errorMessage)
    {
        layout = null!;
        if (!TryParseJson(json, "selected layout file", out JsonDocument rootDocument, out errorMessage))
        {
            return false;
        }

        using (rootDocument)
        {
            return TryDeserializeLayout(rootDocument.RootElement, out layout, out errorMessage);
        }
    }

    public static bool TryDeserializeLayout(
        JsonElement root,
        out ObjectLayoutSnapshot layout,
        out string errorMessage)
    {
        try
        {
            layout = null!;

            if (!ObjectLayoutFileUtility.LooksLikeObjectLayout(root))
            {
                errorMessage = "The selected file is not a supported object layout json file.";
                return false;
            }

            if (!TryReadFormatVersion(root, out int formatVersion))
            {
                errorMessage = "The selected layout file is missing a valid format version.";
                return false;
            }

            if (formatVersion is not (1 or 2) && formatVersion != ObjectLayoutFileDocument.CurrentFormatVersion)
            {
                errorMessage = $"Unsupported layout file version: {formatVersion}.";
                return false;
            }

            if ((formatVersion < 3 && !TryUpgradeLayout(root, formatVersion, out root, out errorMessage))
                || !TryDeserialize(root, "selected layout file", out ObjectLayoutFileDocument? document, out errorMessage, formatVersion < 3 ? ReleasedJsonOptions : JsonOptions))
            {
                return false;
            }

            if (document.Id == Guid.Empty || string.IsNullOrWhiteSpace(document.Name))
            {
                errorMessage = "The selected layout file is missing a valid layout id or name.";
                return false;
            }

            if (document.ExportedAtUtc == default || document.CreatedAtUtc == default || document.UpdatedAtUtc == default)
            {
                errorMessage = "The selected layout file is missing valid layout timestamps.";
                return false;
            }

            if (document.Revision <= 0)
            {
                errorMessage = "The selected layout file is missing a valid layout revision.";
                return false;
            }

            if (document.Folders.Any(static folder => folder is null))
            {
                errorMessage = "Folders cannot contain null entries.";
                return false;
            }

            if (!ObjectApiMapper.TryToGroups(document.Groups, document.Id, out List<SceneGroupSnapshot> groups))
            {
                errorMessage = $"Groups[{groups.Count}]: invalid group transform, creation time, or location.";
                return false;
            }

            if (!TryToSnapshots(document.Objects, document.Id, out List<ObjectSnapshot> snapshots, out errorMessage)
                || !SceneGroupUtility.TryValidateOwner(document.Id, snapshots, groups, out errorMessage))
            {
                return false;
            }

            layout = new ObjectLayoutSnapshot
            {
                Id = document.Id,
                Name = document.Name,
                Revision = document.Revision,
                CreatedAtUtc = document.CreatedAtUtc,
                UpdatedAtUtc = document.UpdatedAtUtc,
                Objects = snapshots.OrderBy(static snapshot => snapshot.CreatedAtUtc).ToImmutableArray(),
                Groups = groups.OrderBy(static group => group.CreatedAtUtc).ToImmutableArray(),
                Folders = ObjectFolderUtility.ExpandFolderEntries(document.Folders, snapshots, groups),
            };
            errorMessage = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            layout = null!;
            errorMessage = ex.Message;
            return false;
        }
    }

    public static bool TryDeserializeAutosave(
        string json,
        out ObjectPersistentWorkspaceSnapshot workspace,
        out string errorMessage)
    {
        workspace = null!;
        if (!TryParseJson(json, "autosave file", out JsonDocument rootDocument, out errorMessage))
        {
            return false;
        }

        using (rootDocument)
        {
            return TryDeserializeAutosave(rootDocument.RootElement, out workspace, out errorMessage);
        }
    }

    public static bool TryDeserializeAutosave(
        JsonElement root,
        out ObjectPersistentWorkspaceSnapshot workspace,
        out string errorMessage)
    {
        try
        {
            workspace = null!;

            if (!LooksLikeAutosave(root))
            {
                errorMessage = "The autosave file is not a supported object autosave json file.";
                return false;
            }

            if (!TryReadFormatVersion(root, out int formatVersion))
            {
                errorMessage = "The autosave file is missing a valid format version.";
                return false;
            }

            if (formatVersion == 2)
            {
                if (!TryUpgradeAutosave(root, out root, out errorMessage))
                {
                    return false;
                }
            }
            else if (formatVersion != ObjectLayoutAutosaveDocument.CurrentFormatVersion)
            {
                errorMessage = $"Unsupported autosave file version: {formatVersion}.";
                return false;
            }

            if (!TryDeserialize(root, "autosave file", out ObjectLayoutAutosaveDocument? document, out errorMessage, formatVersion == 2 ? ReleasedJsonOptions : JsonOptions))
            {
                return false;
            }

            if (document.SavedAtUtc == default)
            {
                errorMessage = "The autosave file is missing a valid saved timestamp.";
                return false;
            }

            if (document.StandaloneFolders.Any(static folder => folder is null)
                || document.Layouts.Any(static layout => layout is null || layout.Id == Guid.Empty
                    || string.IsNullOrWhiteSpace(layout.Name) || layout.Folders is null || layout.Folders.Any(static folder => folder is null)))
            {
                errorMessage = "The autosave file contains invalid object data.";
                return false;
            }

            if (!TryToAutosaveSnapshots(document.Objects, out List<ObjectSnapshot> snapshots, out errorMessage))
            {
                return false;
            }

            HashSet<Guid> layoutIds = document.Layouts.Select(static layout => layout.Id).ToHashSet();
            if (layoutIds.Count != document.Layouts.Count
                || document.CreationLayoutId.HasValue && !layoutIds.Contains(document.CreationLayoutId.Value)
                || snapshots.Any(snapshot => snapshot.LayoutId.HasValue && !layoutIds.Contains(snapshot.LayoutId.Value)))
            {
                errorMessage = "The autosave file contains invalid layout ownership.";
                return false;
            }

            ILookup<Guid?, ObjectSnapshot> objectsByOwner = snapshots.ToLookup(static snapshot => snapshot.LayoutId);
            errorMessage = "The autosave contains invalid group metadata.";
            if (!ObjectApiMapper.TryToGroups(document.StandaloneGroups, null, out List<SceneGroupSnapshot> standaloneGroups)
                || !SceneGroupUtility.TryValidateOwner(null, objectsByOwner[null].ToArray(), standaloneGroups, out errorMessage))
                return false;
            Dictionary<Guid, IReadOnlyList<SceneGroupSnapshot>> groupsByOwner = [];
            foreach (ObjectLayoutAutosaveOwner owner in document.Layouts)
            {
                errorMessage = "The autosave contains invalid group metadata.";
                if (!ObjectApiMapper.TryToGroups(owner.Groups, owner.Id, out List<SceneGroupSnapshot> groups)
                    || !SceneGroupUtility.TryValidateOwner(owner.Id, objectsByOwner[owner.Id].ToArray(), groups, out errorMessage))
                    return false;
                groupsByOwner.Add(owner.Id, groups);
            }
            IEnumerable<Guid> groupIds = document.StandaloneGroups.Select(static group => group.Id)
                .Concat(document.Layouts.SelectMany(static owner => owner.Groups).Select(static group => group.Id));
            if (!SceneIdentityValidation.TryValidate([snapshots.Select(static snapshot => snapshot.Id), groupIds], out _))
            {
                errorMessage = "The autosave contains duplicate scene item identities.";
                return false;
            }
            workspace = NormalizeAutosaveFolders(new ObjectPersistentWorkspaceSnapshot
            {
                StandaloneObjects = objectsByOwner[null].OrderBy(static snapshot => snapshot.CreatedAtUtc).ToImmutableArray(),
                StandaloneFolders = document.StandaloneFolders,
                StandaloneGroups = standaloneGroups,
                CreationLayoutId = document.CreationLayoutId,
                Layouts = document.Layouts.Select(layout => new ObjectLayoutSnapshot
                {
                    Id = layout.Id,
                    Name = layout.Name,
                    Folders = layout.Folders,
                    Groups = groupsByOwner[layout.Id],
                    Objects = objectsByOwner[layout.Id].OrderBy(static snapshot => snapshot.CreatedAtUtc).ToImmutableArray(),
                    CreatedAtUtc = document.SavedAtUtc,
                    UpdatedAtUtc = document.SavedAtUtc,
                }).ToImmutableArray(),
                Revision = document.PersistentRevision,
                CapturedAtUtc = document.SavedAtUtc,
            });
            errorMessage = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            workspace = null!;
            errorMessage = ex.Message;
            return false;
        }
    }

    private static bool TryParseJson(string json, string sourceLabel, out JsonDocument document, out string errorMessage)
    {
        try
        {
            document = JsonDocumentUtility.Parse(json);
            errorMessage = string.Empty;
            return true;
        }
        catch (JsonException ex)
        {
            document = null!;
            errorMessage = $"The {sourceLabel} is not valid JSON: {ex.Message}";
            return false;
        }
    }

    public static ObjectLayoutAutosaveDocument BuildAutosaveDocument(ObjectPersistentWorkspaceSnapshot workspace)
    {
        workspace = NormalizeAutosaveFolders(workspace);

        return new ObjectLayoutAutosaveDocument
        {
            DocumentKind = ObjectLayoutAutosaveDocument.DocumentKindValue,
            FormatVersion = ObjectLayoutAutosaveDocument.CurrentFormatVersion,
            SavedAtUtc = workspace.CapturedAtUtc,
            PersistentRevision = workspace.Revision,
            CreationLayoutId = workspace.CreationLayoutId,
            Layouts = workspace.Layouts.Select(static layout => new ObjectLayoutAutosaveOwner
            {
                Id = layout.Id,
                Name = layout.Name,
                Folders = [.. layout.Folders],
                Groups = layout.Groups.Select(ObjectApiMapper.ToPersistentGroup).ToList(),
            }).ToList(),
            Objects = workspace.EnumerateObjects()
                .Select(BuildAutosaveObject).ToList(),
            StandaloneFolders = [.. workspace.StandaloneFolders],
            StandaloneGroups = workspace.StandaloneGroups.Select(ObjectApiMapper.ToPersistentGroup).ToList(),
        };
    }

    private static ObjectPersistentWorkspaceSnapshot NormalizeAutosaveFolders(ObjectPersistentWorkspaceSnapshot workspace)
    {
        return workspace with
        {
            StandaloneFolders = ObjectFolderUtility.ExpandFolderEntries(workspace.StandaloneFolders, workspace.StandaloneObjects, workspace.StandaloneGroups),
            Layouts = workspace.Layouts.Select(static layout => layout with
            {
                Folders = ObjectFolderUtility.ExpandFolderEntries(layout.Folders, layout.Objects, layout.Groups),
            }).ToImmutableArray(),
        };
    }

    // version 2
    private static bool TryUpgradeAutosave(JsonElement root, out JsonElement upgradedRoot, out string errorMessage)
    {
        upgradedRoot = default;
        if (!root.TryGetPropertyIgnoreCase("DefaultLayoutId", out JsonElement defaultId)
            || defaultId.ValueKind != JsonValueKind.Null && (defaultId.ValueKind != JsonValueKind.String
                || !defaultId.TryGetGuid(out Guid layoutId) || layoutId == Guid.Empty))
        {
            errorMessage = "The autosave has no valid default layout id.";
            return false;
        }

        if (!root.TryGetPropertyIgnoreCase("Name", out JsonElement name)
            || name.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(name.GetString()))
        {
            errorMessage = "The autosave has no valid workspace name.";
            return false;
        }

        if (!root.TryGetPropertyIgnoreCase("DefaultLayoutFolders", out JsonElement folders)
            || folders.ValueKind != JsonValueKind.Array)
        {
            errorMessage = "The autosave has no valid layout folders.";
            return false;
        }

        if (!TryDeserialize(root, "autosave file", out JsonObject? upgraded, out errorMessage))
        {
            return false;
        }

        if (upgraded[nameof(ObjectLayoutAutosaveDocument.StandaloneGroups)] is JsonNode standaloneGroups
                && (standaloneGroups is not JsonArray groupArray || groupArray.Count != 0)
            || upgraded[nameof(ObjectLayoutAutosaveDocument.Layouts)] is JsonNode layouts
                && (layouts is not JsonArray layoutArray || layoutArray.Count != 0)
            || upgraded[nameof(ObjectLayoutAutosaveDocument.CreationLayoutId)] is not null)
        {
            errorMessage = "Legacy autosaves cannot contain group or workspace owner metadata.";
            return false;
        }

        JsonArray owners = [];
        JsonNode? id = upgraded["DefaultLayoutId"];
        if (id is null && upgraded["DefaultLayoutFolders"]!.AsArray().Count != 0)
        {
            errorMessage = "The autosave contains layout folders without a layout owner.";
            return false;
        }

        if (id is not null)
        {
            owners.Add(new JsonObject
            {
                ["Id"] = id.DeepClone(),
                ["Name"] = upgraded["Name"]!.DeepClone(),
                ["Folders"] = upgraded["DefaultLayoutFolders"]!.DeepClone(),
                ["Groups"] = new JsonArray(),
            });
        }

        upgraded["FormatVersion"] = ObjectLayoutAutosaveDocument.CurrentFormatVersion;
        upgraded["CreationLayoutId"] = id?.DeepClone();
        upgraded["Layouts"] = owners;
        upgraded["StandaloneGroups"] = new JsonArray();
        upgraded.Remove("DefaultLayoutId");
        upgraded.Remove("DefaultLayoutFolders");
        upgraded.Remove("Name");
        upgradedRoot = JsonSerializer.SerializeToElement(upgraded, JsonOptions);
        return true;
    }

    private static ObjectLayoutFileDocument BuildLayoutDocument(ObjectLayoutSnapshot layout)
    {
        IReadOnlyList<ObjectFolderSnapshot> folders = ObjectFolderUtility.ExpandFolderEntries(layout.Folders, layout.Objects, layout.Groups);

        return new ObjectLayoutFileDocument
        {
            DocumentKind = ObjectLayoutFileDocument.DocumentKindValue,
            FormatVersion = ObjectLayoutFileDocument.CurrentFormatVersion,
            Id = layout.Id,
            Name = layout.Name,
            Revision = layout.Revision,
            ExportedAtUtc = DateTime.UtcNow,
            CreatedAtUtc = layout.CreatedAtUtc,
            UpdatedAtUtc = layout.UpdatedAtUtc,
            Objects = layout.Objects.Select(BuildLayoutFileObject).ToList(),
            Folders = [.. folders],
            Groups = layout.Groups.Select(ObjectApiMapper.ToPersistentGroup).ToList(),
        };
    }

    private static ObjectLayoutFileObject BuildLayoutFileObject(ObjectSnapshot snapshot)
        => new()
        {
            GroupId = snapshot.GroupId,
            FolderPath = ObjectFolderUtility.SanitizeFolderPath(snapshot.FolderPath),
            Locked = snapshot.Locked,
            Object = ObjectApiMapper.ToWorldObject(snapshot),
        };

    private static ObjectLayoutAutosaveObject BuildAutosaveObject(ObjectSnapshot snapshot)
        => new()
        {
            GroupId = snapshot.GroupId,
            LayoutId = snapshot.LayoutId,
            FolderPath = ObjectFolderUtility.SanitizeFolderPath(snapshot.FolderPath),
            Locked = snapshot.Locked,
            Object = ObjectApiMapper.ToWorldObject(snapshot),
        };

    // version 1 layouts use seperate folder metadata so we need to upgrade it to version 2
    internal static bool TryUpgradeLayoutFolders(JsonElement root, JsonObject upgraded, out string errorMessage)
    {
        if (!root.TryGetPropertyIgnoreCase("FolderColors", out JsonElement colors)
            || colors.ValueKind != JsonValueKind.Object)
        {
            errorMessage = "The selected layout file is missing valid folder data.";
            return false;
        }

        if (!TryDeserialize(root.TryGetPropertyIgnoreCase(nameof(ObjectLayoutFileDocument.Folders), out JsonElement folderList) ? folderList : JsonSerializer.SerializeToElement(Array.Empty<string>()), "layout folder list", out List<string>? paths, out errorMessage)
            || !TryDeserialize(colors, "layout folder colors", out Dictionary<string, string>? colorMap, out errorMessage))
        {
            return false;
        }

        if (!root.TryGetPropertyIgnoreCase(nameof(ObjectLayoutFileDocument.Objects), out JsonElement objects)
            || objects.ValueKind != JsonValueKind.Array)
        {
            errorMessage = "The selected layout file is missing a valid object list.";
            return false;
        }

        foreach (JsonElement entry in objects.EnumerateArray())
        {
            if (entry.ValueKind == JsonValueKind.Object
                && entry.TryGetPropertyIgnoreCase(nameof(ObjectLayoutFileObject.FolderPath), out JsonElement path)
                && path.ValueKind == JsonValueKind.String)
            {
                paths.Add(path.GetString()!);
            }
        }

        upgraded[nameof(ObjectLayoutFileDocument.Folders)] = JsonSerializer.SerializeToNode(
            ObjectFolderUtility.FromFolderColorMap(paths, colorMap), JsonOptions);
        upgraded.Remove("FolderColors");
        errorMessage = string.Empty;
        return true;
    }

    internal static bool TryToSnapshot(ObjectLayoutFileObject entry, Guid? layoutId, out ObjectSnapshot snapshot)
    {
        if (!ObjectApiMapper.TryToDetachedSnapshot(entry.Object, out snapshot))
        {
            return false;
        }

        snapshot = snapshot with
        {
            LayoutId = layoutId == Guid.Empty ? null : layoutId,
            GroupId = entry.GroupId,
            FolderPath = ObjectFolderUtility.SanitizeFolderPath(entry.FolderPath),
            Locked = entry.Locked,
        };
        return true;
    }

    private static bool TryToSnapshots(
        IReadOnlyList<ObjectLayoutFileObject>? objects,
        Guid layoutId,
        out List<ObjectSnapshot> snapshots, out string errorMessage)
        => TryMapObjectEntries(objects, _ => layoutId, out snapshots, out errorMessage);

    private static bool TryToAutosaveSnapshots(
        IReadOnlyList<ObjectLayoutAutosaveObject>? objects,
        out List<ObjectSnapshot> snapshots, out string errorMessage)
        => TryMapObjectEntries(objects, static entry => entry.LayoutId, out snapshots, out errorMessage);

    private static bool TryUpgradeLayout(JsonElement root, int formatVersion, out JsonElement upgradedRoot, out string errorMessage)
    {
        upgradedRoot = default;
        if (!TryDeserialize(root, "selected layout file", out JsonObject? upgraded, out errorMessage)
            || formatVersion == 1 && !TryUpgradeLayoutFolders(root, upgraded, out errorMessage))
            return false;
        if (root.TryGetPropertyIgnoreCase(nameof(ObjectLayoutFileDocument.Groups), out JsonElement legacyGroups)
            && (legacyGroups.ValueKind != JsonValueKind.Array || legacyGroups.GetArrayLength() != 0))
        {
            errorMessage = "Legacy layouts cannot contain group metadata.";
            return false;
        }
        upgraded[nameof(ObjectLayoutFileDocument.FormatVersion)] = ObjectLayoutFileDocument.CurrentFormatVersion;
        upgraded[nameof(ObjectLayoutFileDocument.Groups)] = new JsonArray();

        upgradedRoot = JsonSerializer.SerializeToElement(upgraded, JsonOptions);
        return true;
    }

    private static bool TryMapObjectEntries<TEntry>(
        IReadOnlyList<TEntry>? objects,
        Func<TEntry, Guid?> getLayoutId,
        out List<ObjectSnapshot> snapshots, out string errorMessage)
        where TEntry : ObjectLayoutFileObject
    {
        errorMessage = string.Empty;
        if (objects is null)
        {
            errorMessage = "The object list is missing.";
            snapshots = [];
            return false;
        }

        snapshots = new List<ObjectSnapshot>(objects.Count);
        for (int index = 0; index < objects.Count; ++index)
        {
            TEntry? entry = objects[index];
            if (entry is null || !TryToSnapshot(entry, getLayoutId(entry), out ObjectSnapshot snapshot))
            {
                WorldObject? item = entry?.Object;
                errorMessage = $"Objects[{index}] ('{item?.Name}', {item?.Id}): invalid object identity, timestamp, type, or model data.";
                snapshots = [];
                return false;
            }

            snapshots.Add(snapshot);
        }

        return true;
    }

    private static bool TryReadFormatVersion(JsonElement root, out int formatVersion)
    {
        if (root.TryGetPropertyIgnoreCase(nameof(ObjectLayoutFileDocument.FormatVersion), out JsonElement value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out formatVersion))
        {
            return true;
        }

        formatVersion = 0;
        return false;
    }

    private static bool TryDeserialize<TDocument>(
        JsonElement root,
        string sourceLabel,
        [NotNullWhen(true)] out TDocument? document,
        out string errorMessage,
        JsonSerializerOptions? options = null)
        where TDocument : class
    {
        try
        {
            document = root.Deserialize<TDocument>(options ?? JsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            document = default;
            errorMessage = $"The {sourceLabel} contains invalid data: {ex.Message}";
            return false;
        }

        if (document is null)
        {
            errorMessage = $"The {sourceLabel} is empty or invalid.";
            return false;
        }

        errorMessage = string.Empty;
        return true;
    }

    private static bool LooksLikeAutosave(JsonElement root)
        => root.ValueKind == JsonValueKind.Object
           && root.TryGetPropertyIgnoreCase(nameof(ObjectLayoutAutosaveDocument.DocumentKind), out JsonElement documentKind)
           && documentKind.ValueKind == JsonValueKind.String
           && string.Equals(documentKind.GetString(), ObjectLayoutAutosaveDocument.DocumentKindValue, StringComparison.Ordinal)
           && root.TryGetPropertyIgnoreCase(nameof(ObjectLayoutAutosaveDocument.FormatVersion), out _)
           && root.TryGetPropertyIgnoreCase(nameof(ObjectLayoutAutosaveDocument.Objects), out _);
}
