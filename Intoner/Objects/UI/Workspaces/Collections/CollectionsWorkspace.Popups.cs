using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Utility;
using Dalamud.Interface;
using Intoner.Objects.Collections;
using Intoner.Objects.UI.Components;
using Intoner.Objects.UI.Dependencies;
using Intoner.Objects.Utils;
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
        var popupSize = EditorLayout.ScaledVector(760f, 440f);
        var popupPos = ResolveObjectCollectionPopupPosition(
            _objectCollectionAddModPopupAnchorMin,
            _objectCollectionAddModPopupAnchorMax,
            popupSize,
            popupMargin);

        ImGui.SetNextWindowPos(popupPos, ImGuiCond.Appearing);
        ImGui.SetNextWindowSize(popupSize, ImGuiCond.Appearing);
        ImGui.SetNextWindowSizeConstraints(popupSize, popupSize);

        using var windowPadding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, EditorLayout.ScaledVector(10f, 10f));
        using var windowRounding = ImRaii.PushStyle(ImGuiStyleVar.WindowRounding, EditorLayout.Scaled(12f));
        using var popupBg = ImRaii.PushColor(ImGuiCol.PopupBg, ThemeColors.WithAlpha(_editorOverlayLayer.BackgroundColor, 0.98f));
        using var popupBorder = ImRaii.PushColor(ImGuiCol.Border, ThemeColors.WithAlpha(accent, 0.42f));

        using var popup = ImRaii.Popup(ObjectCollectionAddModPopupId, ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        if (!popup)
        {
            return;
        }

        ImGui.TextColored(accent, "Add Penumbra Mods");
        DrawObjectCollectionMutedText("Browse installed Penumbra mods and add them to this collection.");

        DependencyStatus penumbraStatus = _dependencies.GetStatus(IPenumbraDependency.Id);
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
        if (installedMods.Count == 0)
        {
            DrawObjectCollectionMutedText("No installed Penumbra mods are currently available.", topSpacing: 6f);
            return;
        }

        IReadOnlyList<ObjectAvailableMod> filteredMods = GetFilteredObjectCollectionMods(installedMods);
        if (filteredMods.Count == 0)
        {
            DrawObjectCollectionMutedText("No installed mods match the current filter.", topSpacing: 6f);
            return;
        }

        var actionWidth = EditorLayout.Scaled(88f);
        var tableHeight = EditorLayout.Positive(ImGui.GetContentRegionAvail().Y - EditorLayout.Scaled(2f));
        using var table = ImRaii.Table(
            "##objectCollectionAddModTable",
            2,
            ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersOuter | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.ScrollY,
            new Vector2(-1f, tableHeight));
        if (!table)
        {
            return;
        }

        ImGui.TableSetupColumn("Mod", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Action", ImGuiTableColumnFlags.WidthFixed, actionWidth);
        ImGui.TableHeadersRow();

        HashSet<string> assignedModDirectories = collection.Record.Entries
            .Select(static entry => ObjectCollectionKeyUtility.NormalizeModDirectory(entry.ModDirectory))
            .Where(static modDirectory => modDirectory.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        float rowHeight = ImGui.GetFrameHeight() + (ImGui.GetStyle().CellPadding.Y * 2f);
        using UiVirtualList.Scope list = UiVirtualList.Begin(filteredMods.Count, UiVirtualListOptions.Rows(rowHeight));
        while (list.Step())
        {
            for (int index = list.DisplayStart; index < list.DisplayEnd; ++index)
            {
                ObjectAvailableMod mod = filteredMods[index];
                string modDirectory = ObjectCollectionKeyUtility.NormalizeModDirectory(mod.ModDirectory);
                bool alreadyAssigned = modDirectory.Length > 0 && assignedModDirectories.Contains(modDirectory);
                bool canAdd = modDirectory.Length > 0 && !alreadyAssigned;
                string actionLabel = (canAdd, alreadyAssigned) switch
                {
                    (true, _) => "Add",
                    (_, true) => "Assigned",
                    _         => "Invalid",
                };

                ImGui.TableNextRow(ImGuiTableRowFlags.None, rowHeight);

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(EditorTextUtility.ClipTextToWidth(mod.ModName, EditorLayout.Positive(ImGui.GetContentRegionAvail().X - EditorLayout.Scaled(8f))));
                if (ImGui.IsItemHovered() && !string.IsNullOrWhiteSpace(mod.ModDirectory))
                {
                    IntonerTooltip.Attach(mod.ModDirectory);
                }

                ImGui.TableNextColumn();
                using (ImRaii.Disabled(!canAdd))
                using (ImRaii.PushId(modDirectory))
                {
                    if (ImGui.Button($"{actionLabel}##objectCollectionAddMod", new Vector2(-1f, 0f))
                     && canAdd
                     && AddObjectCollectionEntry(collection, mod))
                    {
                        ImGui.CloseCurrentPopup();
                        return;
                    }
                }
            }
        }
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

    private static void DrawObjectCollectionMutedText(string text, float topSpacing = 0f)
    {
        if (topSpacing > 0f)
        {
            ImGuiHelpers.ScaledDummy(topSpacing);
        }

        using (ImRaii.PushColor(ImGuiCol.Text, ThemeColors.TextDisabled))
        using (ImRaiiScope.TextWrapPos())
        {
            ImGui.TextWrapped(text);
        }
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
