using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Intoner.Objects.Collections;
using Intoner.Objects.UI.Components;
using Intoner.UI.Performance;
using System.Numerics;
using static Intoner.Objects.UI.Components.CollectionStatusUi;
using static Intoner.Objects.UI.Components.EditorCard;

namespace Intoner.Objects.UI;

internal sealed partial class EditorWindow
{
    private void DrawObjectCollectionListPanel(IReadOnlyList<ObjectCollectionSnapshot> collections)
    {
        ObjectCollectionSnapshot? selectedCollection = collections
            .FirstOrDefault(collection => string.Equals(collection.Record.CollectionId, _selectedObjectCollectionId, StringComparison.OrdinalIgnoreCase));
        DrawObjectCollectionListHero(collections, selectedCollection);
        DrawObjectCollectionListCard(collections);
        DrawObjectCollectionCreatePopup();
    }

    private void DrawObjectCollectionListHero(
        IReadOnlyList<ObjectCollectionSnapshot> collections,
        ObjectCollectionSnapshot? selectedCollection)
    {
        var accent = EditorColors.AccentPurple;
        var actionButtonEdge = ResolveActionIconButtonEdge(FontAwesomeIcon.Swatchbook, FontAwesomeIcon.Trash);
        var actionWidth = ResolveActionStripWidth(actionButtonEdge, 2);

        DrawPanelCard(
            "objectCollectionsListHero",
            EditorColors.ButtonDefault with { W = 0.30f },
            accent with { W = 0.24f },
            Scaled(8f),
            ResolveObjectListCardPadding(),
            () =>
            {
                DrawCardHeader(
                    "objectCollectionsListHeroHeader",
                    FontAwesomeIcon.Swatchbook,
                    "Collections",
                    BuildObjectCollectionListStatus(collections, selectedCollection),
                    accent,
                    () =>
                    {
                        if (DrawAccentIconButton("objectCollectionCreate", FontAwesomeIcon.Swatchbook, "Create a new collection", EditorColors.AccentPurple, actionButtonEdge))
                        {
                            QueueCreateObjectCollectionPopup();
                        }

                        ImGui.SameLine();
                        using (ImRaii.Disabled(selectedCollection is null))
                        {
                            if (DrawAccentIconButton("objectCollectionDelete", FontAwesomeIcon.Trash, "Delete the selected collection", EditorColors.DimRed, actionButtonEdge))
                            {
                                DeleteSelectedObjectCollection();
                            }
                        }
                    },
                    actionWidth,
                    wrapSubtitle: true);
            });
    }

    private void DrawObjectCollectionListCard(IReadOnlyList<ObjectCollectionSnapshot> collections)
    {
        var padding = ResolveObjectListCardPadding();
        var itemSpacingY = ResolveObjectListItemSpacingY();
        var innerHeight = ResolveObjectListCardInnerHeight(padding, itemSpacingY);
        var background = EditorColors.ButtonDefault with { W = 0.24f };
        var rounding = Scaled(8f);

        DrawPanelCard(
            "objectCollectionsListCard",
            background,
            EditorColors.AccentPurple with { W = 0.18f },
            rounding,
            padding,
            () =>
            {
                if (collections.Count == 0)
                {
                    DrawPlacedObjectsEmptyState("Create a collection to start assigning Penumbra mods to objects.", innerHeight);
                    return;
                }

                using var childBg = ImRaii.PushColor(ImGuiCol.ChildBg, Vector4.Zero);
                using var child = ObjectScrollList.Begin(
                    "##objectCollectionsListScroll",
                    new Vector2(0f, innerHeight),
                    CreateOverlayScrollPanelOptions(background, rounding, EditorColors.AccentPurple));
                if (!child)
                {
                    return;
                }

                ImGui.Dummy(new Vector2(0f, itemSpacingY));

                var itemHeight = ResolveObjectListEntryHeight();
                UiVirtualList.Draw(
                    collections,
                    UiVirtualListOptions.Rows(itemHeight, itemSpacingY),
                    (collection, _) => DrawObjectCollectionListEntry(collection, itemHeight));
            });
    }

    private void DrawObjectCollectionListEntry(ObjectCollectionSnapshot collection, float height)
    {
        bool selected = string.Equals(_selectedObjectCollectionId, collection.Record.CollectionId, StringComparison.OrdinalIgnoreCase);
        Vector4 accent = ResolveObjectCollectionAccentColor(collection.ResolveState);
        if (DrawObjectListEntryCard(
            $"objectCollectionEntry:{collection.Record.CollectionId}",
            collection.Record.Name,
            BuildObjectCollectionEntryDetail(collection),
            collection.Record.Entries.Count.ToString(),
            BuildAssignedModsSubtitle(collection.Record.Entries.Count),
            selected,
            accent,
            height))
        {
            _selectedObjectCollectionId = collection.Record.CollectionId;
            LoadObjectCollectionNameDraft(collection);
        }
    }
}

