using Intoner.Objects.Models;
using Intoner.Objects.Utils;
using System.Text.Json;

namespace Intoner.Objects.Api;

internal static class ObjectLayoutJsonSerializer
{
    public static readonly JsonSerializerOptions JsonOptions = ObjectJsonSerializerOptionsUtility.CreateStrictIndented();

    private enum RequiredJsonValueKind
    {
        String,
        Guid,
        NullableGuid,
        DateTime,
        Int64,
        Array,
        Object,
    }

    private readonly record struct RequiredJsonProperty(string Name, RequiredJsonValueKind Kind, string ErrorMessage);

    private static readonly RequiredJsonProperty[] LayoutDocumentRootProperties =
    [
        new(nameof(ObjectLayoutFileDocument.DocumentKind), RequiredJsonValueKind.String, "The selected layout file is missing a valid document kind."),
        new(nameof(ObjectLayoutFileDocument.Id), RequiredJsonValueKind.Guid, "The selected layout file is missing a layout id."),
        new(nameof(ObjectLayoutFileDocument.Name), RequiredJsonValueKind.String, "The selected layout file is missing a valid layout name."),
        new(nameof(ObjectLayoutFileDocument.ExportedAtUtc), RequiredJsonValueKind.DateTime, "The selected layout file is missing valid layout timestamps."),
        new(nameof(ObjectLayoutFileDocument.CreatedAtUtc), RequiredJsonValueKind.DateTime, "The selected layout file is missing valid layout timestamps."),
        new(nameof(ObjectLayoutFileDocument.UpdatedAtUtc), RequiredJsonValueKind.DateTime, "The selected layout file is missing valid layout timestamps."),
        new(nameof(ObjectLayoutFileDocument.Objects), RequiredJsonValueKind.Array, "The selected layout file is missing a valid object list."),
        new(nameof(ObjectLayoutFileDocument.Folders), RequiredJsonValueKind.Array, "The selected layout file is missing a valid folder list."),
        new(nameof(ObjectLayoutFileDocument.FolderColors), RequiredJsonValueKind.Object, "The selected layout file is missing a valid folder color map."),
    ];

    private static readonly RequiredJsonProperty[] AutosaveDocumentRootProperties =
    [
        new(nameof(ObjectLayoutAutosaveDocument.DocumentKind), RequiredJsonValueKind.String, "The autosave file is missing a valid document kind."),
        new(nameof(ObjectLayoutAutosaveDocument.Name), RequiredJsonValueKind.String, "The autosave file is missing a valid workspace name."),
        new(nameof(ObjectLayoutAutosaveDocument.SavedAtUtc), RequiredJsonValueKind.DateTime, "The autosave file is missing a valid saved timestamp."),
        new(nameof(ObjectLayoutAutosaveDocument.PersistentRevision), RequiredJsonValueKind.Int64, "The autosave file is missing a valid persistent revision."),
        new(nameof(ObjectLayoutAutosaveDocument.DefaultLayoutId), RequiredJsonValueKind.NullableGuid, "The autosave file is missing a valid default layout id."),
        new(nameof(ObjectLayoutAutosaveDocument.Objects), RequiredJsonValueKind.Array, "The autosave file is missing a valid object list."),
        new(nameof(ObjectLayoutAutosaveDocument.Folders), RequiredJsonValueKind.Array, "The autosave file is missing a valid folder list."),
        new(nameof(ObjectLayoutAutosaveDocument.FolderColors), RequiredJsonValueKind.Object, "The autosave file is missing a valid folder color map."),
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

        if (formatVersion != ObjectLayoutFileDocument.CurrentFormatVersion)
        {
            return FailUnsupportedVersion(formatVersion, out layout, out errorMessage);
        }

        if (!TryValidateLayoutDocumentRoot(root, out errorMessage))
        {
            return false;
        }

        if (!TryDeserialize(root, "selected layout file", out ObjectLayoutFileDocument? deserializedDocument, out errorMessage))
        {
            return false;
        }

        ObjectLayoutFileDocument document = deserializedDocument!;
        if (!string.Equals(document.DocumentKind, ObjectLayoutFileDocument.DocumentKindValue, StringComparison.Ordinal))
        {
            errorMessage = "The selected json file is not an object layout document.";
            return false;
        }

        if (document.FormatVersion != ObjectLayoutFileDocument.CurrentFormatVersion)
        {
            return FailUnsupportedVersion(document.FormatVersion, out layout, out errorMessage);
        }

        if (document.Id == Guid.Empty)
        {
            errorMessage = "The selected layout file is missing a layout id.";
            return false;
        }

        if (!TryToSnapshots(document.Objects, document.Id, out List<ObjectSnapshot> snapshots))
        {
            errorMessage = "The selected layout file contains invalid object data.";
            return false;
        }

        layout = BuildLayoutSnapshot(
            document.Id,
            document.Name,
            document.CreatedAtUtc,
            document.UpdatedAtUtc,
            snapshots,
            document.Folders,
            document.FolderColors);
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
            return FailUnsupportedAutosaveVersion(formatVersion, out workspace, out errorMessage);
        }

