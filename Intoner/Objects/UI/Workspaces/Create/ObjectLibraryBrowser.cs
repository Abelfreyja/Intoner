using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Intoner.Objects.Catalog;
using Intoner.Objects.Library;
using Intoner.Objects.Models;
using Intoner.Objects.Runtime;
using Intoner.Objects.UI.Components;
using Intoner.Objects.UI.Services;
using Intoner.Objects.Utils;
using Intoner.Scene;
using Intoner.Services;
using System.Numerics;

namespace Intoner.Objects.UI;

internal sealed partial class ObjectLibraryBrowser
{
    private readonly IObjectFolderService _objectFolderService;
    private readonly IObjectSceneView _sceneView;
    private readonly IObjectKindService _objectKindService;
    private readonly IObjectCatalogService _objectCatalog;
    private readonly IObjectLibrary _objectLibrary;
    private readonly IClipboardTextService _clipboardText;
    private readonly IObjectClipboardService _objectClipboard;
    private readonly IScenePlacementService _scenePlacementService;
    private readonly EditorInteraction _interaction;
    private readonly SceneEditorCommands _sceneCommands;
    private readonly SceneListRow _sceneListRow;
    private readonly EditorFilterBar _filterBar;
    private readonly ObjectBrowserRowRenderer _browserRows;
    private readonly CreateBrowserSelection _browserSelection;
    private readonly ObjectCreationDraft _draft;
    private readonly ObjectCreateActions _createActions;
    private readonly ObjectBrowserPanel _browserPanel;
    private readonly CatalogLibraryStatus _libraryStatus;
    private readonly HashSet<Guid> _collapsedLibraryGroups = [];
    private string _libraryFilter = string.Empty;
    private string _libraryKindFilter = string.Empty;
    private long _librarySelectionRevision = -1;
    private int _libraryGroupStateRevision;
    private LibraryViewCache? _libraryViewCache;
    private LibraryKindCountsCache? _libraryKindCountsCache;
    private LibraryPresetMatchesCache? _libraryPresetMatchesCache;

    public ObjectLibraryBrowser(
        IObjectFolderService objectFolderService,
        IObjectSceneView sceneView,
        IObjectKindService objectKindService,
        IObjectCatalogService objectCatalog,
        IObjectLibrary objectLibrary,
        IClipboardTextService clipboardText,
        IObjectClipboardService objectClipboard,
        IScenePlacementService scenePlacementService,
        EditorInteraction interaction,
        SceneEditorCommands sceneCommands,
        SceneListRow sceneListRow,
        EditorFilterBar filterBar,
        ObjectBrowserRowRenderer browserRows,
        CreateBrowserSelection browserSelection,
        ObjectCreationDraft draft,
        ObjectCreateActions createActions,
        ObjectBrowserPanel browserPanel,
        CatalogLibraryStatus libraryStatus)
    {
        _objectFolderService   = objectFolderService;
        _sceneView             = sceneView;
        _objectKindService     = objectKindService;
        _objectCatalog         = objectCatalog;
        _objectLibrary         = objectLibrary;
        _clipboardText         = clipboardText;
        _objectClipboard       = objectClipboard;
        _scenePlacementService = scenePlacementService;
        _interaction           = interaction;
        _sceneCommands         = sceneCommands;
        _sceneListRow          = sceneListRow;
        _filterBar             = filterBar;
        _browserRows           = browserRows;
        _browserSelection      = browserSelection;
        _draft                 = draft;
        _createActions         = createActions;
        _browserPanel          = browserPanel;
        _libraryStatus         = libraryStatus;
    }

    internal void DrawCatalogEntryLibraryContext(string id, string name, ObjectLibraryPreset preset)
    {
        using EditorContextMenu.PopupScope contextMenu = EditorContextMenu.BeginForLastItem($"##catalogLibrary:{id}");
        if (!contextMenu)
        {
            return;
        }

        EditorContextMenu.DrawFirstSectionLabel(name);
        if (EditorContextMenu.DrawItem(
                FontAwesomeIcon.Play,
                ObjectEditorCatalog.ResolvePlacementActionLabel(preset.Kind),
                enabled: _createActions.CanPlaceObjectPreset(preset)))
        {
            _ = _createActions.TryPlaceObjectPreset(preset);
        }

        DrawCopyAssetPathAction(preset);

        EditorContextMenu.DrawSeparator();
        ObjectLibrarySnapshot library = _objectLibrary.Current;
        IReadOnlyList<ObjectLibraryEntry> sourceEntries = ObjectLibraryQuery.FindEntriesBySource(library, preset);
        if (sourceEntries.Count > 0)
        {
            DrawOpenLibraryAction($"catalogLibraryOpen:{id}", sourceEntries);
        }

        using EditorContextMenu.SubMenuScope addMenu = EditorContextMenu.BeginSubMenu(
            $"catalogLibraryAdd:{id}",
            FontAwesomeIcon.FolderPlus,
            "Add to Library");
        if (!addMenu)
        {
            return;
        }

        IReadOnlyList<ObjectLibraryFolder> folders = OrderLibraryFolders(library.Folders);
        FolderSelectionMenu.Draw(
            folders,
            false,
            static _ => false,
            static folder => folder.Id.ToString(),
            static folder => folder.ParentFolderId?.ToString() ?? string.Empty,
            static folder => folder.Name,
            () => _ = _objectLibrary.TryAdd(name, preset, null, out _),
            folder => _ = _objectLibrary.TryAdd(name, preset, folder.Id, out _));

        EditorContextMenu.DrawSeparator();
        if (EditorContextMenu.DrawItem(FontAwesomeIcon.FolderPlus, "New Folder..."))
        {
            OpenCreateLibraryFolderDialog(folderName =>
                _objectLibrary.TryAddToNewFolder(name, preset, folderName, null, out _, out _));
        }
    }

