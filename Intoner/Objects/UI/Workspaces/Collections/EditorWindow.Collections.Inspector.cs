using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Intoner.Objects.Collections;
using Intoner.Objects.Models;
using Intoner.Objects.UI.Components;
using System.Numerics;
using static Intoner.Objects.UI.Components.CollectionStatusUi;
using static Intoner.Objects.UI.Components.EditorCard;

namespace Intoner.Objects.UI;

internal sealed partial class EditorWindow
{
    private void DrawObjectCollectionInspectorPanel(IReadOnlyList<ObjectCollectionSnapshot> collections)
    {
        if (!TryResolveSelectedObjectCollection(collections, out ObjectCollectionSnapshot selectedCollection))
        {
            DrawUtilityWorkspaceHero(
                "objectCollectionsInspectorHero",
                FontAwesomeIcon.Cubes,
                "Collection Editor",
                "select or create a collection to edit assigned Penumbra mods");
            DrawUtilityWorkspacePlaceholderCard("objectCollectionsInspectorPlaceholder", "Select a collection to edit.");
            return;
        }

        IReadOnlyList<ObjectSnapshot> selectedSnapshots = ResolveSelectedPersistedSnapshots();
        IReadOnlyList<ObjectSnapshot> assignedSnapshots = _sceneView.GetPlacedObjectSnapshots()
            .Where(snapshot => string.Equals(snapshot.CollectionId, selectedCollection.Record.CollectionId, StringComparison.OrdinalIgnoreCase))
            .ToList();

        DrawObjectCollectionInspectorHero(selectedCollection, selectedSnapshots, assignedSnapshots.Count);

        var modsSectionHeight = ResolveRemainingRegionHeight(Scaled(160f), Scaled(4f));
        DrawObjectCollectionModsSection(selectedCollection, modsSectionHeight);
    }

    private void DrawObjectCollectionInspectorHero(
        ObjectCollectionSnapshot collection,
        IReadOnlyList<ObjectSnapshot> selectedSnapshots,
        int assignedObjectCount)
    {
        Vector4 accent = ResolveObjectCollectionAccentColor(collection.ResolveState);
        var actionButtonEdge = ResolveActionIconButtonEdge(FontAwesomeIcon.Redo, FontAwesomeIcon.Save, FontAwesomeIcon.Ban);
        var actionWidth = ResolveActionStripWidth(actionButtonEdge, 3);
        bool canApplyToSelected = HasObjectCollectionAssignmentChange(selectedSnapshots, collection.Record.CollectionId);
        bool canClearSelected = HasAnyObjectCollectionAssignment(selectedSnapshots);

        DrawPanelCard(
            "objectCollectionsInspectorHero",
            EditorColors.ButtonDefault with { W = 0.30f },
            accent with { W = 0.24f },
            Scaled(8f),
            ResolveObjectListCardPadding(),
            () =>
            {
                DrawObjectCollectionInspectorSummary(
                    collection,
                    assignedObjectCount,
                    accent,
                    actionWidth,
                    () =>
                    {
                        if (DrawAccentIconButton("objectCollectionRecompile", FontAwesomeIcon.Redo, "Reresolve this collection", EditorColors.AccentBlue, actionButtonEdge))
                        {
                            RecompileObjectCollection(collection);
                        }

                        ImGui.SameLine();
                        using (ImRaii.Disabled(!canApplyToSelected))
                        {
                            if (DrawAccentIconButton("objectCollectionApplySelected", FontAwesomeIcon.Save, "Apply this collection to selected objects", EditorColors.AccentGreen, actionButtonEdge))
                            {
                                ApplyObjectCollectionToSelectedObjects(collection.Record.CollectionId, selectedSnapshots);
                            }
                        }

                        ImGui.SameLine();
                        using (ImRaii.Disabled(!canClearSelected))
                        {
                            if (DrawAccentIconButton("objectCollectionClearSelected", FontAwesomeIcon.Ban, "Clear the selected objects collection assignment", EditorColors.AccentYellow, actionButtonEdge))
                            {
                                ApplyObjectCollectionToSelectedObjects(string.Empty, selectedSnapshots);
                            }
                        }
                    });
            });
    }

