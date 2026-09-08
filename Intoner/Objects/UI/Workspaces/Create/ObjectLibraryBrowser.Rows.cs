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
            ImGui.Dummy(new Vector2(0f, folderMetrics.GroupSpacing));
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
                ImGui.Dummy(new Vector2(0f, folderMetrics.GroupSpacing));
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
        EditorListCard.Interaction interaction = SceneListRow.DrawFolderInteraction(
            row,
            metrics,
            leftInset: depth == 0 ? 0f : metrics.ChildInset * depth,
            rightInset: depth == 0 ? 0f : metrics.ChildRightInset);
        using (EditorContextMenu.PopupScope contextMenu = EditorContextMenu.BeginForLastItem($"##{row.Id}:context"))
        {
            if (contextMenu)
            {
                DrawLibraryGroupContextMenu(group);
            }
        }

        if (interaction.Clicked && string.IsNullOrWhiteSpace(_libraryFilter))
        {
            ToggleLibraryGroupCollapsed(group.Source.Id);
            collapsed = IsLibraryGroupCollapsed(group.Source.Id);
            row = row with { Collapsed = collapsed };
        }

        _sceneListRow.DrawFolderChrome(row, interaction);
        SceneListRow.DrawFolderContent(
            row,
            metrics,
            interaction,
            interaction.Max.X - EditorLayout.Scaled(10f));
        return new SceneListRow.FolderResult(collapsed, new SceneListRow.RowGeometry(interaction.Min, interaction.Max));
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
    {
        int childDepth = depth + 1;
        using SceneListRow.FolderChildrenScope children = SceneListRow.BeginFolderChildren(
            metrics,
            FolderColorPicker.ResolveAccent(group.Source.Color),
            childDepth);
        int childCount = group.VisibleEntries.Count + group.Children.Count;
        int childIndex = group.VisibleEntries.Count;
        using UiVirtualList.Scope list = UiVirtualList.Begin(
            group.VisibleEntries.Count,
            UiVirtualListOptions.Rows(itemHeight, metrics.ItemSpacing) with
            {
                DrawTrailingSpacing = group.Children.Count > 0,
            });
        while (list.Step())
        {
            for (int index = list.DisplayStart; index < list.DisplayEnd; ++index)
            {
                SceneListRow.RowGeometry geometry = DrawLibraryEntry(
                    group.VisibleEntries[index],
                    selectableOrder,
                    itemHeight,
                    metrics.ChildInset * childDepth,
                    metrics.ChildRightInset);
                children.Add(geometry);
                list.FinishItem(index);
            }
        }

        foreach (LibraryBrowserView.Group childGroup in group.Children)
        {
            bool collapsed = IsLibraryGroupCollapsed(childGroup.Source.Id)
                && string.IsNullOrWhiteSpace(_libraryFilter);
            SceneListRow.FolderResult row = DrawLibraryGroup(
                childGroup,
                metrics,
                collapsed,
                childDepth);
            children.Add(row.Geometry);
            bool hasNext = ++childIndex < childCount;
            if (!row.Collapsed && (childGroup.VisibleEntries.Count > 0 || childGroup.Children.Count > 0))
            {
                DrawLibraryGroupChildren(
                    childGroup,
                    selectableOrder,
                    metrics,
                    itemHeight,
                    childDepth);
            }

            children.AddSpacing(hasNext);
        }
    }

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
