using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Utility;
using Dalamud.Interface;
using Intoner.Objects.Collections;
using Intoner.Objects.UI.Components;
using Intoner.Objects.UI.Dependencies;
using Intoner.Objects.Utils;
using Intoner.Services.Configuration;
using Intoner.Services.Dependencies;
using Intoner.UI.Performance;
using Intoner.UI;
using System.Numerics;

namespace Intoner.Objects.UI;

internal sealed partial class CollectionsWorkspace
{
    private IReadOnlyList<ObjectAvailableMod>? _objectCollectionModFilterSource;
    private string _objectCollectionAppliedModFilter = string.Empty;
    private IReadOnlyList<ObjectAvailableMod> _objectCollectionFilteredMods = [];

    private void DrawObjectCollectionAddModPopup(IReadOnlyList<ObjectCollectionSnapshot> collections)
    {
        if (_openObjectCollectionAddModPopupNextFrame)
        {
            ImGui.OpenPopup(ObjectCollectionAddModPopupId);
            _openObjectCollectionAddModPopupNextFrame = false;
        }

        if (_objectCollectionAddModPopupCollectionId.Length == 0
         || !SceneItemPresentation.TryResolveObjectCollectionById(collections, _objectCollectionAddModPopupCollectionId, out ObjectCollectionSnapshot collection))
        {
            return;
        }

        var accent = ThemeColors.AccentPrimary;
        var popupMargin = EditorLayout.Scaled(8f);
        var popupSize = Vector2.Min(EditorLayout.ScaledVector(620f, 440f),
            Vector2.Max(Vector2.One, ImGui.GetMainViewport().WorkSize - new Vector2(popupMargin * 2f)));
        var popupPos = ResolveObjectCollectionPopupPosition(
            _objectCollectionAddModPopupAnchorMin,
            _objectCollectionAddModPopupAnchorMax,
            popupSize,
            popupMargin);

        ImGui.SetNextWindowPos(popupPos, ImGuiCond.Appearing);
        ImGui.SetNextWindowSize(popupSize, ImGuiCond.Appearing);
        ImGui.SetNextWindowSizeConstraints(popupSize, popupSize);

        using var windowPadding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, EditorLayout.ScaledVector(10f, 10f));
        using var windowRounding = ImRaii.PushStyle(ImGuiStyleVar.WindowRounding, EditorLayout.Scaled(8f));
        using var popupBg = ImRaii.PushColor(ImGuiCol.PopupBg, ThemeColors.WithAlpha(_editorOverlayLayer.BackgroundColor, 0.98f));
        using var popupBorder = ImRaii.PushColor(ImGuiCol.Border, ThemeColors.WithAlpha(accent, 0.42f));