        if (!TryValidateAutosaveDocumentRoot(root, out errorMessage))
        {
            return false;
        }

        if (!TryDeserialize(root, "autosave file", out ObjectLayoutAutosaveDocument? deserializedDocument, out errorMessage))
        {
            return false;
        }

        ObjectLayoutAutosaveDocument document = deserializedDocument!;
        if (!string.Equals(document.DocumentKind, ObjectLayoutAutosaveDocument.DocumentKindValue, StringComparison.Ordinal))
        {
            errorMessage = "The autosave file is not an object autosave document.";
            return false;
        }

        if (document.FormatVersion != ObjectLayoutAutosaveDocument.CurrentFormatVersion)
        {
            return FailUnsupportedAutosaveVersion(document.FormatVersion, out workspace, out errorMessage);
        }

        if (!TryToAutosaveSnapshots(document.Objects, out List<ObjectSnapshot> snapshots))
        {
            errorMessage = "The autosave file contains invalid object data.";
            return false;
        }

        IReadOnlyList<string> orderedFolders = ObjectFolderUtility.OrderFolders(
            document.Folders.Concat(snapshots.Select(static snapshot => snapshot.FolderPath)));
        workspace = new ObjectPersistentWorkspaceSnapshot
        {
            Objects = snapshots.OrderBy(static snapshot => snapshot.CreatedAtUtc).ToList(),
            Folders = orderedFolders,
            FolderColors = new Dictionary<string, string>(
                ObjectFolderUtility.OrderFolderColorMap(document.FolderColors, orderedFolders),
                StringComparer.OrdinalIgnoreCase),
            DefaultLayoutId = document.DefaultLayoutId,
            Name = ObjectStringUtility.TrimOrFallback(document.Name, "Recovered object workspace"),
            Revision = document.PersistentRevision,
            CapturedAtUtc = document.SavedAtUtc,
        };
        errorMessage = string.Empty;
        return true;
    }

    public static ObjectLayoutAutosaveDocument BuildAutosaveDocument(ObjectPersistentWorkspaceSnapshot workspace)
    {
        IReadOnlyList<string> orderedFolders = ObjectFolderUtility.OrderFolders(
            workspace.Folders.Concat(workspace.Objects.Select(static snapshot => snapshot.FolderPath)));

        return new ObjectLayoutAutosaveDocument
        {
            DocumentKind = ObjectLayoutAutosaveDocument.DocumentKindValue,
            FormatVersion = ObjectLayoutAutosaveDocument.CurrentFormatVersion,
            SavedAtUtc = workspace.CapturedAtUtc,
            PersistentRevision = workspace.Revision,
            DefaultLayoutId = workspace.DefaultLayoutId,
            Name = ObjectStringUtility.TrimOrFallback(workspace.Name, "Current object workspace"),
            Objects = workspace.Objects.Select(BuildAutosaveObject).ToList(),
            Folders = [.. orderedFolders],
            FolderColors = new Dictionary<string, string>(
                ObjectFolderUtility.OrderFolderColorMap(workspace.FolderColors, orderedFolders),
                StringComparer.OrdinalIgnoreCase),
        };
    }

    private static ObjectLayoutFileDocument BuildLayoutDocument(ObjectLayoutSnapshot layout)
    {
        IReadOnlyList<string> folders = ObjectFolderUtility.OrderFolders(
            layout.Folders.Concat(layout.Objects.Select(static snapshot => snapshot.FolderPath)));

        return new ObjectLayoutFileDocument
        {
            DocumentKind = ObjectLayoutFileDocument.DocumentKindValue,
            FormatVersion = ObjectLayoutFileDocument.CurrentFormatVersion,
            Id = layout.Id,
            Name = layout.Name,
            ExportedAtUtc = DateTime.UtcNow,
            CreatedAtUtc = layout.CreatedAtUtc,
            UpdatedAtUtc = layout.UpdatedAtUtc,
            Objects = layout.Objects.Select(BuildLayoutFileObject).ToList(),
            Folders = [.. folders],
            FolderColors = new Dictionary<string, string>(
                ObjectFolderUtility.OrderFolderColorMap(layout.FolderColors, folders),
                StringComparer.OrdinalIgnoreCase),
        };
    }

    private static ObjectLayoutFileObject BuildLayoutFileObject(ObjectSnapshot snapshot)
        => new()
        {
            FolderPath = ObjectFolderUtility.SanitizeFolderPath(snapshot.FolderPath),
            Locked = snapshot.Locked,
            Object = ObjectApiMapper.ToDto(snapshot),
        };

    private static ObjectLayoutAutosaveObject BuildAutosaveObject(ObjectSnapshot snapshot)
        => new()
        {
            LayoutId = snapshot.LayoutId,
            FolderPath = ObjectFolderUtility.SanitizeFolderPath(snapshot.FolderPath),
            Locked = snapshot.Locked,
            Object = ObjectApiMapper.ToDto(snapshot),
        };

    private static ObjectLayoutSnapshot BuildLayoutSnapshot(
        Guid layoutId,
        string name,
        DateTime createdAtUtc,
        DateTime updatedAtUtc,
        IReadOnlyList<ObjectSnapshot> snapshots,
        IReadOnlyList<string> folders,
        IReadOnlyDictionary<string, string> folderColors)
    {
        IReadOnlyList<string> orderedFolders = ObjectFolderUtility.OrderFolders(
            folders.Concat(snapshots.Select(static snapshot => snapshot.FolderPath)));

        return new ObjectLayoutSnapshot
        {
            Id = layoutId,
            Name = name,
            CreatedAtUtc = createdAtUtc,
            UpdatedAtUtc = updatedAtUtc,
            Objects = snapshots.OrderBy(static snapshot => snapshot.CreatedAtUtc).ToList(),
            Folders = orderedFolders,
            FolderColors = new Dictionary<string, string>(
                ObjectFolderUtility.OrderFolderColorMap(folderColors, orderedFolders),
                StringComparer.OrdinalIgnoreCase),
        };
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

    private static bool TryValidateLayoutDocumentRoot(JsonElement root, out string errorMessage)
        => TryValidateRequiredProperties(root, LayoutDocumentRootProperties, out errorMessage);

    private static bool TryValidateAutosaveDocumentRoot(JsonElement root, out string errorMessage)
        => TryValidateRequiredProperties(root, AutosaveDocumentRootProperties, out errorMessage);

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
            RequiredJsonValueKind.Object => element.ValueKind == JsonValueKind.Object,
            _ => false,
        };
    }

    private static bool TryDeserialize<TDocument>(JsonElement root, string sourceLabel, out TDocument? document, out string errorMessage)
    {
        try
        {
            document = root.Deserialize<TDocument>(JsonOptions);
        }
        catch (JsonException)
        {
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

    private static bool FailUnsupportedVersion(int version, out ObjectLayoutSnapshot layout, out string errorMessage)
    {
        layout = null!;
        errorMessage = $"Unsupported layout file version: {version}.";
        return false;
    }

    private static bool LooksLikeAutosave(JsonElement root)
        => root.ValueKind == JsonValueKind.Object
           && root.TryGetProperty(nameof(ObjectLayoutAutosaveDocument.DocumentKind), out JsonElement documentKind)
           && documentKind.ValueKind == JsonValueKind.String
           && string.Equals(documentKind.GetString(), ObjectLayoutAutosaveDocument.DocumentKindValue, StringComparison.Ordinal)
           && root.TryGetProperty(nameof(ObjectLayoutAutosaveDocument.FormatVersion), out _)
           && root.TryGetProperty(nameof(ObjectLayoutAutosaveDocument.Objects), out _);

    private static bool FailUnsupportedAutosaveVersion(int version, out ObjectPersistentWorkspaceSnapshot workspace, out string errorMessage)
    {
        workspace = null!;
        errorMessage = $"Unsupported autosave file version: {version}.";
        return false;
    }
}

