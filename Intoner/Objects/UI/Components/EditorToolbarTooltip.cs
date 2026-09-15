using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Intoner.Services.Input;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Intoner.Objects.UI.Components;

[StructLayout(LayoutKind.Auto)]
internal readonly record struct EditorToolbarTooltipAction(string Gesture, string Description);

[StructLayout(LayoutKind.Auto)]
internal readonly record struct EditorToolbarTooltipStatus(
    FontAwesomeIcon Icon,
    string Label,
    bool? Enabled,
    string Detail = "",
    Vector4? Accent = null);

[StructLayout(LayoutKind.Auto)]
internal readonly record struct EditorToolbarTooltipKeyHint(KeyboardModifiers Modifiers, string Description);

internal static class EditorToolbarTooltip
{
    private const float MaxWidth = 320f;
    private const float StatusIconColumnWidth = 19f;

    [StructLayout(LayoutKind.Auto)]
    private readonly record struct LayoutMetrics(
        float ContentWidth,
        float ActionGestureWidth,
        float StatusValueWidth);

    public static void Draw(
        FontAwesomeIcon icon,
        string title,
        Vector4 accent,
        IReadOnlyList<EditorToolbarTooltipAction> actions,
        IReadOnlyList<EditorToolbarTooltipStatus> statuses,
        EditorToolbarTooltipKeyHint? keyHint = null,
        string description = "")
    {
        ArgumentNullException.ThrowIfNull(actions);
        ArgumentNullException.ThrowIfNull(statuses);

        if (IntonerTooltip.IsSuppressed || string.IsNullOrWhiteSpace(title))
        {
            return;
        }

        description ??= string.Empty;
        bool hasSections = actions.Count > 0 || statuses.Count > 0;
        LayoutMetrics metrics = MeasureLayout(icon, title, description, actions, statuses, keyHint);
        IntonerTooltip.DrawContentSized(
            () =>
            {
                IntonerTooltipContent.Header(icon, title, description, accent);
                if (actions.Count > 0)
                {
                    IntonerTooltipContent.Separator(4f);
                    DrawActions(actions, accent, metrics.ActionGestureWidth);
                }

                if (statuses.Count > 0)
                {
                    IntonerTooltipContent.Separator(actions.Count > 0 ? 0f : 4f);
                    DrawStatuses(statuses, accent, metrics.StatusValueWidth);
                }

                if (keyHint is { } hint)
                {
                    IntonerTooltipContent.Separator();
                    IntonerTooltipContent.KeyHint("Hold", hint.Modifiers, hint.Description, accent);
                }
            },
            metrics.ContentWidth,
            new IntonerTooltipOptions
            {
                Accent = accent,
                MaxWidth = hasSections ? MaxWidth : null,
                ItemSpacing = hasSections ? new Vector2(6f, 5f) : null,
            });
    }

