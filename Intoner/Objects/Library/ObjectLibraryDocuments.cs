using Intoner.Scene;
using Intoner.Services.Serialization;
using System.Text.Json;

namespace Intoner.Objects.Library;

/// <summary> owns the object library document format and mapping </summary>
internal static class ObjectLibraryDocuments
{
    private const int FileVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public static ObjectLibraryStateDocument EmptyState { get; } = new()
    {
        Version = FileVersion,
        Entries = [],
        Folders = [],
    };

    public static ObjectLibraryStateDocument? DeserializeState(string json)
    {
        ObjectLibraryStateDocument? document = JsonSerializer.Deserialize<ObjectLibraryStateDocument>(json, JsonOptions);
        return document is { Version: FileVersion } ? document : null;
    }

    public static ObjectLibraryPrefabDocument? DeserializePrefab(string json)
    {
        ObjectLibraryPrefabDocument? document = JsonSerializer.Deserialize<ObjectLibraryPrefabDocument>(json, JsonOptions);
        return document is { Version: FileVersion } ? document : null;
    }

    public static string Serialize(ObjectLibraryStateDocument document)
        => JsonSerializer.Serialize(document, JsonOptions);

    public static string Serialize(ObjectLibraryPrefabDocument document)
        => JsonSerializer.Serialize(document, JsonOptions);

    private static ObjectLibraryStateDocument CreateState(
        IEnumerable<ObjectLibraryEntry> entries,
        IReadOnlyList<ObjectLibraryFolder> folders,
        IEnumerable<ObjectLibraryPrefab> prefabs)
    {
        HashSet<Guid> prefabEntryIds = prefabs.SelectMany(static prefab => prefab.EntryIds).ToHashSet();
        return new ObjectLibraryStateDocument
        {
            Version = FileVersion,
            Entries = entries.Where(entry => !prefabEntryIds.Contains(entry.Id)).ToArray(),
            Folders = folders.ToArray(),
        };
    }

    public static ObjectLibraryPrefabDocument CreatePrefab(
        ObjectLibraryPrefab prefab,
        IReadOnlyDictionary<Guid, ObjectLibraryEntry> entries)
        => new()
        {
            Version = FileVersion,
            Id = prefab.Id,
            Name = prefab.Name,
            ParentFolderId = prefab.ParentFolderId,
            Color = prefab.Color,
            CreatedAtUtc = prefab.CreatedAtUtc,
            CapturedIn = prefab.CapturedIn,
            Entries = prefab.EntryIds.Select(id => entries[id]).ToArray(),
        };

    public static ObjectLibraryContent CreateContent(
        ObjectLibraryStateDocument state,
        IEnumerable<ObjectLibraryPrefabFile> prefabFiles,
        out IReadOnlyList<ObjectLibraryPrefabConflict> conflicts)
    {
        ObjectLibraryContent stateContent = ObjectLibraryRules.Normalize(state.Entries, state.Folders, []);
        List<ObjectLibraryEntry> entries = [.. stateContent.Entries];
        List<ObjectLibraryPrefab> prefabs = [];
        List<ObjectLibraryPrefabConflict> rejected = [];
        HashSet<Guid> entryIds = entries.Select(static entry => entry.Id).ToHashSet();
        HashSet<Guid> groupIds = stateContent.Folders.Select(static folder => folder.Id).ToHashSet();
        Dictionary<Guid, HashSet<string>> groupNames = ObjectLibraryRules.CreateSiblingNameSets(stateContent.Folders);

        foreach (ObjectLibraryPrefabFile file in prefabFiles)
        {
            if (!TryCreatePrefabContent(file.Document, stateContent.Folders, out ObjectLibraryContent prefabContent))
            {
                continue;
            }

            ObjectLibraryPrefab prefab = prefabContent.Prefabs[0];
            ObjectLibraryEntry? duplicateEntry = prefabContent.Entries
                .FirstOrDefault(entry => entryIds.Contains(entry.Id));
            string? conflict = null;
            if (groupIds.Contains(prefab.Id))
            {
                conflict = $"group id {prefab.Id:D}";
            }
            else if (ObjectLibraryRules.GetSiblingNameSet(groupNames, prefab.ParentFolderId).Contains(prefab.Name))
            {
                conflict = $"group name '{prefab.Name}'";
            }
            else if (duplicateEntry is not null)
            {
                conflict = $"member id {duplicateEntry.Id:D}";
            }

            if (conflict is not null)
            {
                rejected.Add(new ObjectLibraryPrefabConflict(file.Path, conflict));
                continue;
            }

            _ = groupIds.Add(prefab.Id);
            _ = ObjectLibraryRules.GetSiblingNameSet(groupNames, prefab.ParentFolderId).Add(prefab.Name);
            entryIds.UnionWith(prefabContent.Entries.Select(static entry => entry.Id));
            entries.AddRange(prefabContent.Entries);
            prefabs.Add(prefab);
        }

        conflicts = rejected;
        return ObjectLibraryRules.Normalize(entries, stateContent.Folders, prefabs);
    }

    public static bool StateEquals(ObjectLibraryStateDocument left, ObjectLibraryStateDocument right)
        => left.Entries.SequenceEqual(right.Entries)
        && left.Folders.SequenceEqual(right.Folders);