    private void DrawObjectCollectionInspectorSummary(
        ObjectCollectionSnapshot collection,
        int assignedObjectCount,
        Vector4 accent,
        float actionWidth,
        Action drawActions)
    {
        using var table = ImRaii.Table(
            "##objectCollectionsInspectorSummary",
            3,
            ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.NoPadOuterX | ImGuiTableFlags.NoSavedSettings);
        if (!table)
        {
            return;
        }

        ImGui.TableSetupColumn("Collection", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Gap", ImGuiTableColumnFlags.WidthFixed, Scaled(10f));
        ImGui.TableSetupColumn("Actions", ImGuiTableColumnFlags.WidthFixed, actionWidth);

        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        DrawObjectCollectionInspectorSummaryContent(collection, assignedObjectCount, accent);

        ImGui.TableNextColumn();
        ImGui.TableNextColumn();
        drawActions();
    }

    private void DrawObjectCollectionInspectorSummaryContent(
        ObjectCollectionSnapshot collection,
        int assignedObjectCount,
        Vector4 accent)
    {
        using (ImRaii.PushFont(UiBuilder.IconFont))
        using (ImRaii.PushColor(ImGuiCol.Text, accent))
        {
            ImGui.TextUnformatted(FontAwesomeIcon.Cubes.ToIconString());
        }

        ImGui.SameLine(0f, Scaled(8f));
        using (ImRaii.Group())
        {
            DrawObjectCollectionInspectorName(collection, accent);
            DrawObjectCollectionInspectorStatusLine(collection, assignedObjectCount, accent);
            DrawObjectCollectionInspectorIdLine(collection.Record.CollectionId);
        }
    }

    private void DrawObjectCollectionInspectorName(ObjectCollectionSnapshot collection, Vector4 accent)
    {
        if (string.Equals(_editingObjectCollectionNameId, collection.Record.CollectionId, StringComparison.OrdinalIgnoreCase))
        {
            DrawObjectCollectionInspectorNameField(collection);
            return;
        }

        DrawObjectCollectionInspectorNameLabel(collection, accent);
    }

    private void DrawObjectCollectionInspectorNameField(ObjectCollectionSnapshot collection)
    {
        ImGui.SetNextItemWidth(-1f);
        if (_focusObjectCollectionNameEdit)
        {
            ImGui.SetKeyboardFocusHere();
            _focusObjectCollectionNameEdit = false;
        }

        bool nameSubmitted = ImGui.InputTextWithHint(
            "##objectCollectionNameDraft",
            "collection name",
            ref _objectCollectionNameDraft,
            ObjectCollectionNameMaxLength,
            ImGuiInputTextFlags.EnterReturnsTrue);
        if (nameSubmitted || ImGui.IsItemDeactivated())
        {
            CommitObjectCollectionName(collection);
            _editingObjectCollectionNameId = string.Empty;
        }
    }

    private void DrawObjectCollectionInspectorNameLabel(ObjectCollectionSnapshot collection, Vector4 accent)
    {
        string name = string.IsNullOrWhiteSpace(collection.Record.Name)
            ? "Unnamed collection"
            : collection.Record.Name;
        string editIcon = FontAwesomeIcon.Edit.ToIconString();
        Vector2 editIconSize;
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            editIconSize = ImGui.CalcTextSize(editIcon);
        }

        var gap = Scaled(6f);
        var availableWidth = Positive(ImGui.GetContentRegionAvail().X);
        var nameWidth = MathF.Max(ResolveMinimumCardTextWidth(), availableWidth - editIconSize.X - gap);
        string clippedName = ClipTextToWidth(name, nameWidth);

        using (ImRaii.Group())
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(clippedName);
            ImGui.SameLine(0f, gap);
            using (ImRaii.PushFont(UiBuilder.IconFont))
            using (ImRaii.PushColor(ImGuiCol.Text, accent with { W = 0.78f }))
            {
                ImGui.TextUnformatted(editIcon);
            }
        }

        if (!ImGui.IsItemHovered())
        {
            return;
        }

        ImGui.SetTooltip("Right click to edit collection name");
        if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
        {
            BeginObjectCollectionNameEdit(collection);
        }
    }

    private void BeginObjectCollectionNameEdit(ObjectCollectionSnapshot collection)
    {
        _editingObjectCollectionNameId = collection.Record.CollectionId;
        _focusObjectCollectionNameEdit = true;
        LoadObjectCollectionNameDraft(collection);
    }

    private static void DrawObjectCollectionInspectorStatusLine(
        ObjectCollectionSnapshot collection,
        int assignedObjectCount,
        Vector4 accent)
    {
        ObjectCollectionHeaderStatus? headerStatus = BuildObjectCollectionHeaderStatus(collection, assignedObjectCount);
        string badgeText = headerStatus?.IssueText ?? BuildObjectCollectionHeaderBadgeText(collection, assignedObjectCount);
        bool noteHovered = DrawObjectCollectionHeaderNote(
            "objectCollectionsInspectorHeroNote",
            badgeText,
            accent);
        if (noteHovered && headerStatus is not null)
        {
            DrawObjectCollectionStatusTooltip(headerStatus, accent);
        }

        ImGui.SameLine(0f, Scaled(7f));
        ImGui.AlignTextToFramePadding();

        string statusText = BuildObjectCollectionInspectorStatus(collection, assignedObjectCount);
        var availableWidth = Positive(ImGui.GetContentRegionAvail().X);
        string clippedStatusText = ClipTextToWidth(statusText, availableWidth);
        ImGui.TextDisabled(clippedStatusText);
        if (!string.Equals(clippedStatusText, statusText, StringComparison.Ordinal) && ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(statusText);
        }
    }

    private static void DrawObjectCollectionInspectorIdLine(string collectionId)
    {
        ImGui.TextDisabled("id");
        ImGui.SameLine(0f, Scaled(5f));

        var availableWidth = Positive(ImGui.GetContentRegionAvail().X);
        string clippedId = ClipTextToWidth(collectionId, availableWidth);
        ImGui.TextDisabled(clippedId);
        if (!string.Equals(clippedId, collectionId, StringComparison.Ordinal) && ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(collectionId);
        }
    }
}