    private uint? ResolveLibraryItemIconId(ObjectLibraryEntry libraryEntry)
    {
        if (libraryEntry.Preset.Model is not FurnitureModel furniture
         || ResolveLibraryFurnitureVariant(furniture) is not { } variant)
        {
            return null;
        }

        return variant.IconId > 0 ? variant.IconId : null;
    }

    private EditorBadge? ResolveLibraryBadge(ObjectLibraryPreset preset)
    {
        if (preset.Model is LightModel light)
        {
            return ObjectEditorCatalog.FindLightCatalogEntry(light.LightType) is { } lightEntry
                ? ObjectEditorCatalog.ResolveLightTypeBadge(lightEntry)
                : null;
        }

        if (preset.Model is BgObjectModel bgObject
         && _objectCatalog.TryResolveEntry(
                ObjectCatalogKind.BgObject,
                bgObject.ModelPath,
                out ObjectCatalogEntry? bgObjectEntry))
        {
            return _libraryStatus.CreateTerritoryUsageBadge(bgObjectEntry.BgObjectInfo);
        }

        if (preset.Model is not FurnitureModel furniture
         || ResolveLibraryFurnitureVariant(furniture) is not { } variant)
        {
            return null;
        }

        return FurnitureDataTooltip.CreateBadge(variant);
    }

    private ObjectCatalogFurnitureVariant? ResolveLibraryFurnitureVariant(FurnitureModel furniture)
    {
        if (!_objectCatalog.TryResolveFurnitureVariant(
                furniture.SharedGroupPath,
                furniture.HousingRowId,
                furniture.ItemRowId,
                out _,
                out ObjectCatalogFurnitureVariant? variant))
        {
            return null;
        }

        return variant;
    }

    internal sealed record LibraryViewCache(
        long Revision,
        int GroupStateRevision,
        string Filter,
        string KindFilter,
        LibraryBrowserView View);

    internal sealed record LibraryKindCountsCache(
        long Revision,
        IReadOnlyList<ObjectCatalogFilterCount> Counts);

    internal sealed record LibraryPresetMatchesCache(
        long Revision,
        ObjectLibraryPreset Preset,
        IReadOnlyList<ObjectLibraryEntry> Entries);

    internal void DrawObjectLibraryBrowser(int catalogCount)
    {
        ObjectLibrarySnapshot library = _objectLibrary.Current;
        PruneLibrarySelection(library);

        LibraryBrowserView view = ResolveLibraryView(library, _libraryFilter, _libraryKindFilter);
        IReadOnlyList<ObjectCatalogFilterCount> kindCounts = ResolveLibraryKindCounts(library);
        string filter = _libraryFilter;
        string kindFilter = _libraryKindFilter;
        float actionEdge = EditorIconButton.MeasureMaxEdge(FontAwesomeIcon.FolderPlus);
        float actionGap = ImGui.GetStyle().ItemSpacing.X;
        _browserPanel.DrawCatalogHeaderCardCore(
            "##objectLibrary",
            FontAwesomeIcon.LayerGroup,
            "Object Library",
            library.Entries.Count,
            view.VisibleEntryCount,
            catalogCount,
            ref filter,
            "search saved objects, folders, and prefabs",
            () =>
            {
                bool canPasteLibraryGroup = _objectClipboard.CanPasteLibraryGroup();
                using (ImRaii.Disabled(!canPasteLibraryGroup))
                {
                    if (EditorIconButton.DrawAccent(
                            "objectLibraryPaste",
                            FontAwesomeIcon.Paste,
                            canPasteLibraryGroup
                                ? "Paste a Library folder or prefab from the clipboard"
                                : "The clipboard does not contain a Library folder or prefab",
                            ThemeColors.AccentPrimary,
                            actionEdge))
                    {
                        PasteLibraryGroupFromClipboard();
                    }
                }

                ImGui.SameLine();
                if (EditorIconButton.DrawAccent(
                        "objectLibraryCreateFolder",
                        FontAwesomeIcon.FolderPlus,
                        "Create a Library folder",
                        ThemeColors.AccentPrimary,
                        actionEdge))
                {
                    OpenCreateLibraryFolderDialog();
                }
            },
            new Vector2((actionEdge * 2f) + actionGap, actionEdge),
            () =>
            {
                kindFilter = _filterBar.DrawCatalogFilterButtons("objectLibraryKinds", kindFilter, kindCounts);
            });
        _libraryFilter = filter;
        _libraryKindFilter = kindFilter;

        view = ResolveLibraryView(library, _libraryFilter, _libraryKindFilter);
        _browserPanel.DrawCatalogEntriesPanel(
            "##objectLibrary",
            view.VisibleRowCount,
            "No saved objects, folders, or prefabs match this search.",
            () => DrawLibraryRows(view));
    }

