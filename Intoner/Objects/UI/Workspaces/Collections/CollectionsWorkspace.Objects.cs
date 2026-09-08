using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface;
using Intoner.Objects.Collections;
using Intoner.Objects.Models;
using Intoner.Objects.UI.Components;
using Intoner.Services.Configuration;
using Intoner.UI.Performance;
using System.Numerics;

namespace Intoner.Objects.UI;

internal sealed partial class CollectionsWorkspace
{
    private void DrawObjectCollectionEmptyWorkspace(float height)
    {
        Vector2 padding = EditorLayout.ResolveObjectListCardPadding();
        EditorCard.DrawPanelCard(
            "objectCollectionsEmptyWorkspace",
            ThemeColors.ButtonDefault with { W = 0.20f },
            ThemeColors.AccentPrimary with { W = 0.14f },
            EditorLayout.Scaled(6f),
            padding,
            height,
            () => EditorEmptyState.Draw(
                "Create a collection to assign Penumbra mods to placed objects.",
                EditorLayout.Positive(height - (padding.Y * 2f))));
    }

    private void DrawObjectCollectionObjectsSection(
        ObjectCollectionSnapshot collection,
        IReadOnlyList<ObjectSnapshot> assignedSnapshots,
        IReadOnlySet<Guid> activeObjectIds,
        float width,
        float height)
    {
        string filter = _objectCollectionAssignedObjectFilter.Trim();
        IReadOnlyList<ObjectSnapshot> visibleSnapshots = filter.Length == 0
            ? assignedSnapshots
            : assignedSnapshots
                .Where(snapshot => SceneItemPresentation.MatchesObjectSearchFilter(snapshot, activeObjectIds.Contains(snapshot.Id), filter))
                .ToList();
        _collectionPanel.DrawObjectCollectionPanel(
            "objectCollectionsObjectsSection",
            FontAwesomeIcon.Cube,
            "Assigned Objects",
            BuildAssignedObjectsSubtitle(assignedSnapshots.Count),
            ThemeColors.AccentPrimary,
            width,
            height,
            layout => CollectionPanel.DrawObjectCollectionPanelSearch(
                "objectCollectionAssignedObjectFilter",
                "Filter assigned objects",
                ref _objectCollectionAssignedObjectFilter,
                visibleSnapshots.Count,
                assignedSnapshots.Count,
                layout,
                layout.Max.X - EditorLayout.Scaled(8f)),
            contentHeight => DrawObjectCollectionAssignedObjectsList(
                collection,
                visibleSnapshots,
                assignedSnapshots.Count,
                activeObjectIds,
                contentHeight));
    }

    private void DrawObjectCollectionAssignedObjectsList(
        ObjectCollectionSnapshot collection,
        IReadOnlyList<ObjectSnapshot> visibleSnapshots,
        int assignedObjectCount,
        IReadOnlySet<Guid> activeObjectIds,
        float height)
    {
        if (visibleSnapshots.Count == 0)
        {
            string message = assignedObjectCount == 0
                ? $"No placed objects currently use {collection.Record.Name}."
                : "No assigned objects match the current filter.";
            EditorEmptyState.Draw(message, height);
            return;
        }

        IReadOnlyList<string> placedFolders = _sceneView.GetPlacedFolders();
        IReadOnlyList<Guid> selectableRowOrder = visibleSnapshots
            .Where(static snapshot => !snapshot.Locked)
            .Select(static snapshot => snapshot.Id)
            .ToList();
        SceneListRow.Metrics rowMetrics = SceneListRow.ResolveMetrics(SceneListRowSize.Compact);
        using var itemSpacing = ImRaii.PushStyle(
            ImGuiStyleVar.ItemSpacing,
            new Vector2(ImGui.GetStyle().ItemSpacing.X, 0f));

        ImGui.Dummy(new Vector2(0f, rowMetrics.ItemSpacing));
        UiVirtualList.Draw(
            visibleSnapshots,
            UiVirtualListOptions.Rows(rowMetrics.ItemHeight, rowMetrics.ItemSpacing),
            (snapshot, _) => _sceneItems.DrawPlacedObjectCard(
                snapshot,
                activeObjectIds.Contains(snapshot.Id),
                _interaction.Selection.Contains(snapshot.Id),
                placedFolders,
                selectableRowOrder,
                rowMetrics));
    }

    private static string BuildAssignedObjectsSubtitle(int count)
        => count == 1 ? "1 assigned object" : $"{count} assigned objects";
}
