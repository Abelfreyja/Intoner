using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
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

internal static class EditorToolbarTooltip
{
    private const float MaxWidth = 320f;

    private static Vector2 StatusCellPadding
        => new(MathF.Round(4f * ImGuiHelpers.GlobalScale), IntonerTooltipContent.TableCellPadding.Y);

    [StructLayout(LayoutKind.Auto)]
    private readonly record struct LayoutMetrics(
        float ContentWidth,
        float ActionGestureWidth,
        float StatusStateWidth,
        float StatusValueWidth)
    {
        public bool SeparateState => StatusStateWidth > 0f && StatusValueWidth > 0f;
    }

    public static void Draw(
        FontAwesomeIcon icon,
        string title,
        Vector4 accent,
        IReadOnlyList<EditorToolbarTooltipAction> actions,
        IReadOnlyList<EditorToolbarTooltipStatus> statuses,
        IReadOnlyList<IntonerTooltipKeyHint> keyHints,
        string description = "")
    {
        ArgumentNullException.ThrowIfNull(actions);
        ArgumentNullException.ThrowIfNull(statuses);
        ArgumentNullException.ThrowIfNull(keyHints);

        if (IntonerTooltip.IsSuppressed || string.IsNullOrWhiteSpace(title))
        {
            return;
        }

        description ??= string.Empty;
        bool hasSections = actions.Count > 0 || statuses.Count > 0;
        bool hasDescription = !string.IsNullOrWhiteSpace(description);
        bool hasBody = hasSections || hasDescription || keyHints.Count > 0;
        LayoutMetrics metrics = MeasureLayout(icon, title, description, actions, statuses, keyHints);
        IntonerTooltip.DrawContentSized(
            () =>
            {
                if (hasBody)
                {
                    IntonerTooltipContent.Header(icon, title, description, accent);
                }
                else
                {
                    IntonerTooltipContent.Label(icon, title, accent);
                }

                if (actions.Count > 0)
                {
                    if (hasDescription)
                    {
                        IntonerTooltipContent.Separator();
                    }

                    DrawActions(actions, metrics.ActionGestureWidth);
                }

                if (statuses.Count > 0)
                {
                    if (actions.Count > 0 || hasDescription)
                    {
                        IntonerTooltipContent.Separator();
                    }

                    DrawStatuses(statuses, accent, metrics);
                }

                if (keyHints.Count > 0)
                {
                    if (hasSections || hasDescription)
                    {
                        IntonerTooltipContent.Separator();
                    }

                    foreach (IntonerTooltipKeyHint hint in keyHints)
                    {
                        IntonerTooltipContent.KeyHint(hint, accent);
                    }
                }
            },
            metrics.ContentWidth,
            new IntonerTooltipOptions
            {
                Accent = accent,
                MaxWidth = hasSections ? MaxWidth : null,
            });
    }

