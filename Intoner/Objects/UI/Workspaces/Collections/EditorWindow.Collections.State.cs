using Dalamud.Interface;
using Intoner.Objects.Collections;
using Intoner.Objects.Models;
using Intoner.Objects.UI.Components;
using Intoner.Objects.Utils;

namespace Intoner.Objects.UI;

internal sealed partial class EditorWindow
{
    private static float ResolveActionIconButtonEdge(params FontAwesomeIcon[] icons)
        => icons.Max(icon => ResolveSquareIconButtonMetrics(icon.ToIconString()).Edge);

    private bool CreateObjectCollection(string name)
    {
        if (!_objectCollectionManager.TryCreateCollection(name, out ObjectCollectionSnapshot snapshot))
        {
            return false;
        }

        _selectedObjectCollectionId = snapshot.Record.CollectionId;
        LoadObjectCollectionNameDraft(snapshot);
        return true;
    }

    private void CommitObjectCollectionName(ObjectCollectionSnapshot collection)
    {
        if (string.Equals(_objectCollectionNameDraft, collection.Record.Name, StringComparison.Ordinal))
        {
            _objectCollectionNameCommitted = collection.Record.Name;
            _objectCollectionNameDraftCollectionId = collection.Record.CollectionId;
            return;
        }

        if (_objectCollectionManager.TryUpdateCollection(collection.Record with { Name = _objectCollectionNameDraft }, out ObjectCollectionSnapshot updatedSnapshot))
        {
            LoadObjectCollectionNameDraft(updatedSnapshot);
            return;
        }

        LoadObjectCollectionNameDraft(collection);
    }

    private void RecompileObjectCollection(ObjectCollectionSnapshot collection)
        => _objectCollectionManager.EnsureCollectionMaterialized(collection.Record.CollectionId, forceResolve: true);

    private void OpenDeleteObjectCollectionDialog(ObjectCollectionSnapshot collection)
    {
        string collectionId = collection.Record.CollectionId;
        OpenDialog(EditorDialog.Request.TryConfirmation(
            "collection-delete",
            "Delete Collection",
            "Delete Collection",
            () => TryDeleteObjectCollection(collectionId)) with
        {
            Icon = FontAwesomeIcon.Trash,
            ConfirmIcon = FontAwesomeIcon.Trash,
            Accent = EditorColors.DimRed,
            Detail = collection.Record.Name,
            Description = "This permanently deletes the collection and unassigns it from placed objects. Installed Penumbra mods are NOT deleted.",
            FailureMessage = "The collection could not be deleted.",
        });
    }

    private bool TryDeleteObjectCollection(string collectionId)
    {
        if (!_objectCollectionManager.TryGetCollection(collectionId, out ObjectCollectionSnapshot collection))
        {
            return false;
        }

        IReadOnlyList<ObjectSnapshot> affectedSnapshots = _sceneView.GetPlacedObjectSnapshots()
            .Where(snapshot => string.Equals(snapshot.CollectionId, collection.Record.CollectionId, StringComparison.OrdinalIgnoreCase))
            .Select(snapshot => snapshot with { CollectionId = string.Empty })
            .ToList();
        if (affectedSnapshots.Count > 0 && !_mutationService.TryUpdateMany(affectedSnapshots, out _))
        {
            return false;
        }

        if (!_objectCollectionManager.TryDeleteCollection(collection.Record.CollectionId))
        {
            return false;
        }

        if (string.Equals(_selectedObjectCollectionId, collection.Record.CollectionId, StringComparison.OrdinalIgnoreCase))
        {
            _selectedObjectCollectionId = string.Empty;
            LoadObjectCollectionNameDraft(null);
        }

        return true;
    }

