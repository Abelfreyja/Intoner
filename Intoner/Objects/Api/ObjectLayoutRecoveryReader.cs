using Intoner.Objects.Models;
using Intoner.Objects.Runtime;
using Intoner.Objects.Utils;
using Intoner.Services.Serialization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Intoner.Objects.Api;

internal static class ObjectLayoutRecoveryReader
{
    public static bool TryRead(string json, IObjectKindService kinds, out ObjectLayoutImportPayload payload, out string error)
    {
        payload = null!;
        try
        {
            using JsonDocument source = JsonDocumentUtility.Parse(json);
            JsonElement root = source.RootElement;
            if (!ObjectLayoutFileUtility.LooksLikeObjectLayout(root)
                || !root.TryGetPropertyIgnoreCase("Objects", out JsonElement entries) || entries.ValueKind != JsonValueKind.Array
                || !root.TryGetPropertyIgnoreCase("FormatVersion", out JsonElement versionNode) || versionNode.ValueKind != JsonValueKind.Number || !versionNode.TryGetInt32(out int version))
            {
                error = "Select an Intoner layout with a valid header and object list.";
                return false;
            }

            JsonElement groupEntries = root.TryGetPropertyIgnoreCase("Groups", out JsonElement groupsNode)
                ? groupsNode : JsonSerializer.SerializeToElement(Array.Empty<PersistentSceneGroup>());
            if (groupEntries.ValueKind != JsonValueKind.Array || version < 3 && groupEntries.GetArrayLength() != 0)
            {
                error = "The layout has invalid group metadata for its format version.";
                return false;
            }

            JsonObject header = new(new JsonNodeOptions { PropertyNameCaseInsensitive = true });
            foreach (JsonProperty property in root.EnumerateObject().Where(static property =>
                         !property.Name.Equals("Objects", StringComparison.OrdinalIgnoreCase)
                         && !property.Name.Equals("Groups", StringComparison.OrdinalIgnoreCase)))
            {
                header.Add(property.Name, JsonNode.Parse(property.Value.GetRawText()));
            }

            header["Objects"] = new JsonArray();
            header["Groups"] = new JsonArray();
            if (version == 1)
            {
                if (!ObjectLayoutJsonSerializer.TryUpgradeLayoutFolders(root, header, out error))
                {
                    return false;
                }

                header["FormatVersion"] = ObjectLayoutFileDocument.CurrentFormatVersion;
            }

            if (!ObjectLayoutJsonSerializer.TryDeserializeLayout(header.ToJsonString(), out ObjectLayoutSnapshot layout, out error))
            {
                return false;
            }

            JsonSerializerOptions options = version < 3 ? ObjectLayoutJsonSerializer.ReleasedJsonOptions : ObjectLayoutJsonSerializer.JsonOptions;
            List<string> notes = [];
            List<(ObjectSnapshot Value, string Label)> objects = [];
            List<(SceneGroupSnapshot Value, string Label)> groups = [];
            Dictionary<Guid, int> identities = [];
            int index = 0;
            foreach (JsonElement entry in entries.EnumerateArray())
            {
                string label = $"Objects[{index++}]";
                try
                {
                    CountIdentity(entry.TryGetPropertyIgnoreCase("Object", out JsonElement item) ? item : default, identities);
                    ObjectLayoutFileObject? decoded = entry.Deserialize<ObjectLayoutFileObject>(options);
                    if (decoded is null || !ObjectLayoutJsonSerializer.TryToSnapshot(decoded, layout.Id, out ObjectSnapshot snapshot)
                        || !kinds.TrySanitizeSnapshot(snapshot, out ObjectSnapshot sanitized))
                    {
                        notes.Add($"{label}: excluded invalid identity, timestamp, object type, or model data.");
                        continue;
                    }

                    objects.Add((sanitized, $"{label} ('{snapshot.Name}', {snapshot.Id:D})"));
                    if (sanitized != snapshot)
                    {
                        notes.Add($"{label}: normalized object values using the standard object rules.");
                    }
                }
                catch (Exception ex) when (ex is JsonException or ArgumentException)
                {
                    notes.Add($"{label}: excluded invalid data: {ex.Message}");
                }
            }

            index = 0;
            foreach (JsonElement entry in groupEntries.EnumerateArray())
            {
                string label = $"Groups[{index++}]";
                try
                {
                    CountIdentity(entry, identities);
                    PersistentSceneGroup? decoded = entry.Deserialize<PersistentSceneGroup>(options);
                    if (!ObjectApiMapper.TryToGroup(decoded, layout.Id, out SceneGroupSnapshot mapped))
                    {
                        notes.Add($"{label}: excluded invalid group metadata.");
                        continue;
                    }

                    groups.Add((mapped, label));
                }
                catch (Exception ex) when (ex is JsonException or ArgumentException)
                {
                    notes.Add($"{label}: excluded invalid data: {ex.Message}");
                }
            }

            objects.RemoveAll(entry => ExcludeDuplicate(entry.Value.Id, entry.Label, identities, notes));
            groups.RemoveAll(entry => ExcludeDuplicate(entry.Value.Id, entry.Label, identities, notes));
            Dictionary<Guid, SceneGroupSnapshot> groupMap = groups.ToDictionary(static entry => entry.Value.Id, static entry => entry.Value);
            HashSet<Guid> objectIds = objects.Select(static entry => entry.Value.Id).ToHashSet();
            List<ObjectSnapshot> snapshots = [];
            foreach ((ObjectSnapshot snapshot, string label) in objects)
            {
                ObjectSnapshot recovered = snapshot;
                if (snapshot.GroupId is Guid groupId
                    && (!groupMap.TryGetValue(groupId, out SceneGroupSnapshot? group) || !SceneGroupUtility.IsValidMember(snapshot, group)))
                {
                    recovered = recovered with { GroupId = null };
                    notes.Add($"{label}: detached invalid group membership; kept the world transform.");
                }

                if (snapshot.Model is FurnitureModel { AttachmentParentId: { } parent } && !objectIds.Contains(parent))
                {
                    notes.Add($"{label}: detached an attachment whose parent could not be recovered.");
                }

                snapshots.Add(recovered);
            }

            if (snapshots.Count == 0 && groups.Count == 0 && layout.Folders.Count == 0)
            {
                error = "No valid objects, groups, or folders could be recovered.";
                return false;
            }

            if (!ObjectSnapshotUtility.TryCopyWithNewIds(snapshots, groups.Select(static entry => entry.Value).ToArray(),
                    out List<ObjectSnapshot> copies, out List<SceneGroupSnapshot> groupCopies))
            {
                error = "The recovered objects do not have unambiguous identities.";
                return false;
            }

            string name = $"{layout.Name} (Recovered)";
            payload = new ObjectLayoutImportPayload(name, copies.Select(static item => item with { LayoutId = null }).ToArray(),
                ObjectFolderUtility.ExpandFolderEntries(layout.Folders, copies, groupCopies),
                $"Recovered {copies.Count} objects and {groupCopies.Count} groups into '{name}'. The original file is unchanged.")
            {
                Groups = groupCopies.Select(static group => group with { LayoutId = null }).ToArray(),
                RecoveryNotes = notes,
            };
            error = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            error = $"The layout cannot be recovered: {ex.Message}";
            return false;
        }
    }

    private static void CountIdentity(JsonElement item, Dictionary<Guid, int> identities)
    {
        if (item.TryGetPropertyIgnoreCase("Id", out JsonElement idNode) && idNode.ValueKind == JsonValueKind.String
            && idNode.TryGetGuid(out Guid id) && id != Guid.Empty)
        {
            identities[id] = identities.GetValueOrDefault(id) + 1;
        }
    }

    private static bool ExcludeDuplicate(Guid id, string label, IReadOnlyDictionary<Guid, int> identities, List<string> notes)
    {
        if (identities.GetValueOrDefault(id) <= 1)
        {
            return false;
        }

        notes.Add($"{label}: excluded duplicate scene item identity {id:D}.");
        return true;
    }
}
