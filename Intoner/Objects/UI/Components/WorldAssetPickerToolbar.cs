using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Intoner.Scene;
using System.Numerics;

namespace Intoner.Objects.UI.Components;

internal sealed class WorldAssetPickerToolbar(
    EditorWorldPicker picker,
    ObjectPreviewRenderer preview,
    WorldAssetActions assetActions)
{
    private string? _labelPath;
    private string _label = string.Empty;

    public static float Height => 40f * ImGuiHelpers.GlobalScale;

    private string GetLabel(WorldAssetPickState state)
    {
        string? path = state.IsPicking && state.Hover.Status == WorldAssetPickStatus.Hit ? state.Hover.Asset!.Path : state.SelectedPath;

        if (!string.Equals(path, _labelPath, StringComparison.Ordinal))
        {
            _labelPath = path;
            _label = path is null ? "No asset selected" : Path.GetFileNameWithoutExtension(path);
        }

        if (!state.IsPicking)
        {
            return path is null ? "No asset selected" : _label;
        }

        return state.Hover.Status switch
        {
            WorldAssetPickStatus.Hit => _label,
            WorldAssetPickStatus.Loading => "Loading...",
            WorldAssetPickStatus.Unavailable => "Unavailable",
            _ => "Picking",
        };
    }

    internal bool Draw(bool workspaceAvailable, ref bool expanded)
    {
        float scale = ImGuiHelpers.GlobalScale;
        float edge = 28f * scale;
        float gap = 5f * scale;
        using var spacing = ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, new Vector2(gap));
        ImGui.InvisibleButton("drag-picker", new Vector2(15f * scale, edge));
        ImDrawListPtr drawList = ImGui.GetWindowDrawList();
        Vector2 gripMin = ImGui.GetItemRectMin() + new Vector2(1f, 2f) * scale;
        float dotSize = 3f * scale;
        float rowStep = (edge - 7f * scale) / 4f;
        uint dotColor = ImGui.GetColorU32(ThemeColors.TextDisabled);
        for (int row = 0; row < 5; ++row)
        {
            for (int column = 0; column < 3; ++column)
            {
                Vector2 dotMin = gripMin + new Vector2(column * 5f * scale, row * rowStep);
                drawList.AddRectFilled(dotMin, dotMin + new Vector2(dotSize), dotColor, 0.5f * scale);
            }
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeAll);
        }

        if (ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left))
        {
            ImGuiViewportPtr viewport = ImGui.GetMainViewport();
            Vector2 max = Vector2.Max(viewport.Pos, viewport.Pos + viewport.Size - ImGui.GetWindowSize());
            ImGui.SetWindowPos(Vector2.Clamp(ImGui.GetWindowPos() + ImGui.GetIO().MouseDelta, viewport.Pos, max));
        }

        ImGui.SameLine();

        if (EditorIconButton.DrawToggle("expand", expanded ? FontAwesomeIcon.ChevronUp : FontAwesomeIcon.ChevronDown,
                new Vector2(20f * scale, edge), expanded, ThemeColors.AccentPrimary, ThemeColors.Text,
                expanded ? "Collapse assets" : "Nearby assets and recent picks", style: EditorToggleIconButtonStyle.Borderless))
        {
            expanded = !expanded;
        }

        ImGui.SameLine();
        bool picking = picker.State.IsPicking;

        if (EditorIconButton.DrawToggle("pick", FontAwesomeIcon.EyeDropper, new Vector2(edge), picking,
                ThemeColors.AccentPrimary, ThemeColors.Text, picking ? "Cancel Picking" : "Pick from World"))
        {
            if (picking) picker.Cancel();
            else picker.Begin();
        }

        WorldAssetPickState state = picker.State;
        string? path = state.IsPicking ? state.Hover.Asset?.Path : state.SelectedPath;
        string label = GetLabel(state);

        ImGui.SameLine();
        float width = MathF.Max(1f, ImGui.GetContentRegionAvail().X - (edge + gap) * 4f);
        Vector2 min = ImGui.GetCursorScreenPos();
        bool copy = ImGui.InvisibleButton("picked-asset", new Vector2(width, edge));
        ImGui.GetWindowDrawList().AddText(min + new Vector2(3f * scale, (edge - ImGui.GetTextLineHeight()) * 0.5f),
            ImGui.GetColorU32(ThemeColors.Text), EditorTextUtility.ClipTextToWidth(label, width - 6f * scale));
        bool hovered = ImGui.IsItemHovered();

        if (hovered && !state.IsPicking && path is not null)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);

            if (copy)
            {
                ImGui.SetClipboardText(path);
            }
        }

        ImGui.SameLine();

        bool opened = assetActions.Draw(edge, workspaceAvailable);

        ImGui.SameLine();

        if (EditorIconButton.DrawToggle("close", FontAwesomeIcon.Times, new Vector2(edge), false,
                ThemeColors.DimRed, ThemeColors.DimRed, "Close"))
        {
            picker.Close();
        }

        if (!hovered)
        {
            return opened;
        }

        if (!state.IsPicking && path is not null)
        {
            IntonerTooltip.Draw(() =>
            {
                preview.DrawAssetTooltip(state.SelectedAsset!);
                IntonerTooltipContent.Separator();
                IntonerTooltipContent.KeyHint("Click", "LMB", "to copy the asset path");
            }, new IntonerTooltipOptions { Width = 300f });
        }
        else if (state.Hover.Status == WorldAssetPickStatus.Unavailable)
        {
            string reason = state.Hover.Asset?.UnavailableReason switch
            {
                WorldAssetUnavailableReason.Animated => "Animated models cannot be picked.",
                WorldAssetUnavailableReason.UnresolvedResource => "This resource does not identify a reusable game asset.",
                _ => "Model geometry is unavailable.",
            };
            IntonerTooltip.Draw(() =>
            {
                IntonerTooltipContent.Header(FontAwesomeIcon.ExclamationTriangle, "Unavailable");
                ImGui.TextWrapped(reason);
            }, new IntonerTooltipOptions { Width = 260f });
        }

        return opened;
    }
}