    private void PasteLibraryGroupFromClipboard()
    {
        if (!_objectClipboard.TryPasteLibraryGroup(out ObjectLibraryPasteResult result))
        {
            return;
        }

        _libraryFilter = string.Empty;
        _libraryKindFilter = string.Empty;
        if (_collapsedLibraryGroups.Remove(result.Group.Id))
        {
            ++_libraryGroupStateRevision;
        }

        _ = result.EntryIds.Count > 0
            ? _browserSelection.LibrarySelection.TryReplaceSelection(result.EntryIds)
            : _browserSelection.LibrarySelection.TryClear();
    }

    private LibraryBrowserView ResolveLibraryView(ObjectLibrarySnapshot library, string filter, string kindFilter)
    {
        string normalizedFilter = TextUtility.TrimOrEmpty(filter);
        string normalizedKind = TextUtility.TrimOrEmpty(kindFilter);
        if (_libraryViewCache is { } cache
         && cache.Revision == library.Revision
         && cache.GroupStateRevision == _libraryGroupStateRevision
         && string.Equals(cache.Filter, normalizedFilter, StringComparison.Ordinal)
         && string.Equals(cache.KindFilter, normalizedKind, StringComparison.Ordinal))
        {
            return cache.View;
        }

        LibraryBrowserView view = LibraryBrowserView.Create(library, normalizedFilter, normalizedKind, _collapsedLibraryGroups, ObjectEditorCatalog.BuildLibraryEntrySearchText);
        _libraryViewCache = new LibraryViewCache(
            library.Revision,
            _libraryGroupStateRevision,
            normalizedFilter,
            normalizedKind,
            view);
        return view;
    }

    private IReadOnlyList<ObjectCatalogFilterCount> ResolveLibraryKindCounts(ObjectLibrarySnapshot library)
    {
        if (_libraryKindCountsCache is { } cache && cache.Revision == library.Revision)
        {
            return cache.Counts;
        }

        IReadOnlyList<ObjectCatalogFilterCount> counts = BuildLibraryKindCounts(library.Entries);
        _libraryKindCountsCache = new LibraryKindCountsCache(library.Revision, counts);
        return counts;
    }

    private static IReadOnlyList<ObjectCatalogFilterCount> BuildLibraryKindCounts(IReadOnlyList<ObjectLibraryEntry> entries)
        => Enum.GetValues<ObjectKind>()
            .Where(static kind => kind is ObjectKind.Furniture or ObjectKind.Light or ObjectKind.Vfx or ObjectKind.BgObject)
            .Select(kind => new ObjectCatalogFilterCount(
                ObjectEditorCatalog.GetObjectKindLabel(kind),
                entries.Count(entry => entry.Preset.Kind == kind)))
            .Where(static count => count.Count > 0)
            .ToArray();

    private bool IsLibraryGroupCollapsed(Guid groupId)
        => _collapsedLibraryGroups.Contains(groupId);

    private void PruneLibrarySelection(ObjectLibrarySnapshot library)
    {
        if (_librarySelectionRevision == library.Revision)
        {
            return;
        }

        _librarySelectionRevision = library.Revision;
        _ = _browserSelection.LibrarySelection.TryPrune(library.Entries.Select(static entry => entry.Id).ToHashSet());
        HashSet<Guid> groupIds = library.Folders.Select(static folder => folder.Id)
            .Concat(library.Prefabs.Select(static prefab => prefab.Id))
            .ToHashSet();
        if (_collapsedLibraryGroups.RemoveWhere(groupId => !groupIds.Contains(groupId)) > 0)
        {
            ++_libraryGroupStateRevision;
        }
    }

    private void ToggleLibraryGroupCollapsed(Guid groupId)
    {
        if (!_collapsedLibraryGroups.Add(groupId))
        {
            _collapsedLibraryGroups.Remove(groupId);
        }

        ++_libraryGroupStateRevision;
    }

    internal IReadOnlyList<ObjectLibraryEntry> ResolveLibraryPresetMatches(
        ObjectLibrarySnapshot library,
        ObjectLibraryPreset preset)
    {
        if (_libraryPresetMatchesCache is { } cache
         && cache.Revision == library.Revision
         && cache.Preset == preset)
        {
            return cache.Entries;
        }

        IReadOnlyList<ObjectLibraryEntry> entries = ObjectLibraryQuery.FindEntries(library, preset);
        _libraryPresetMatchesCache = new LibraryPresetMatchesCache(library.Revision, preset, entries);
        return entries;
    }
}