        using var popup = ImRaii.Popup(ObjectCollectionAddModPopupId, ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        if (!popup)
        {
            return;
        }

        ImGui.TextColored(accent, "Add Penumbra Mods");
        ImGuiHelpers.ScaledDummy(4f);

        DependencyStatus penumbraStatus = _penumbra.Status;
        if (!penumbraStatus.IsAvailable)
        {
            ImGuiHelpers.ScaledDummy(6f);
            DependencyUi.DrawInlineStatus(penumbraStatus);
            return;
        }

        _ = EditorSearchField.Draw(
            "objectCollectionModFilter",
            ref _objectCollectionModFilter,
            new EditorSearchFieldOptions(
                "Filter installed mods",
                accent,
                MaxLength: CollectionPanel.ObjectCollectionFilterMaxLength));

        IReadOnlyList<ObjectAvailableMod> installedMods = _objectModDataSource.GetInstalledMods();
        IReadOnlyList<ObjectAvailableMod> previousMods = _objectCollectionFilteredMods;
        IReadOnlyList<ObjectAvailableMod> filteredMods = GetFilteredObjectCollectionMods(installedMods);
        float listHeight = EditorLayout.Positive(ImGui.GetContentRegionAvail().Y - EditorLayout.Scaled(2f));
        using var childBackground = ImRaii.PushColor(ImGuiCol.ChildBg, Vector4.Zero);
        using var listPadding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        using var child = EditorScrollList.Begin(
            "##objectCollectionAddModList",
            new Vector2(0f, listHeight),
            _editorOverlayLayer.CreateScrollPanelOptions(
                ThemeColors.WithAlpha(_editorOverlayLayer.BackgroundColor, 0.98f), EditorLayout.Scaled(6f), accent));
        if (!child)
        {
            return;
        }

        if (ImGui.IsWindowAppearing() || !ReferenceEquals(previousMods, filteredMods))
        {
            ImGui.SetScrollY(0f);
        }

        if (filteredMods.Count == 0)
        {
            EditorEmptyState.Draw(installedMods.Count == 0
                ? "No installed Penumbra mods are currently available."
                : "No installed mods match the current filter.", listHeight);
            return;
        }

        HashSet<string> assignedModDirectories = collection.Record.Entries
            .Select(static entry => ObjectCollectionKeyUtility.NormalizeModDirectory(entry.ModDirectory))
            .Where(static modDirectory => modDirectory.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        SceneListRow.Metrics metrics = SceneListRow.ResolveMetrics(SceneListRowSize.Compact);
        using UiVirtualList.Scope list = UiVirtualList.Begin(filteredMods.Count,
            UiVirtualListOptions.Rows(metrics.ItemHeight, metrics.ItemSpacing));
        while (list.Step())
        {
            for (int index = list.DisplayStart; index < list.DisplayEnd; ++index)
            {
                ObjectAvailableMod mod = filteredMods[index];
                string modDirectory = ObjectCollectionKeyUtility.NormalizeModDirectory(mod.ModDirectory);
                bool alreadyAssigned = modDirectory.Length > 0 && assignedModDirectories.Contains(modDirectory);
                using var id = ImRaii.PushId(modDirectory.Length > 0 ? modDirectory : $"invalid:{index}");
                if (DrawObjectCollectionAvailableModRow(mod, modDirectory, alreadyAssigned, metrics.ItemHeight)
                 && AddObjectCollectionEntry(collection, mod))
                {
                    ImGui.CloseCurrentPopup();
                    return;
                }

                list.FinishItem(index);
            }
        }
    }

    private bool DrawObjectCollectionAvailableModRow(ObjectAvailableMod mod, string modDirectory, bool alreadyAssigned, float height)
    {
        bool canAdd = modDirectory.Length > 0 && !alreadyAssigned;
        (FontAwesomeIcon Icon, string Tooltip, Vector4 Accent) action = (canAdd, alreadyAssigned) switch
        {
            (true, _) => (FontAwesomeIcon.Plus, "Add mod to this collection", ThemeColors.AccentPrimary),
            (_, true) => (FontAwesomeIcon.Check, "Already assigned to this collection", ThemeColors.AccentGreen),
            _         => (FontAwesomeIcon.Ban, "Invalid mod directory", ThemeColors.DimRed),
        };
        float rowWidth = EditorLayout.Positive(ImGui.GetContentRegionAvail().X - EditorLayout.Scaled(6f));
        using var rowSpacing = ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, Vector2.Zero);
        EditorListCard.Interaction interaction = EditorListCard.DrawInteraction(
            "objectCollectionAvailableMod", false, new Vector2(rowWidth, height),
            ImGuiSelectableFlags.AllowItemOverlap | ImGuiSelectableFlags.DontClosePopups);
        DrawObjectCollectionModContextMenu("##objectCollectionAvailableModContext", mod.ModDirectory);
        Vector2 cursorAfterRow = ImGui.GetCursorPos();
        ImDrawListPtr drawList = ImGui.GetWindowDrawList();
        bool hovered = canAdd && interaction.Hovered;
        _listCard.DrawFrame(drawList, interaction.Min, interaction.Max,
            hovered ? CollectionPanel.CollectionModStyle.RowHover : CollectionPanel.CollectionModStyle.Row,
            Vector4.Zero, Vector4.Zero, EditorLayout.Scaled(4f), hovered, ImDrawFlags.RoundCornersAll);

        float padding = EditorLayout.Scaled(10f);
        float gap = EditorLayout.Scaled(8f);
        float buttonSize = EditorLayout.Scaled(CollectionPanel.CollectionModStyle.ActionSize);
        float centerY = (interaction.Min.Y + interaction.Max.Y) * 0.5f;
        Vector2 actionPos = new(interaction.Max.X - padding - buttonSize, centerY - buttonSize * 0.5f);
        float labelX = interaction.Min.X + padding;
        float labelWidth = EditorLayout.Positive(actionPos.X - gap - labelX);
        string modLabel = mod.ModName.Length > 0 ? mod.ModName : modDirectory;
        drawList.AddText(new Vector2(labelX, centerY - ImGui.GetTextLineHeight() * 0.5f),
            ImGui.GetColorU32(canAdd ? ThemeColors.Text : ThemeColors.TextDisabled), EditorTextUtility.ClipTextToWidth(modLabel, labelWidth));
        if (IntonerTooltip.IsAreaHovered(new Vector2(labelX, interaction.Min.Y), new Vector2(actionPos.X - gap, interaction.Max.Y)))
        {
            string tooltip = modDirectory.Length == 0 || string.Equals(modLabel.Trim(), modDirectory, StringComparison.OrdinalIgnoreCase)
                ? modLabel
                : $"{modLabel}\n{mod.ModDirectory}";
            IntonerTooltip.DrawText(tooltip, action.Accent, 35f);
        }

        ImGui.SetCursorScreenPos(actionPos);
        bool clicked;
        using (ImRaii.Disabled(!canAdd))
        {
            clicked = EditorIconButton.DrawAccent("objectCollectionAddMod", action.Icon, action.Tooltip, action.Accent,
                edgeOverride: buttonSize, style: EditorAccentIconButtonStyle.Subtle);
        }
        ImGui.SetCursorPos(cursorAfterRow);
        return canAdd && clicked;
    }

