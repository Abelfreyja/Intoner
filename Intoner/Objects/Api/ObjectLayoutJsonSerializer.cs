using Intoner.Objects.Models;
using Intoner.Objects.Utils;
using Intoner.Scene;
using Intoner.Services.Serialization;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Intoner.Objects.Api;

internal static class ObjectLayoutJsonSerializer
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerOptionsUtility.CreateStrictIndented())
    {
        AllowDuplicateProperties = false,
    };

    private enum RequiredJsonValueKind
    {
        String,
        Guid,
        NullableGuid,
        DateTime,
        Int64,
        Array,
    }

    private readonly record struct RequiredJsonProperty(string Name, RequiredJsonValueKind Kind, string ErrorMessage);

    private static readonly RequiredJsonProperty[] LayoutDocumentRootProperties =
    [
        new(nameof(ObjectLayoutFileDocument.DocumentKind), RequiredJsonValueKind.String, "The selected layout file is missing a valid document kind."),
        new(nameof(ObjectLayoutFileDocument.Id), RequiredJsonValueKind.Guid, "The selected layout file is missing a layout id."),
        new(nameof(ObjectLayoutFileDocument.Name), RequiredJsonValueKind.String, "The selected layout file is missing a valid layout name."),
        new(nameof(ObjectLayoutFileDocument.Revision), RequiredJsonValueKind.Int64, "The selected layout file is missing a valid layout revision."),
        new(nameof(ObjectLayoutFileDocument.ExportedAtUtc), RequiredJsonValueKind.DateTime, "The selected layout file is missing valid layout timestamps."),
        new(nameof(ObjectLayoutFileDocument.CreatedAtUtc), RequiredJsonValueKind.DateTime, "The selected layout file is missing valid layout timestamps."),
        new(nameof(ObjectLayoutFileDocument.UpdatedAtUtc), RequiredJsonValueKind.DateTime, "The selected layout file is missing valid layout timestamps."),
        new(nameof(ObjectLayoutFileDocument.Objects), RequiredJsonValueKind.Array, "The selected layout file is missing a valid object list."),
        new(nameof(ObjectLayoutFileDocument.Folders), RequiredJsonValueKind.Array, "The selected layout file is missing a valid folder list."),
    ];

    private static readonly RequiredJsonProperty[] AutosaveDocumentRootProperties =
    [
        new(nameof(ObjectLayoutAutosaveDocument.DocumentKind), RequiredJsonValueKind.String, "The autosave file is missing a valid document kind."),
        new(nameof(ObjectLayoutAutosaveDocument.Name), RequiredJsonValueKind.String, "The autosave file is missing a valid workspace name."),
        new(nameof(ObjectLayoutAutosaveDocument.SavedAtUtc), RequiredJsonValueKind.DateTime, "The autosave file is missing a valid saved timestamp."),
        new(nameof(ObjectLayoutAutosaveDocument.PersistentRevision), RequiredJsonValueKind.Int64, "The autosave file is missing a valid persistent revision."),
        new(nameof(ObjectLayoutAutosaveDocument.DefaultLayoutId), RequiredJsonValueKind.NullableGuid, "The autosave file is missing a valid default layout id."),
        new(nameof(ObjectLayoutAutosaveDocument.Objects), RequiredJsonValueKind.Array, "The autosave file is missing a valid object list."),
        new(nameof(ObjectLayoutAutosaveDocument.StandaloneFolders), RequiredJsonValueKind.Array, "The autosave file is missing a valid standalone folder list."),
        new(nameof(ObjectLayoutAutosaveDocument.DefaultLayoutFolders), RequiredJsonValueKind.Array, "The autosave file is missing a valid default layout folder list."),
    ];

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

    private static bool TryParseJson(string json, string sourceLabel, out JsonDocument document, out string errorMessage)
    {
        try
        {
            document = JsonDocument.Parse(json);
            errorMessage = string.Empty;
            return true;
        }
        catch (JsonException)
        {
            document = null!;
            errorMessage = $"The {sourceLabel} is not valid json.";
            return false;
        }
    }

    public static bool TryDeserializeLayout(
        JsonElement root,
        out ObjectLayoutSnapshot layout,
        out string errorMessage)
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

        if (formatVersion != 1 && formatVersion != ObjectLayoutFileDocument.CurrentFormatVersion)
        {
            errorMessage = $"Unsupported layout file version: {formatVersion}.";
            return false;
        }

        if (!TryValidateRequiredProperties(root, LayoutDocumentRootProperties, out errorMessage)
            || (formatVersion == 1 && !TryUpgradeLayoutFolders(root, out root, out errorMessage))
            || !TryDeserialize(root, "selected layout file", out ObjectLayoutFileDocument? document, out errorMessage))
        {
            return false;
        }

        if (document.Revision <= 0)
        {
            errorMessage = "The selected layout file is missing a valid layout revision.";
            return false;
        }

        if (document.Folders.Any(static folder => folder is null)
            || !TryToSnapshots(document.Objects, document.Id, out List<ObjectSnapshot> snapshots))
        {
            errorMessage = "The selected layout file contains invalid object data.";
            return false;
        }

        layout = new ObjectLayoutSnapshot
        {
            Id = document.Id,
            Name = document.Name,
            Revision = document.Revision,
            CreatedAtUtc = document.CreatedAtUtc,
            UpdatedAtUtc = document.UpdatedAtUtc,
            Objects = snapshots.OrderBy(static snapshot => snapshot.CreatedAtUtc).ToList(),
            Folders = ObjectFolderUtility.OrderFolderEntries(
                document.Folders.Concat(snapshots.Select(static snapshot => new ObjectFolderSnapshot(snapshot.FolderPath)))),
        };
        errorMessage = string.Empty;
        return true;
    }

    public static bool TryDeserializeAutosave(
        JsonElement root,
        out ObjectPersistentWorkspaceSnapshot workspace,
        out string errorMessage)
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

        if (formatVersion != ObjectLayoutAutosaveDocument.CurrentFormatVersion)
        {
            errorMessage = $"Unsupported autosave file version: {formatVersion}.";
            return false;
        }

        if (!TryValidateRequiredProperties(root, AutosaveDocumentRootProperties, out errorMessage)
            || !TryDeserialize(root, "autosave file", out ObjectLayoutAutosaveDocument? document, out errorMessage))
        {
            return false;
        }

        if (document.StandaloneFolders.Any(static folder => folder is null)
            || document.DefaultLayoutFolders.Any(static folder => folder is null)
            || !TryToAutosaveSnapshots(document.Objects, out List<ObjectSnapshot> snapshots)
            || !SceneIdentityValidation.TryValidate([snapshots.Select(static snapshot => snapshot.Id)], out _))
        {
            errorMessage = "The autosave file contains invalid object data.";
            return false;
        }

        workspace = NormalizeAutosaveFolders(new ObjectPersistentWorkspaceSnapshot
        {
            Objects = snapshots.OrderBy(static snapshot => snapshot.CreatedAtUtc).ToList(),
            StandaloneFolders = document.StandaloneFolders,
            DefaultLayoutFolders = document.DefaultLayoutFolders,
            DefaultLayoutId = document.DefaultLayoutId,
            Name = TextUtility.TrimOrFallback(document.Name, "Recovered object workspace"),
            Revision = document.PersistentRevision,
            CapturedAtUtc = document.SavedAtUtc,
        });
        errorMessage = string.Empty;
        return true;
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
            DefaultLayoutId = workspace.DefaultLayoutId,
            Name = TextUtility.TrimOrFallback(workspace.Name, "Current object workspace"),
            Objects = workspace.Objects.Select(BuildAutosaveObject).ToList(),
            StandaloneFolders = [.. workspace.StandaloneFolders],
            DefaultLayoutFolders = [.. workspace.DefaultLayoutFolders],
        };
    }

    private static ObjectPersistentWorkspaceSnapshot NormalizeAutosaveFolders(ObjectPersistentWorkspaceSnapshot workspace)
    {
        List<ObjectFolderSnapshot> standaloneFolders = [.. workspace.StandaloneFolders];
        List<ObjectFolderSnapshot> defaultLayoutFolders = [.. workspace.DefaultLayoutFolders];
        foreach (ObjectSnapshot snapshot in workspace.Objects)
        {
            List<ObjectFolderSnapshot> folders = workspace.DefaultLayoutId.HasValue && snapshot.LayoutId == workspace.DefaultLayoutId
                ? defaultLayoutFolders
                : standaloneFolders;
            folders.Add(new ObjectFolderSnapshot(snapshot.FolderPath));
        }

        return workspace with
        {
            StandaloneFolders = ObjectFolderUtility.ExpandFolderEntries(standaloneFolders),
            DefaultLayoutFolders = ObjectFolderUtility.ExpandFolderEntries(defaultLayoutFolders),
        };
    }

    private static ObjectLayoutFileDocument BuildLayoutDocument(ObjectLayoutSnapshot layout)
    {
        IReadOnlyList<ObjectFolderSnapshot> folders = ObjectFolderUtility.OrderFolderEntries(
            layout.Folders.Concat(layout.Objects.Select(static snapshot => new ObjectFolderSnapshot(snapshot.FolderPath))));

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
        };
    }

    private static ObjectLayoutFileObject BuildLayoutFileObject(ObjectSnapshot snapshot)
        => new()
        {
            FolderPath = ObjectFolderUtility.SanitizeFolderPath(snapshot.FolderPath),
            Locked = snapshot.Locked,
            Object = ObjectApiMapper.ToWorldObject(snapshot),
        };

    private static ObjectLayoutAutosaveObject BuildAutosaveObject(ObjectSnapshot snapshot)
        => new()
        {
            LayoutId = snapshot.LayoutId,
            FolderPath = ObjectFolderUtility.SanitizeFolderPath(snapshot.FolderPath),
            Locked = snapshot.Locked,
            Object = ObjectApiMapper.ToWorldObject(snapshot),
        };

    // version 1 layouts use seperate folder metadata so we need to upgrade it to version 2
    private static bool TryUpgradeLayoutFolders(JsonElement root, out JsonElement upgradedRoot, out string errorMessage)
    {
        upgradedRoot = default;
        if (!root.TryGetProperty("FolderColors", out JsonElement colors)
            || colors.ValueKind != JsonValueKind.Object)
        {
            errorMessage = "The selected layout file is missing valid folder data.";
            return false;
        }

        if (!TryDeserialize(root.GetProperty(nameof(ObjectLayoutFileDocument.Folders)), "layout folder list", out List<string>? paths, out errorMessage)
            || !TryDeserialize(colors, "layout folder colors", out Dictionary<string, string>? colorMap, out errorMessage)
            || !TryDeserialize(root, "selected layout file", out JsonObject? upgraded, out errorMessage))
        {
            return false;
        }

        JsonElement objects = root.GetProperty(nameof(ObjectLayoutFileDocument.Objects));
        foreach (JsonElement entry in objects.EnumerateArray())
        {
            if (entry.ValueKind == JsonValueKind.Object
                && entry.TryGetProperty(nameof(ObjectLayoutFileObject.FolderPath), out JsonElement path)
                && path.ValueKind == JsonValueKind.String)
            {
                paths.Add(path.GetString()!);
            }
        }

        upgraded[nameof(ObjectLayoutFileDocument.FormatVersion)] = ObjectLayoutFileDocument.CurrentFormatVersion;
        upgraded[nameof(ObjectLayoutFileDocument.Folders)] = JsonSerializer.SerializeToNode(
            ObjectFolderUtility.FromFolderColorMap(paths, colorMap), JsonOptions);
        upgraded.Remove("FolderColors");
        upgradedRoot = JsonSerializer.SerializeToElement(upgraded, JsonOptions);
        errorMessage = string.Empty;
        return true;
    }

    private static bool TryToSnapshots(
        IReadOnlyList<ObjectLayoutFileObject>? objects,
        Guid layoutId,
        out List<ObjectSnapshot> snapshots)
        => TryToSnapshots(
            objects,
            static entry => entry.Object,
            (entry, snapshot) => snapshot with
            {
                LayoutId = layoutId,
                FolderPath = ObjectFolderUtility.SanitizeFolderPath(entry.FolderPath),
                Locked = entry.Locked,
            },
            out snapshots);

    private static bool TryToAutosaveSnapshots(
        IReadOnlyList<ObjectLayoutAutosaveObject>? objects,
        out List<ObjectSnapshot> snapshots)
        => TryToSnapshots(
            objects,
            static entry => entry.Object,
            static (entry, snapshot) => snapshot with
            {
                LayoutId = entry.LayoutId == Guid.Empty ? null : entry.LayoutId,
                FolderPath = ObjectFolderUtility.SanitizeFolderPath(entry.FolderPath),
                Locked = entry.Locked,
            },
            out snapshots);

    private static bool TryToSnapshots<TEntry>(
        IReadOnlyList<TEntry>? objects,
        Func<TEntry, WorldObject?> getObject,
        Func<TEntry, ObjectSnapshot, ObjectSnapshot> buildSnapshot,
        out List<ObjectSnapshot> snapshots)
        where TEntry : class
    {
        if (objects is null)
        {
            snapshots = [];
            return false;
        }

        snapshots = new List<ObjectSnapshot>(objects.Count);
        foreach (TEntry? entry in objects)
        {
            if (entry is null || !TryToDetachedSnapshot(getObject(entry), out ObjectSnapshot snapshot))
            {
                snapshots = [];
                return false;
            }

            snapshots.Add(buildSnapshot(entry, snapshot));
        }

        return true;
    }

    private static bool TryToDetachedSnapshot(WorldObject? dto, out ObjectSnapshot snapshot)
    {
        if (dto is not null
            && IsPersistedLayoutObject(dto)
            && ObjectApiMapper.TryToDetachedSnapshot(dto, out snapshot))
        {
            return true;
        }

        snapshot = default!;
        return false;
    }

    private static bool IsPersistedLayoutObject(WorldObject dto)
        => dto.Id != Guid.Empty && dto.CreatedAtUtc != default;

    private static bool TryReadFormatVersion(JsonElement root, out int formatVersion)
    {
        if (root.TryGetProperty(nameof(ObjectLayoutFileDocument.FormatVersion), out JsonElement value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out formatVersion))
        {
            return true;
        }

        formatVersion = 0;
        return false;
    }

    private static bool TryValidateRequiredProperties(
        JsonElement root,
        ReadOnlySpan<RequiredJsonProperty> properties,
        out string errorMessage)
    {
        foreach (RequiredJsonProperty property in properties)
        {
            if (TryReadRequiredProperty(root, property))
            {
                continue;
            }

            errorMessage = property.ErrorMessage;
            return false;
        }

        errorMessage = string.Empty;
        return true;
    }

    private static bool TryReadRequiredProperty(JsonElement root, RequiredJsonProperty property)
    {
        if (!root.TryGetProperty(property.Name, out JsonElement element))
        {
            return false;
        }

        return property.Kind switch
        {
            RequiredJsonValueKind.String => element.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(element.GetString()),
            RequiredJsonValueKind.Guid => element.ValueKind == JsonValueKind.String && element.TryGetGuid(out Guid guid) && guid != Guid.Empty,
            RequiredJsonValueKind.NullableGuid => element.ValueKind == JsonValueKind.Null
                || (element.ValueKind == JsonValueKind.String && element.TryGetGuid(out Guid guid) && guid != Guid.Empty),
            RequiredJsonValueKind.DateTime => element.ValueKind == JsonValueKind.String && element.TryGetDateTime(out DateTime dateTime) && dateTime != default,
            RequiredJsonValueKind.Int64 => element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out _),
            RequiredJsonValueKind.Array => element.ValueKind == JsonValueKind.Array,
            _ => false,
        };
    }

    private static bool TryDeserialize<TDocument>(
        JsonElement root,
        string sourceLabel,
        [NotNullWhen(true)] out TDocument? document,
        out string errorMessage)
        where TDocument : class
    {
        try
        {
            document = root.Deserialize<TDocument>(JsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            // json object decoding reports duplicate properties as argument exceptions
            document = default;
            errorMessage = $"The {sourceLabel} contains invalid object data.";
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
           && root.TryGetProperty(nameof(ObjectLayoutAutosaveDocument.DocumentKind), out JsonElement documentKind)
           && documentKind.ValueKind == JsonValueKind.String
           && string.Equals(documentKind.GetString(), ObjectLayoutAutosaveDocument.DocumentKindValue, StringComparison.Ordinal)
           && root.TryGetProperty(nameof(ObjectLayoutAutosaveDocument.FormatVersion), out _)
           && root.TryGetProperty(nameof(ObjectLayoutAutosaveDocument.Objects), out _);
}

