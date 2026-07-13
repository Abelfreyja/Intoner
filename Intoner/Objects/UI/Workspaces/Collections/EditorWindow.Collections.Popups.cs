using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Intoner.Objects.Collections;
using Intoner.Objects.Utils;
using Intoner.UI;
using System.Numerics;

namespace Intoner.Objects.UI;

internal sealed partial class EditorWindow
{
    private void DrawObjectCollectionAddModPopup(IReadOnlyList<ObjectCollectionSnapshot> collections)
    {
        if (_openObjectCollectionAddModPopupNextFrame)
        {
            ImGui.OpenPopup(ObjectCollectionAddModPopupId);
            _openObjectCollectionAddModPopupNextFrame = false;
        }

        if (_objectCollectionAddModPopupCollectionId.Length == 0
         || !TryResolveObjectCollectionById(collections, _objectCollectionAddModPopupCollectionId, out ObjectCollectionSnapshot collection))
        {
            return;
        }

        var accent = EditorColors.AccentPurple;
        var popupMargin = Scaled(8f);
        var popupSize = ScaledVector(760f, 440f);
        var popupPos = ResolveObjectCollectionPopupPosition(
            _objectCollectionAddModPopupAnchorMin,
            _objectCollectionAddModPopupAnchorMax,
            popupSize,
            popupMargin);

        ImGui.SetNextWindowPos(popupPos, ImGuiCond.Appearing);
        ImGui.SetNextWindowSize(popupSize, ImGuiCond.Appearing);
        ImGui.SetNextWindowSizeConstraints(popupSize, popupSize);

        using var windowPadding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, ScaledVector(10f, 10f));
        using var windowRounding = ImRaii.PushStyle(ImGuiStyleVar.WindowRounding, Scaled(12f));
        using var popupBg = ImRaii.PushColor(ImGuiCol.PopupBg, EditorColors.WithAlpha(_windowBodyBackgroundColor, 0.98f));
        using var popupBorder = ImRaii.PushColor(ImGuiCol.Border, EditorColors.WithAlpha(accent, 0.42f));

        using var popup = ImRaii.Popup(ObjectCollectionAddModPopupId, ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        if (!popup)
        {
            return;
        }

        ImGui.TextColored(accent, "Add Penumbra Mods");
        DrawObjectCollectionMutedText("Browse installed Penumbra mods and add them to this collection.");

        ImGui.SetNextItemWidth(-1f);
        ImGui.InputTextWithHint(
            "##objectCollectionModFilter",
            "filter installed mods",
            ref _objectCollectionModFilter,
            ObjectCollectionModFilterMaxLength);

        IReadOnlyList<ObjectAvailableMod> installedMods = _objectModDataSource.GetInstalledMods();
        if (installedMods.Count == 0)
        {
            DrawObjectCollectionMutedText("No installed Penumbra mods are currently available.", topSpacing: 6f);
            return;
        }

        string filter = _objectCollectionModFilter.Trim();
        var filteredMods = installedMods
            .Where(mod => filter.Length == 0
                || mod.ModName.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || mod.ModDirectory.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (filteredMods.Count == 0)
        {
            DrawObjectCollectionMutedText("No installed mods match the current filter.", topSpacing: 6f);
            return;
        }

        var actionWidth = Scaled(88f);
        var tableHeight = Positive(ImGui.GetContentRegionAvail().Y - Scaled(2f));
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
        for (var index = 0; index < filteredMods.Count; ++index)
        {
            ObjectAvailableMod mod = filteredMods[index];
            string modDirectory = ObjectCollectionKeyUtility.NormalizeModDirectory(mod.ModDirectory);
            bool alreadyAssigned = modDirectory.Length > 0 && assignedModDirectories.Contains(modDirectory);
            bool canAdd = modDirectory.Length > 0 && !alreadyAssigned;
            string actionLabel = modDirectory.Length == 0
                ? "Invalid"
                : alreadyAssigned
                    ? "Assigned"
                    : "Add";

            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(ClipTextToWidth(mod.ModName, Positive(ImGui.GetContentRegionAvail().X - Scaled(8f))));
            if (ImGui.IsItemHovered() && !string.IsNullOrWhiteSpace(mod.ModDirectory))
            {
                UiSharedService.AttachToolTip(mod.ModDirectory);
            }

            ImGui.TableNextColumn();
            using (ImRaii.Disabled(!canAdd))
            {
                if (ImGui.Button($"{actionLabel}##objectCollectionAddMod:{index}:{mod.ModDirectory}", new Vector2(-1f, 0f))
                 && canAdd
                 && AddObjectCollectionEntry(collection, mod))
                {
                    ImGui.CloseCurrentPopup();
                    return;
                }
            }
        }
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

        using (ImRaii.PushColor(ImGuiCol.Text, EditorColors.TextDisabled))
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

    private void QueueCreateObjectCollectionPopup()
    {
        _objectCollectionCreateInput = string.Empty;
        _openObjectCollectionCreatePopupNextFrame = true;
    }

    private void DrawObjectCollectionCreatePopup()
    {
        if (_openObjectCollectionCreatePopupNextFrame)
        {
            ImGui.OpenPopup(ObjectCollectionCreatePopupId);
            _openObjectCollectionCreatePopupNextFrame = false;
        }

        using var popup = ImRaii.Popup(ObjectCollectionCreatePopupId, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        if (!popup)
        {
            return;
        }

        ImGui.TextUnformatted("Create Collection");
        ImGui.Separator();

        if (ImGui.IsWindowAppearing())
        {
            ImGui.SetKeyboardFocusHere();
        }

        ImGui.SetNextItemWidth(Positive(Scaled(240f)));
        bool submitted = ImGui.InputTextWithHint(
            "##objectCollectionCreateInput",
            "collection name",
            ref _objectCollectionCreateInput,
            ObjectCollectionNameMaxLength,
            ImGuiInputTextFlags.EnterReturnsTrue);

        bool canCreate = !string.IsNullOrWhiteSpace(ObjectStringUtility.TrimOrEmpty(_objectCollectionCreateInput));
        using (ImRaii.Disabled(!canCreate))
        {
            if ((submitted || ImGui.Button("Create Collection"))
                && canCreate
                && CreateObjectCollection(_objectCollectionCreateInput))
            {
                ImGui.CloseCurrentPopup();
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel"))
        {
            ImGui.CloseCurrentPopup();
        }
    }
}

