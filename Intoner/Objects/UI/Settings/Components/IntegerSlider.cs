using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Intoner.Objects.UI;
using System.Numerics;
using static Intoner.Objects.UI.Settings.Components.SettingsChrome;

namespace Intoner.Objects.UI.Settings.Components;

internal static class IntegerSlider
{
    public const float HitHeight = 18f;

    private const float TrackHeight = 7f;
    private const float TrackInset = 7f;
    private const float ThumbRadius = 6f;

    public static IntegerSliderUpdate Draw(string id, ref int value, IntegerSettingRange range, float width, Vector4 accent, bool enabled)
    {
        bool changed = false;
        Vector2 min = ImGui.GetCursorScreenPos();
        Vector2 size = new(width, Scaled(HitHeight));
        using (ImRaii.Disabled(!enabled))
        {
            ImGui.InvisibleButton(id, size);
        }

        bool hovered = enabled && ImGui.IsItemHovered();
        bool manualInputRequested = InputGesture.ManualInputRequested(hovered, enabled);
        bool active = enabled && !ImGui.GetIO().KeyCtrl && ImGui.IsItemActive();
        if (active)
        {
            int nextValue = ResolveSliderValue(ImGui.GetIO().MousePos.X, min.X, width, range);
            changed = nextValue != value;
            value = nextValue;
        }

        bool commit = enabled && !manualInputRequested && ImGui.IsItemDeactivated();
        DrawSliderRail(min, size, value, range, accent, enabled, hovered, active);
        value = range.Clamp(value);
        return new IntegerSliderUpdate(changed, commit, manualInputRequested);
    }

    private static void DrawSliderRail(
        Vector2 min,
        Vector2 size,
        int value,
        IntegerSettingRange range,
        Vector4 accent,
        bool enabled,
        bool hovered,
        bool active)
    {
        var scale = ImGuiHelpers.GlobalScale;
        float trackHeight = Scaled(TrackHeight);
        float inset = Scaled(TrackInset);
        float trackY = min.Y + ((size.Y - trackHeight) * 0.5f);
        Vector2 trackMin = new(min.X + inset, trackY);
        Vector2 trackMax = new(min.X + MathF.Max(inset, size.X - inset), trackY + trackHeight);
        float ratio = ResolveSliderRatio(value, range);
        float fillX = trackMin.X + ((trackMax.X - trackMin.X) * ratio);
        Vector2 fillMax = new(fillX, trackMax.Y);
        Vector2 thumbCenter = new(fillX, trackY + (trackHeight * 0.5f));
        float thumbRadius = Scaled(ThumbRadius) + (active ? scale : 0f);
        Vector4 trackFill = EditorColors.ButtonDefault with { W = enabled ? 0.46f : 0.18f };
        Vector4 trackBorder = EditorColors.Border with { W = enabled ? 0.26f : 0.12f };
        Vector4 activeFill = accent with { W = enabled ? (active ? 0.92f : 0.68f) : 0.24f };
        Vector4 thumbFill = enabled
            ? accent with { W = active ? 1f : 0.88f }
            : EditorColors.TextDisabled with { W = 0.34f };
        Vector4 thumbBorder = hovered || active
            ? EditorColors.Text with { W = enabled ? 0.72f : 0.22f }
            : EditorColors.Border with { W = enabled ? 0.42f : 0.14f };
        ImDrawListPtr drawList = ImGui.GetWindowDrawList();
        float rounding = trackHeight * 0.5f;

        drawList.AddRectFilled(trackMin, trackMax, ImGui.GetColorU32(trackFill), rounding);
        if (fillMax.X > trackMin.X)
        {
            drawList.AddRectFilled(trackMin, fillMax, ImGui.GetColorU32(activeFill), rounding);
        }

        drawList.AddRect(trackMin, trackMax, ImGui.GetColorU32(trackBorder), rounding, ImDrawFlags.None, scale);
        drawList.AddCircleFilled(thumbCenter, thumbRadius, ImGui.GetColorU32(thumbFill));
        drawList.AddCircle(thumbCenter, thumbRadius, ImGui.GetColorU32(thumbBorder), 0, scale);
    }

    private static int ResolveSliderValue(float mouseX, float controlX, float width, IntegerSettingRange range)
    {
        float inset = Scaled(TrackInset);
        float trackMin = controlX + inset;
        float trackMax = controlX + MathF.Max(inset, width - inset);
        float ratio = Math.Clamp((mouseX - trackMin) / MathF.Max(1f, trackMax - trackMin), 0f, 1f);
        float rawValue = range.Minimum + ((range.Maximum - range.Minimum) * ratio);
        int stepCount = (int)MathF.Round((rawValue - range.Minimum) / range.StepSize);
        return range.Clamp(range.Minimum + (stepCount * range.StepSize));
    }

    private static float ResolveSliderRatio(int value, IntegerSettingRange range)
    {
        if (range.Maximum <= range.Minimum)
        {
            return 0f;
        }

        return Math.Clamp((range.Clamp(value) - range.Minimum) / (float)(range.Maximum - range.Minimum), 0f, 1f);
    }
}