    private bool AddObjectCollectionEntry(ObjectCollectionSnapshot collection, ObjectAvailableMod mod)
    {
        string modDirectory = ObjectCollectionKeyUtility.NormalizeModDirectory(mod.ModDirectory);
        if (modDirectory.Length == 0
         || collection.Record.Entries.Any(entry => string.Equals(ObjectCollectionKeyUtility.NormalizeModDirectory(entry.ModDirectory), modDirectory, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        List<ObjectCollectionModSettings> entries = [.. collection.Record.Entries];
        entries.Add(new ObjectCollectionModSettings
        {
            ModDirectory = mod.ModDirectory,
            ModName = mod.ModName,
        });

        return _objectCollectionManager.TryUpdateCollection(collection.Record with { Entries = entries }, out _);
    }

    private void RemoveObjectCollectionEntry(ObjectCollectionSnapshot collection, int index)
    {
        if (index < 0 || index >= collection.Record.Entries.Count)
        {
            return;
        }

        List<ObjectCollectionModSettings> entries = [.. collection.Record.Entries];
        entries.RemoveAt(index);
        _ = _objectCollectionManager.TryUpdateCollection(collection.Record with { Entries = entries }, out _);
    }

    private void UpdateObjectCollectionEntry(ObjectCollectionSnapshot collection, int index, ObjectCollectionModSettings updatedEntry)
    {
        if (index < 0 || index >= collection.Record.Entries.Count)
        {
            return;
        }

        List<ObjectCollectionModSettings> entries = [.. collection.Record.Entries];
        entries[index] = updatedEntry;
        _ = _objectCollectionManager.TryUpdateCollection(collection.Record with { Entries = entries }, out _);
    }

    private void UpdateObjectCollectionModGroupSelection(
        ObjectCollectionSnapshot collection,
        int index,
        string groupName,
        IReadOnlyList<string> selectedOptionNames)
    {
        if (index < 0 || index >= collection.Record.Entries.Count)
        {
            return;
        }

        string normalizedGroupName = CollectionModSettingsUtility.NormalizeGroupName(groupName);
        if (normalizedGroupName.Length == 0)
        {
            return;
        }

        ObjectCollectionModSettings entry = collection.Record.Entries[index];
        Dictionary<string, List<string>> settings = CollectionModSettingsUtility.CloneSettings(entry.Settings);
        CollectionModSettingsUtility.RemoveGroup(settings, normalizedGroupName);
        if (!CollectionModSettingsUtility.TryNormalizeOptionNames(selectedOptionNames, out List<string> normalizedOptionNames))
        {
            return;
        }

        settings[normalizedGroupName] = normalizedOptionNames;

        UpdateObjectCollectionEntry(collection, index, entry with { Settings = settings });
    }

    private void ClearObjectCollectionModGroupSelection(
        ObjectCollectionSnapshot collection,
        int index,
        string groupName)
    {
        if (index < 0 || index >= collection.Record.Entries.Count)
        {
            return;
        }

        string normalizedGroupName = CollectionModSettingsUtility.NormalizeGroupName(groupName);
        if (normalizedGroupName.Length == 0)
        {
            return;
        }

        ObjectCollectionModSettings entry = collection.Record.Entries[index];
        Dictionary<string, List<string>> settings = CollectionModSettingsUtility.CloneSettings(entry.Settings);
        if (!CollectionModSettingsUtility.RemoveGroup(settings, normalizedGroupName))
        {
            return;
        }

        UpdateObjectCollectionEntry(collection, index, entry with { Settings = settings });
    }

    private void ApplyObjectCollectionToSelectedObjects(string collectionId, IReadOnlyList<ObjectSnapshot> selectedSnapshots)
    {
        string normalizedCollectionId = ObjectCollectionKeyUtility.NormalizeCollectionId(collectionId);
        IReadOnlyList<ObjectSnapshot> updatedSnapshots = selectedSnapshots
            .Where(snapshot => !string.Equals(ObjectCollectionKeyUtility.NormalizeCollectionId(snapshot.CollectionId), normalizedCollectionId, StringComparison.OrdinalIgnoreCase))
            .Select(snapshot => snapshot with { CollectionId = normalizedCollectionId })
            .ToList();
        if (updatedSnapshots.Count == 0)
        {
            return;
        }

        _objectCollectionManager.EnsureCollectionMaterialized(normalizedCollectionId, updatedSnapshots);
        _ = _mutationService.TryUpdateMany(updatedSnapshots, out _);
    }

    private static bool HasObjectCollectionAssignmentChange(IReadOnlyList<ObjectSnapshot> snapshots, string collectionId)
    {
        string normalizedCollectionId = ObjectCollectionKeyUtility.NormalizeCollectionId(collectionId);
        foreach (ObjectSnapshot snapshot in snapshots)
        {
            if (!string.Equals(ObjectCollectionKeyUtility.NormalizeCollectionId(snapshot.CollectionId), normalizedCollectionId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasAnyObjectCollectionAssignment(IReadOnlyList<ObjectSnapshot> snapshots)
    {
        foreach (ObjectSnapshot snapshot in snapshots)
        {
            if (ObjectCollectionKeyUtility.NormalizeCollectionId(snapshot.CollectionId).Length > 0)
            {
                return true;
            }
        }

        return false;
    }

    private IReadOnlyList<ObjectSnapshot> ResolveSelectedPersistedSnapshots()
        => _sceneView.GetPlacedObjectSnapshots()
            .Where(snapshot => _editorSelection.SelectedObjectIds.Contains(snapshot.Id))
            .ToList();

    private void SyncSelectedObjectCollection(IReadOnlyList<ObjectCollectionSnapshot> collections)
    {
        if (_selectedObjectCollectionId.Length == 0)
        {
            if (collections.Count == 0)
            {
                LoadObjectCollectionNameDraft(null);
                return;
            }

            _selectedObjectCollectionId = collections[0].Record.CollectionId;
            LoadObjectCollectionNameDraft(collections[0]);
            return;
        }

        ObjectCollectionSnapshot? selectedCollection = collections
            .FirstOrDefault(collection => string.Equals(collection.Record.CollectionId, _selectedObjectCollectionId, StringComparison.OrdinalIgnoreCase));
        if (selectedCollection is null)
        {
            ObjectCollectionSnapshot? firstCollection = collections.FirstOrDefault();
            _selectedObjectCollectionId = firstCollection?.Record.CollectionId ?? string.Empty;
            LoadObjectCollectionNameDraft(firstCollection);
            return;
        }

        if (!string.Equals(_objectCollectionNameDraftCollectionId, selectedCollection.Record.CollectionId, StringComparison.OrdinalIgnoreCase))
        {
            LoadObjectCollectionNameDraft(selectedCollection);
            return;
        }

        if (string.Equals(_objectCollectionNameDraft, _objectCollectionNameCommitted, StringComparison.Ordinal)
         && !string.Equals(_objectCollectionNameCommitted, selectedCollection.Record.Name, StringComparison.Ordinal))
        {
            LoadObjectCollectionNameDraft(selectedCollection);
        }
    }

    private void LoadObjectCollectionNameDraft(ObjectCollectionSnapshot? collection)
    {
        _objectCollectionNameDraftCollectionId = collection?.Record.CollectionId ?? string.Empty;
        _objectCollectionNameDraft = collection?.Record.Name ?? string.Empty;
        _objectCollectionNameCommitted = collection?.Record.Name ?? string.Empty;
        if (_editingObjectCollectionNameId.Length > 0
         && !string.Equals(_editingObjectCollectionNameId, _objectCollectionNameDraftCollectionId, StringComparison.OrdinalIgnoreCase))
        {
            _editingObjectCollectionNameId = string.Empty;
            _focusObjectCollectionNameEdit = false;
        }
    }

    private bool TryResolveSelectedObjectCollection(
        IReadOnlyList<ObjectCollectionSnapshot> collections,
        out ObjectCollectionSnapshot selectedCollection)
        => TryResolveObjectCollectionById(collections, _selectedObjectCollectionId, out selectedCollection);

    private static bool TryResolveObjectCollectionById(
        IReadOnlyList<ObjectCollectionSnapshot> collections,
        string collectionId,
        out ObjectCollectionSnapshot resolvedCollection)
    {
        foreach (ObjectCollectionSnapshot collection in collections)
        {
            if (string.Equals(collection.Record.CollectionId, collectionId, StringComparison.OrdinalIgnoreCase))
            {
                resolvedCollection = collection;
                return true;
            }
        }

        resolvedCollection = default!;
        return false;
    }
}

