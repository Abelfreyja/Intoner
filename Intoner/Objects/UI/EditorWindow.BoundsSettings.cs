using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Intoner.Objects.Models;
using Intoner.UI;
using System.Numerics;

namespace Intoner.Objects.UI;

internal sealed partial class EditorWindow
{
    private const string BoundsOptionsPopupId = "##objectBoundsOptionsPopup";

    private ObjectBoundsInteractionSettings BoundsInteractionSettings
    {
        get => _gizmo.Settings.BoundsInteractionSettings;
        set => _gizmo.Settings.BoundsInteractionSettings = value;
    }

    private void DrawBoundsToolbarButton(ToolbarSurfaceMode mode)
    {
        var accent = BoundsInteractionSettings.BoundsEnabled
            ? EditorColors.BoundsOverlayAccent
            : BoundsInteractionSettings.SelectionEnabled
                ? EditorColors.AccentBlue
                : (Vector4?)null;

        DrawHeaderActionButton(
            "##objectBoundsOverlay",
            FontAwesomeIcon.BorderAll,
            "Target",
            string.Empty,
            IsBoundsOptionsPopupOpen(),
            ToggleBoundsOverlayEnabled,
            accent,
            useAccentFill: false,
            useNeutralHoverFill: true,
            hoverBorderColor: EditorColors.BoundsOverlayAccent,
            drawBackground: DrawBoundsToolbarButtonBackground,
            drawTooltip: DrawBoundsToolbarTooltip,
            mode: mode);

        if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
        {
            ImGui.OpenPopup(BoundsOptionsPopupId);
        }

        var anchorMin = ImGui.GetItemRectMin();
        var anchorMax = ImGui.GetItemRectMax();
        DrawBoundsOptionsPopup(anchorMin, anchorMax);
    }

    private void DrawBoundsToolbarButtonBackground(ImDrawListPtr drawList, Vector2 min, Vector2 max, bool hovered, bool active)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var inset = 3f * scale;
        var innerMin = min + new Vector2(inset, inset);
        var innerMax = max - new Vector2(inset, inset);
        if (innerMax.X <= innerMin.X || innerMax.Y <= innerMin.Y)
        {
            return;
        }

        var bandGap = MathF.Max(1f * scale, 1f);
        var availableWidth = innerMax.X - innerMin.X;
        var bandWidth = (availableWidth - bandGap) * 0.5f;
        if (bandWidth <= 0f)
        {
            return;
        }

        var alpha = active
            ? 0.46f
            : hovered
                ? 0.40f
                : 0.32f;

        DrawTransformSnapToolbarBand(
            drawList,
            new Vector2(innerMin.X, innerMin.Y),
            new Vector2(innerMin.X + bandWidth, innerMax.Y),
            EditorColors.AccentBlue,
            BoundsInteractionSettings.SelectionEnabled,
            alpha);

