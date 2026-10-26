using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface;
using Intoner.Objects.Library;
using Intoner.Objects.UI.Components;
using Intoner.Services.Configuration;
using Intoner.UI.Performance;
using System.Numerics;

namespace Intoner.Objects.UI;

internal sealed partial class ObjectLibraryBrowser
{
    private void DrawLibraryRows(LibraryBrowserView view)
    {
        SceneListRow.Metrics folderMetrics = SceneListRow.ResolveMetrics(SceneListRowSize.Compact) with
        {
            ChildRightInset = 0f,
        };
        float itemHeight = EditorLayout.Scaled(48f);
        float itemSpacing = EditorLayout.ResolveObjectListItemSpacingY();
        using var zeroSpacing = ImRaii.PushStyle(
            ImGuiStyleVar.ItemSpacing,
            new Vector2(ImGui.GetStyle().ItemSpacing.X, 0f));
        ImGui.Dummy(new Vector2(0f, EditorLayout.Scaled(2f)));

        UiVirtualList.Draw(
            view.RootEntries,
            UiVirtualListOptions.Rows(itemHeight, itemSpacing),
            (entry, _) =>
            {
                DrawLibraryEntry(entry, view.SelectableOrder, itemHeight);
            });

        if (view.RootEntries.Count > 0 && view.RootGroups.Count > 0)
        {
            ImGui.Dummy(new Vector2(0f, folderMetrics.ItemSpacing));
        }

        for (int groupIndex = 0; groupIndex < view.RootGroups.Count; ++groupIndex)
        {
            DrawLibraryGroupTree(
                view.RootGroups[groupIndex],
                view.SelectableOrder,
                folderMetrics,
                itemHeight,
                depth: 0);

            if (groupIndex + 1 < view.RootGroups.Count)
            {
                ImGui.Dummy(new Vector2(0f, folderMetrics.ItemSpacing));
            }
        }
    }

    private SceneListRow.RowGeometry DrawLibraryEntry(
        ObjectLibraryEntry entry,
        IReadOnlyList<Guid> selectableOrder,
        float height,
        float leftInset = 0f,
        float rightInset = 0f)
    {
        bool available = _createActions.CanUseLibraryPreset(entry.Preset);
        ObjectBrowserRowRenderer.ObjectBrowserRow row = new()
        {
            Id = $"library:{entry.Id}",
            Title = entry.Name,
            Detail = ObjectEditorCatalog.BuildLibraryEntryDetail(entry),
            TrailingBadge = ResolveLibraryBadge(entry.Preset),
            ItemIconId = ResolveLibraryItemIconId(entry),
            Selected = _browserSelection.LibrarySelection.Contains(entry.Id),
            Disabled = !available,
        };
        EditorListCard.Interaction interaction = _browserRows.DrawObjectBrowserRow(
            row,
            height,
            () => HandleLibrarySelection(entry, selectableOrder),
            leftInset,
            rightInset);
        if (interaction.RightClicked)
        {
            _ = _browserSelection.LibrarySelection.TrySelectForContextMenu(entry.Id);
        }

        using (EditorContextMenu.PopupScope contextMenu = EditorContextMenu.BeginForLastItem($"##objectLibraryEntry:{entry.Id}:context"))
        {
            if (contextMenu)
            {
                DrawLibraryEntryContextMenu(entry);
            }
        }

        return new SceneListRow.RowGeometry(interaction.Min, interaction.Max);
    }

    private SceneListRow.FolderResult DrawLibraryGroup(
        LibraryBrowserView.Group group,
        SceneListRow.Metrics metrics,
        bool collapsed,
        int depth)
    {
        Vector4 accent = FolderColorPicker.ResolveAccent(group.Source.Color);
        bool isPrefab = group.Source is ObjectLibraryPrefab;
        SceneListRow.Folder row = new()
        {
            Id = $"objectLibraryGroup:{group.Source.Id}",
            Name = group.Source.Name,
            Detail = ObjectEditorCatalog.BuildSavedObjectCountLabel(group.AllEntries.Count),
            Accent = accent,
            TypeIcon = isPrefab ? FontAwesomeIcon.Cubes : null,
            TypeLabel = isPrefab ? "Prefab" : null,
            ChildCount = group.AllEntries.Count,
            Collapsed = collapsed,
        };
        SceneListRow.FolderResult result = _sceneListRow.DrawFolder(
            row,
            metrics,
            (Browser: this, Group: group),
            static context => context.Browser.DrawLibraryGroupContextMenu(context.Group),
            allowCollapse: string.IsNullOrWhiteSpace(_libraryFilter),
            leftInset: depth == 0 ? 0f : metrics.ChildInset * depth,
            rightInset: depth == 0 ? 0f : metrics.ChildRightInset);
        if (result.Collapsed != collapsed)
        {
            ToggleLibraryGroupCollapsed(group.Source.Id);
        }

        return result;
    }

    private void DrawLibraryGroupTree(
        LibraryBrowserView.Group group,
        IReadOnlyList<Guid> selectableOrder,
        SceneListRow.Metrics metrics,
        float itemHeight,
        int depth)
    {
        bool collapsed = IsLibraryGroupCollapsed(group.Source.Id) && string.IsNullOrWhiteSpace(_libraryFilter);
        SceneListRow.FolderResult row = DrawLibraryGroup(group, metrics, collapsed, depth);
        if (!row.Collapsed && (group.VisibleEntries.Count > 0 || group.Children.Count > 0))
        {
            DrawLibraryGroupChildren(group, selectableOrder, metrics, itemHeight, depth);
        }
    }

    private void DrawLibraryGroupChildren(
        LibraryBrowserView.Group group,
        IReadOnlyList<Guid> selectableOrder,
        SceneListRow.Metrics metrics,
        float itemHeight,
        int depth)
        => SceneListRow.DrawFolderChildren(
            group.VisibleEntries, group.Children, metrics,
            FolderColorPicker.ResolveAccent(group.Source.Color), depth, itemHeight,
            (Browser: this, Order: selectableOrder, Metrics: metrics, Height: itemHeight),
            static (entry, childDepth, context) => context.Browser.DrawLibraryEntry(entry, context.Order, context.Height,
                context.Metrics.ChildInset * childDepth, context.Metrics.ChildRightInset),
            static (child, childDepth, context) => context.Browser.DrawLibraryGroup(child, context.Metrics,
                context.Browser.IsLibraryGroupCollapsed(child.Source.Id) && string.IsNullOrWhiteSpace(context.Browser._libraryFilter), childDepth),
            static (child, childDepth, context) => context.Browser.DrawLibraryGroupChildren(child, context.Order, context.Metrics, context.Height, childDepth));

    private void HandleLibrarySelection(ObjectLibraryEntry entry, IReadOnlyList<Guid> selectableOrder)
    {
        ImGuiIOPtr io = ImGui.GetIO();
        bool changed = io.KeyShift
            ? _browserSelection.LibrarySelection.TrySelectRange(entry.Id, selectableOrder, preserveSelection: io.KeyCtrl)
            : _browserSelection.LibrarySelection.TrySelect(entry.Id, toggleSelection: io.KeyCtrl);
        if (!changed || !_browserSelection.LibrarySelection.PrimaryItemId.HasValue)
        {
            return;
        }

        Guid primaryId = _browserSelection.LibrarySelection.PrimaryItemId.Value;
        ObjectLibraryEntry? primary = ObjectLibraryQuery.FindEntry(_objectLibrary.Current, primaryId);
        if (primary is not null && _createActions.CanUseLibraryPreset(primary.Preset))
        {
            _draft.ApplyLibraryPreset(primary.Preset);
        }
    }
}