    private IReadOnlyList<ObjectAvailableMod> GetFilteredObjectCollectionMods(IReadOnlyList<ObjectAvailableMod> installedMods)
    {
        string filter = _objectCollectionModFilter.Trim();
        if (ReferenceEquals(_objectCollectionModFilterSource, installedMods)
         && string.Equals(_objectCollectionAppliedModFilter, filter, StringComparison.OrdinalIgnoreCase))
        {
            return _objectCollectionFilteredMods;
        }

        _objectCollectionModFilterSource = installedMods;
        _objectCollectionAppliedModFilter = filter;
        _objectCollectionFilteredMods = filter.Length == 0
            ? installedMods
            : installedMods.Where(mod => mod.ModName.Contains(filter, StringComparison.OrdinalIgnoreCase)
                                     || mod.ModDirectory.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
        return _objectCollectionFilteredMods;
    }

    private static Vector2 ResolveObjectCollectionPopupPosition(Vector2 anchorMin, Vector2 anchorMax, Vector2 popupSize, float popupMargin)
    {
        var viewport = ImGui.GetMainViewport();
        var workMin = viewport.WorkPos + new Vector2(popupMargin, popupMargin);
        var workMax = viewport.WorkPos + viewport.WorkSize - new Vector2(popupMargin, popupMargin);
        var popupPos = new Vector2(anchorMax.X - popupSize.X, anchorMax.Y + popupMargin);
        var maxPopupX = MathF.Max(workMin.X, workMax.X - popupSize.X);
        var maxPopupY = MathF.Max(workMin.Y, workMax.Y - popupSize.Y);
        popupPos.X = Math.Clamp(popupPos.X, workMin.X, maxPopupX);

        if (popupPos.Y > maxPopupY && anchorMin.Y - popupMargin - popupSize.Y >= workMin.Y)
        {
            popupPos.Y = anchorMin.Y - popupMargin - popupSize.Y;
            return popupPos;
        }

        popupPos.Y = Math.Clamp(popupPos.Y, workMin.Y, maxPopupY);
        return popupPos;
    }

    private void QueueObjectCollectionAddModPopup(string collectionId, Vector2 anchorMin, Vector2 anchorMax)
    {
        _objectCollectionModFilter = string.Empty;
        _objectCollectionAddModPopupCollectionId = collectionId;
        _objectCollectionAddModPopupAnchorMin = anchorMin;
        _objectCollectionAddModPopupAnchorMax = anchorMax;
        _openObjectCollectionAddModPopupNextFrame = true;
    }

    private void OpenCreateObjectCollectionDialog()
    {
        _interaction.OpenDialog(EditorDialog.Request.TextInput("collection-create", "Create Collection", "Create Collection", CreateObjectCollection) with
        {
            Icon = FontAwesomeIcon.Swatchbook,
            Accent = ThemeColors.AccentPrimary,
            Placeholder = "collection name",
            MaxLength = ObjectCollectionNameMaxLength,
            Validate = static input => TextUtility.TrimOrEmpty(input).Length == 0
                ? "Enter a collection name."
                : null,
        });
    }
}