        var rightMinX = innerMin.X + bandWidth + bandGap;
        DrawTransformSnapToolbarBand(
            drawList,
            new Vector2(rightMinX, innerMin.Y),
            new Vector2(innerMax.X, innerMax.Y),
            EditorColors.BoundsOverlayAccent,
            BoundsInteractionSettings.BoundsEnabled,
            alpha);
    }

    private void DrawBoundsToolbarTooltip()
        => DrawToolbarTooltip(EditorColors.BoundsOverlayAccent, () =>
        {
            ImGui.TextColored(EditorColors.BoundsOverlayAccent, "Target and Bounds");
            ImGui.TextDisabled("Left click to toggle bounds.");
            ImGui.TextDisabled("Right click to open target options.");
            ImGui.Spacing();
            ImGui.TextUnformatted($"Selection: {FormatOnOff(BoundsInteractionSettings.SelectionEnabled)}");
            ImGui.TextUnformatted($"Bounds: {FormatOnOff(BoundsInteractionSettings.BoundsEnabled)}");
            ImGui.TextUnformatted($"Filter: {BuildBoundsFilterSummary(BoundsInteractionSettings.BoundsFilter)}");
            ImGui.TextUnformatted($"Selected Only: {FormatOnOff(BoundsInteractionSettings.ShowSelectedOnly)}");
        });

    private void DrawBoundsOptionsPopup(Vector2 anchorMin, Vector2 anchorMax)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var accent = EditorColors.BoundsOverlayAccent;
        var popupWidth = 320f * scale;
        ImGui.SetNextWindowPos(new Vector2(anchorMin.X, anchorMax.Y + (8f * scale)), ImGuiCond.Appearing);
        ImGui.SetNextWindowSize(new Vector2(popupWidth, 0f), ImGuiCond.Appearing);
        ImGui.SetNextWindowSizeConstraints(new Vector2(popupWidth, 0f), new Vector2(popupWidth, float.MaxValue));

        using var windowPadding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, new Vector2(10f * scale, 10f * scale));
        using var windowRounding = ImRaii.PushStyle(ImGuiStyleVar.WindowRounding, 12f * scale);
        using var popupBg = ImRaii.PushColor(ImGuiCol.PopupBg, EditorColors.WithAlpha(_windowBodyBackgroundColor, 0.98f));
        using var popupBorder = ImRaii.PushColor(ImGuiCol.Border, EditorColors.WithAlpha(accent, 0.42f));

        using var popup = ImRaii.Popup(BoundsOptionsPopupId, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        if (!popup)
        {
            return;
        }

        ImGui.TextColored(accent, "Target and Bounds");
        ImGui.TextDisabled("Settings to change selection and bounds behavior.");
        ImGuiHelpers.ScaledDummy(4f);

        using (var settingsTable = CompactSettingsTable("##boundsOptionsSettings"))
        {
            if (settingsTable)
            {
                var selectionEnabled = BoundsInteractionSettings.SelectionEnabled;
                if (DrawCheckboxRow("boundsSelectionEnabled", "Selection Enabled", ref selectionEnabled))
                {
                    BoundsInteractionSettings = BoundsInteractionSettings with { SelectionEnabled = selectionEnabled };
                }

                var boundsEnabled = BoundsInteractionSettings.BoundsEnabled;
                if (DrawCheckboxRow("boundsOverlayEnabled", "Bounds Enabled", ref boundsEnabled))
                {
                    BoundsInteractionSettings = BoundsInteractionSettings with { BoundsEnabled = boundsEnabled };
                }

                var showSelectedOnly = BoundsInteractionSettings.ShowSelectedOnly;
                if (DrawCheckboxRow("boundsSelectedOnly", "Show Selected Only", ref showSelectedOnly))
                {
                    BoundsInteractionSettings = BoundsInteractionSettings with { ShowSelectedOnly = showSelectedOnly };
                }
            }
        }

        ImGuiHelpers.ScaledDummy(6f);
        ImGui.TextDisabled("Bounds Filter");
        ImGuiHelpers.ScaledDummy(2f);

        DrawBoundsFilterCheckbox("boundsFilterFurniture", "Furniture", ObjectKind.Furniture);
        DrawBoundsFilterCheckbox("boundsFilterBgObject", "BgObject", ObjectKind.BgObject);
        DrawBoundsFilterCheckbox("boundsFilterVfx", "VFX", ObjectKind.Vfx);
        DrawBoundsFilterCheckbox("boundsFilterLight", "Light", ObjectKind.Light);
    }

    private void DrawBoundsFilterCheckbox(string id, string label, ObjectKind kind)
    {
        var enabled = IsBoundsKindEnabled(kind);
        if (!ImGui.Checkbox($"{label}##{id}", ref enabled))
        {
            return;
        }

        BoundsInteractionSettings = BoundsInteractionSettings with
        {
            BoundsFilter = enabled
                ? BoundsInteractionSettings.BoundsFilter | kind
                : BoundsInteractionSettings.BoundsFilter & ~kind,
        };
    }

    private static string BuildBoundsFilterSummary(ObjectKind filter)
    {
        List<string> enabledKinds = [];
        if ((filter & ObjectKind.Furniture) != 0)
        {
            enabledKinds.Add("Furniture");
        }

        if ((filter & ObjectKind.BgObject) != 0)
        {
            enabledKinds.Add("BgObject");
        }

        if ((filter & ObjectKind.Vfx) != 0)
        {
            enabledKinds.Add("VFX");
        }

        if ((filter & ObjectKind.Light) != 0)
        {
            enabledKinds.Add("Light");
        }

        return enabledKinds.Count switch
        {
            0 => "None",
            var count when count == Enum.GetValues<ObjectKind>().Length => "All",
            _ => string.Join(", ", enabledKinds),
        };
    }

    private static bool IsBoundsOptionsPopupOpen()
        => ImGui.IsPopupOpen(BoundsOptionsPopupId);

    private bool IsBoundsKindEnabled(ObjectKind kind)
        => (BoundsInteractionSettings.BoundsFilter & kind) != 0;

    private bool ShouldDrawBoundsSnapshot(ObjectBoundsSnapshot snapshot)
    {
        if (!IsBoundsKindEnabled(snapshot.Kind))
        {
            return false;
        }

        return !BoundsInteractionSettings.ShowSelectedOnly || _editorSelection.Contains(snapshot.Id);
    }

    private void ToggleBoundsOverlayEnabled()
        => BoundsInteractionSettings = BoundsInteractionSettings with
        {
            BoundsEnabled = !BoundsInteractionSettings.BoundsEnabled,
        };
}