    private static void DrawActions(
        IReadOnlyList<EditorToolbarTooltipAction> actions,
        Vector4 accent,
        float gestureWidth)
    {
        using var table = ImRaii.Table(
            "##toolbarTooltipActions",
            2,
            ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.NoPadOuterX);
        if (!table)
        {
            return;
        }

        ImGui.TableSetupColumn("Gesture", ImGuiTableColumnFlags.WidthFixed, gestureWidth);
        ImGui.TableSetupColumn("Action", ImGuiTableColumnFlags.WidthStretch);
        foreach (EditorToolbarTooltipAction action in actions)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            using (ImRaii.PushColor(ImGuiCol.Text, accent with { W = 0.88f }))
            {
                ImGui.TextUnformatted(action.Gesture);
            }

            ImGui.TableNextColumn();
            using (ImRaii.PushColor(ImGuiCol.Text, ThemeColors.TextDisabled))
            {
                ImGui.TextUnformatted(action.Description);
            }
        }
    }

    private static void DrawStatuses(
        IReadOnlyList<EditorToolbarTooltipStatus> statuses,
        Vector4 accent,
        float valueWidth)
    {
        using var table = ImRaii.Table(
            "##toolbarTooltipStatuses",
            3,
            ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.NoPadOuterX);
        if (!table)
        {
            return;
        }

        ImGui.TableSetupColumn(
            "Icon",
            ImGuiTableColumnFlags.WidthFixed,
            StatusIconColumnWidth * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Setting", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthFixed, valueWidth);
        foreach (EditorToolbarTooltipStatus status in statuses)
        {
            DrawStatusRow(status, accent);
        }
    }

    private static void DrawStatusRow(EditorToolbarTooltipStatus status, Vector4 accent)
    {
        Vector4 statusAccent = status.Accent ?? accent;
        Vector4 iconColor = status.Enabled switch
        {
            true  => statusAccent,
            false => statusAccent with { W = 0.56f },
            null  => status.Accent ?? ThemeColors.Text,
        };
        Vector4 valueColor = status.Enabled == false
            ? ThemeColors.TextDisabled
            : iconColor;

        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        using (ImRaii.PushFont(UiBuilder.IconFont))
        using (ImRaii.PushColor(ImGuiCol.Text, iconColor with { W = 0.86f }))
        {
            ImGui.TextUnformatted(status.Icon.ToIconString());
        }

        ImGui.TableNextColumn();
        ImGui.TextUnformatted(status.Label);
        ImGui.TableNextColumn();
        using var valueText = ImRaii.PushColor(ImGuiCol.Text, valueColor);
        ImGui.TextUnformatted(BuildStatusValue(status));
    }

    private static LayoutMetrics MeasureLayout(
        FontAwesomeIcon icon,
        string title,
        string description,
        IReadOnlyList<EditorToolbarTooltipAction> actions,
        IReadOnlyList<EditorToolbarTooltipStatus> statuses,
        EditorToolbarTooltipKeyHint? keyHint)
    {
        float contentWidth = IntonerTooltipContent.MeasureHeaderWidth(icon, title, description);
        float actionGestureWidth = 0f;
        float actionDescriptionWidth = 0f;
        foreach (EditorToolbarTooltipAction action in actions)
        {
            actionGestureWidth = MathF.Max(actionGestureWidth, ImGui.CalcTextSize(action.Gesture).X);
            actionDescriptionWidth = MathF.Max(actionDescriptionWidth, ImGui.CalcTextSize(action.Description).X);
        }

        if (actions.Count > 0)
        {
            contentWidth = MathF.Max(
                contentWidth,
                actionGestureWidth + actionDescriptionWidth + MeasureTableColumnSpacing(2));
        }

        float statusLabelWidth = 0f;
        float statusValueWidth = 0f;
        foreach (EditorToolbarTooltipStatus status in statuses)
        {
            statusLabelWidth = MathF.Max(statusLabelWidth, ImGui.CalcTextSize(status.Label).X);
            statusValueWidth = MathF.Max(statusValueWidth, ImGui.CalcTextSize(BuildStatusValue(status)).X);
        }

        if (statuses.Count > 0)
        {
            contentWidth = MathF.Max(
                contentWidth,
                (StatusIconColumnWidth * ImGuiHelpers.GlobalScale)
              + statusLabelWidth
              + statusValueWidth
              + MeasureTableColumnSpacing(3));
        }

        if (keyHint is { } hint)
        {
            contentWidth = MathF.Max(
                contentWidth,
                IntonerTooltipContent.MeasureKeyHintWidth("Hold", KeyboardGestureFormatter.Format(hint.Modifiers), hint.Description));
        }

        return new LayoutMetrics(contentWidth, actionGestureWidth, statusValueWidth);
    }

    private static float MeasureTableColumnSpacing(int columnCount)
        => Math.Max(0, columnCount - 1) * ImGui.GetStyle().CellPadding.X * 2f;

    private static string BuildStatusValue(EditorToolbarTooltipStatus status)
    {
        if (!status.Enabled.HasValue)
        {
            return status.Detail;
        }

        string state = status.Enabled.Value ? "On" : "Off";
        return string.IsNullOrWhiteSpace(status.Detail) ? state : $"{state}  {status.Detail}";
    }
}