    private static void DrawActions(
        IReadOnlyList<EditorToolbarTooltipAction> actions,
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

        ImGui.TableSetupColumn("Gesture", ImGuiTableColumnFlags.WidthFixed, MathF.Ceiling(gestureWidth));
        ImGui.TableSetupColumn("Action", ImGuiTableColumnFlags.WidthStretch);
        foreach (EditorToolbarTooltipAction action in actions)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(action.Gesture);

            ImGui.TableNextColumn();
            using var color = ImRaii.PushColor(ImGuiCol.Text, ThemeColors.TextDisabled);
            using var wrap = ImRaii.TextWrapPos(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X);
            ImGui.TextUnformatted(action.Description);
        }
    }

    private static void DrawStatuses(
        IReadOnlyList<EditorToolbarTooltipStatus> statuses,
        Vector4 accent,
        LayoutMetrics metrics)
    {
        bool separateState = metrics.SeparateState;
        using var cellPadding = ImRaii.PushStyle(ImGuiStyleVar.CellPadding, StatusCellPadding);
        using var table = ImRaii.Table(
            "##toolbarTooltipStatuses",
            separateState ? 3 : 2,
            ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.NoPadOuterX);

        if (!table)
        {
            return;
        }

        ImGui.TableSetupColumn("Setting", ImGuiTableColumnFlags.WidthStretch);

        if (separateState)
        {
            ImGui.TableSetupColumn("State", ImGuiTableColumnFlags.WidthFixed, metrics.StatusStateWidth);
        }

        ImGui.TableSetupColumn(
            "Value",
            ImGuiTableColumnFlags.WidthFixed,
            separateState ? metrics.StatusValueWidth : MathF.Max(metrics.StatusStateWidth, metrics.StatusValueWidth));
        foreach (EditorToolbarTooltipStatus status in statuses)
        {
            DrawStatusRow(status, accent, separateState);
        }
    }

    private static void DrawStatusRow(EditorToolbarTooltipStatus status, Vector4 accent, bool separateState)
    {
        Vector4 statusAccent = status.Accent ?? accent;
        Vector4 iconColor = status.Enabled switch
        {
            true  => statusAccent,
            false => statusAccent with { W = 0.56f },
            null  => status.Accent ?? ThemeColors.Text,
        };
        Vector4 stateColor = status.Enabled == false
            ? ThemeColors.TextDisabled
            : iconColor;

        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        IntonerTooltipContent.Item(
            status.Icon,
            status.Label,
            string.Empty,
            iconColor with { W = 0.86f });
        ImGui.TableNextColumn();

        if (separateState)
        {
            if (status.Enabled is { } enabled)
            {
                using var color = ImRaii.PushColor(ImGuiCol.Text, stateColor);
                ImGui.TextUnformatted(GetStateLabel(enabled));
            }

            ImGui.TableNextColumn();
        }

        string value = !separateState && status.Enabled is { } state
            ? GetStateLabel(state)
            : status.Detail;
        float availableWidth = ImGui.GetContentRegionAvail().X;
        float valueWidth = ImGui.CalcTextSize(value).X;
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + MathF.Max(0f, availableWidth - valueWidth));
        using var valueText = ImRaii.PushColor(
            ImGuiCol.Text,
            !separateState && status.Enabled.HasValue ? stateColor : ThemeColors.Text);
        using var wrap = ImRaii.TextWrapPos(valueWidth > availableWidth ? 0f : -1f);
        ImGui.TextUnformatted(value);
    }

    private static LayoutMetrics MeasureLayout(
        FontAwesomeIcon icon,
        string title,
        string description,
        IReadOnlyList<EditorToolbarTooltipAction> actions,
        IReadOnlyList<EditorToolbarTooltipStatus> statuses,
        IReadOnlyList<IntonerTooltipKeyHint> keyHints)
    {
        float contentWidth = IntonerTooltipContent.MeasureHeaderWidth(icon, title, description);
        float actionGestureWidth = 0f;
        float actionDescriptionWidth = 0f;
        foreach (EditorToolbarTooltipAction action in actions)
        {
            actionGestureWidth = MathF.Max(actionGestureWidth, ImGui.CalcTextSize(action.Gesture).X);
            actionDescriptionWidth = MathF.Max(actionDescriptionWidth, ImGui.CalcTextSize(action.Description).X);
        }

        actionGestureWidth = MathF.Ceiling(actionGestureWidth);

        if (actions.Count > 0)
        {
            contentWidth = MathF.Max(
                contentWidth,
                actionGestureWidth + actionDescriptionWidth + IntonerTooltipContent.TableCellPadding.X * 2f);
        }

        float statusLabelWidth = 0f;
        float statusStateWidth = 0f;
        float statusValueWidth = 0f;
        foreach (EditorToolbarTooltipStatus status in statuses)
        {
            statusLabelWidth = MathF.Max(statusLabelWidth, IntonerTooltipContent.MeasureItemWidth(status.Icon, status.Label));
            statusValueWidth = MathF.Max(statusValueWidth, MathF.Ceiling(ImGui.CalcTextSize(status.Detail).X));

            if (status.Enabled.HasValue && statusStateWidth <= 0f)
            {
                statusStateWidth = MathF.Ceiling(MathF.Max(
                    ImGui.CalcTextSize(GetStateLabel(true)).X,
                    ImGui.CalcTextSize(GetStateLabel(false)).X));
            }
        }

        LayoutMetrics metrics = new(contentWidth, actionGestureWidth, statusStateWidth, statusValueWidth);

        if (statuses.Count > 0)
        {
            float valueWidth = metrics.SeparateState
                ? statusStateWidth + statusValueWidth
                : MathF.Max(statusStateWidth, statusValueWidth);
            float columnSpacing = StatusCellPadding.X * (metrics.SeparateState ? 4f : 2f);
            contentWidth = MathF.Max(contentWidth, statusLabelWidth + valueWidth + columnSpacing);
        }

        foreach (IntonerTooltipKeyHint hint in keyHints)
        {
            contentWidth = MathF.Max(contentWidth, IntonerTooltipContent.MeasureKeyHintWidth(hint));
        }

        return metrics with { ContentWidth = contentWidth };
    }

    private static string GetStateLabel(bool enabled)
        => enabled ? "On" : "Off";
}