    public static ObjectLibrarySavePlan CreateSavePlan(
        ObjectLibrarySnapshot before,
        ObjectLibraryContent after,
        string statePath,
        string prefabDirectoryPath,
        bool stateDocumentExists)
    {
        ObjectLibraryStateDocument previousState = CreateState(before.Entries, before.Folders, before.Prefabs);
        ObjectLibraryStateDocument nextState = CreateState(after.Entries, after.Folders, after.Prefabs);
        List<ObjectLibraryDocumentChange> changes = [];
        if (!stateDocumentExists || !StateEquals(previousState, nextState))
        {
            changes.Add(new ObjectLibraryDocumentChange(statePath, Serialize(nextState), Prefab: null));
        }

        Dictionary<Guid, ObjectLibraryEntry> previousEntries = before.Entries.ToDictionary(static entry => entry.Id);
        Dictionary<Guid, ObjectLibraryEntry> nextEntries = after.Entries.ToDictionary(static entry => entry.Id);
        Dictionary<Guid, ObjectLibraryPrefab> previousPrefabs = before.Prefabs.ToDictionary(static prefab => prefab.Id);
        foreach (ObjectLibraryPrefab prefab in after.Prefabs)
        {
            if (previousPrefabs.TryGetValue(prefab.Id, out ObjectLibraryPrefab? previousPrefab)
             && PrefabContentEquals(previousPrefab, prefab, previousEntries, nextEntries))
            {
                continue;
            }

            ObjectLibraryPrefabDocument document = CreatePrefab(prefab, nextEntries);
            changes.Add(new ObjectLibraryDocumentChange(
                GetPrefabPath(prefabDirectoryPath, prefab.Id),
                Serialize(document),
                document));
        }

        HashSet<Guid> nextPrefabIds = after.Prefabs.Select(static prefab => prefab.Id).ToHashSet();
        foreach (ObjectLibraryPrefab prefab in before.Prefabs.Where(prefab => !nextPrefabIds.Contains(prefab.Id)))
        {
            changes.Add(new ObjectLibraryDocumentChange(
                GetPrefabPath(prefabDirectoryPath, prefab.Id),
                Contents: null,
                Prefab: null));
        }

        return new ObjectLibrarySavePlan(nextState, changes);
    }

    public static string GetPrefabPath(string directoryPath, Guid prefabId)
        => Path.Combine(directoryPath, $"{prefabId:D}.json");

    public static bool IsValid(ObjectLibraryStateDocument document)
    {
        ObjectLibraryContent content = ObjectLibraryRules.Normalize(document.Entries, document.Folders, []);
        return content.Entries.SequenceEqual(document.Entries)
            && content.Folders.SequenceEqual(document.Folders);
    }

    public static bool IsValid(ObjectLibraryPrefabDocument document)
    {
        ObjectLibraryFolder[] parent = document.ParentFolderId is { } parentId
            ?
            [
                new ObjectLibraryFolder
                {
                    Id = parentId,
                    Name = "parent",
                    CreatedAtUtc = DateTimeOffset.UnixEpoch,
                },
            ]
            : [];
        return TryCreatePrefabContent(document, parent, out _);
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        JsonSerializerOptions options =
            JsonSerializerOptionsUtility.CreateStrictIndented(JsonNamingPolicy.CamelCase);
        options.IncludeFields = true;
        return options;
    }

    private static bool TryCreatePrefabContent(
        ObjectLibraryPrefabDocument document,
        IReadOnlyList<ObjectLibraryFolder> folders,
        out ObjectLibraryContent content)
    {
        ObjectLibraryEntry[] entries = document.Entries.ToArray();
        ObjectLibraryPrefab prefab = new()
        {
            Id = document.Id,
            Name = document.Name,
            ParentFolderId = document.ParentFolderId,
            Color = document.Color,
            CreatedAtUtc = document.CreatedAtUtc,
            CapturedIn = document.CapturedIn,
            EntryIds = entries.Select(static entry => entry.Id).ToArray(),
        };
        content = ObjectLibraryRules.Normalize(entries, folders, [prefab]);
        return content.Prefabs.Length == 1
            && content.Entries.Length == entries.Length
            && content.Entries.SequenceEqual(entries)
            && ObjectLibraryRules.PrefabEquals(content.Prefabs[0], prefab);
    }

    private static bool PrefabContentEquals(
        ObjectLibraryPrefab left,
        ObjectLibraryPrefab right,
        IReadOnlyDictionary<Guid, ObjectLibraryEntry> leftEntries,
        IReadOnlyDictionary<Guid, ObjectLibraryEntry> rightEntries)
        => ObjectLibraryRules.PrefabEquals(left, right)
        && right.EntryIds.All(id => leftEntries.TryGetValue(id, out ObjectLibraryEntry? leftEntry)
            && rightEntries.TryGetValue(id, out ObjectLibraryEntry? rightEntry)
            && leftEntry == rightEntry);
}

internal sealed record ObjectLibraryStateDocument
{
    public required int Version { get; init; }
    public required IReadOnlyList<ObjectLibraryEntry> Entries { get; init; }
    public required IReadOnlyList<ObjectLibraryFolder> Folders { get; init; }
}

internal sealed record ObjectLibraryPrefabDocument
{
    public required int Version { get; init; }
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public Guid? ParentFolderId { get; init; }
    public string Color { get; init; } = string.Empty;
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public required SceneCreationContext CapturedIn { get; init; }
    public required IReadOnlyList<ObjectLibraryEntry> Entries { get; init; }
}

internal readonly record struct ObjectLibraryPrefabFile(
    string Path,
    ObjectLibraryPrefabDocument Document);

internal readonly record struct ObjectLibraryPrefabConflict(
    string Path,
    string Reason);

internal sealed record ObjectLibrarySavePlan(
    ObjectLibraryStateDocument State,
    IReadOnlyList<ObjectLibraryDocumentChange> Changes);

internal sealed record ObjectLibraryDocumentChange(
    string Path,
    string? Contents,
    ObjectLibraryPrefabDocument? Prefab);
