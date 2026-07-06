using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using System.Numerics;

namespace Intoner.Objects.UI.Components;

internal static class EditorHeroCard
{
    public readonly record struct Content(FontAwesomeIcon Icon, string Title, string Subtitle, Vector4 Accent, Status? Status);
    public readonly record struct Status(
        FontAwesomeIcon Icon,
        string Title,
        string Message,
        Vector4 Accent,
        Vector4 MessageColor,
        StatusAction? InlineAction = null);
    public readonly record struct StatusAction(FontAwesomeIcon Icon, string Label, string Tooltip, Vector4 Accent);
    public readonly record struct Actions(Action Render, Vector2 Size);
    private readonly record struct MessageLines(string First, string Second, bool IsClipped);

    private const float MinIdentityWidth = 132f;
    private const float MinStatusWidth   = 170f;
    private const float GutterWidth      = 8f;

    public static bool Draw(string id, Content content, Actions? actions = null)
    {
        var clicked = false;

        EditorCard.DrawPanelCard(
            id,
            EditorColors.ButtonDefault with { W = 0.30f },
            content.Accent with { W = 0.24f },
            Scaled(8f),
            ScaledVector(10f, 8f),
            () => clicked = DrawContent(content, actions));

        return clicked;
    }

    private static bool DrawContent(Content content, Actions? actions)
    {
        bool hasStatus = content.Status.HasValue;
        using var cellPadding = ImRaii.PushStyle(ImGuiStyleVar.CellPadding, Vector2.Zero);
        using var table = ImRaii.Table(
            "##editorHeroCard",
            hasStatus ? 3 : 1,
            ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.NoPadInnerX | ImGuiTableFlags.NoPadOuterX);
        if (!table)
        {
            return false;
        }

        if (hasStatus)
        {
            float identityWidth = ResolveIdentityWidth(content, actions?.Size.X ?? 0f, ImGui.GetContentRegionAvail().X);
            ImGui.TableSetupColumn("Identity", ImGuiTableColumnFlags.WidthFixed, identityWidth);
            ImGui.TableSetupColumn("Gutter", ImGuiTableColumnFlags.WidthFixed, Scaled(GutterWidth));
            ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.WidthStretch);
        }
        else
        {
            ImGui.TableSetupColumn("Identity", ImGuiTableColumnFlags.WidthStretch);
        }

        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        float contentHeight = DrawIdentity(content, actions);

        if (!hasStatus)
        {
            return false;
        }

        ImGui.TableNextColumn();
        ImGui.TableNextColumn();
        return DrawStatusPanel(content.Status!.Value, contentHeight);
    }

    private static float DrawIdentity(Content content, Actions? actions)
    {
        Vector2 start = ImGui.GetCursorScreenPos();
        using (ImRaii.Group())
        {
            EditorCard.DrawIconTitleBlock(content.Icon, content.Title, content.Subtitle, content.Accent);

            if (actions.HasValue)
            {
                ImGui.Dummy(new Vector2(0f, Scaled(4f)));
                actions.Value.Render();
            }
        }
        return MathF.Ceiling(MathF.Max(Scaled(48f), ImGui.GetItemRectMax().Y - start.Y));
    }

    private static bool DrawStatusPanel(Status status, float height)
    {
        Vector2 size = new(MathF.Max(Scaled(150f), ImGui.GetContentRegionAvail().X), height);
        Vector2 min = ImGui.GetCursorScreenPos();
        DrawStatusPanelFrame(status, min, size);
        DrawStatusPanelContent(status, min, size);
        bool clicked = status.InlineAction is { } action && DrawStatusActionButton(action, min, size);
        ImGui.SetCursorScreenPos(min);
        ImGui.Dummy(size);
        return clicked;
    }

    private static void DrawStatusPanelFrame(Status status, Vector2 min, Vector2 size)
    {
        Vector2 max = min + size;
        float rounding = Scaled(6f);
        const ImDrawFlags roundingFlags = ImDrawFlags.RoundCornersRight;
        ImDrawListPtr drawList = ImGui.GetWindowDrawList();

        drawList.AddRectFilled(min, max, ImGui.GetColorU32(EditorColors.WithAlpha(EditorColors.ButtonDefault, 0.22f)), rounding, roundingFlags);
        drawList.AddRectFilled(min, max, ImGui.GetColorU32(EditorColors.WithAlpha(status.Accent, 0.035f)), rounding, roundingFlags);
        drawList.AddRectFilled(
            min,
            new Vector2(min.X + Scaled(2f), max.Y),
            ImGui.GetColorU32(EditorColors.WithAlpha(status.Accent, 0.78f)));
        drawList.AddRect(min, max, ImGui.GetColorU32(EditorColors.WithAlpha(EditorColors.Border, 0.32f)), rounding, roundingFlags);
    }

    private static void DrawStatusPanelContent(Status status, Vector2 min, Vector2 size)
    {
        Vector2 max = min + size;
        Vector2 padding = StatusPadding();
        Vector2 contentMax = new(max.X - padding.X, max.Y - padding.Y);
        ImDrawListPtr drawList = ImGui.GetWindowDrawList();

        string icon = status.Icon.ToIconString();
        Vector2 iconPos = min + padding;
        Vector2 titlePos = new(iconPos.X + Scaled(20f), iconPos.Y);
        float titleWidth = MathF.Max(1f, contentMax.X - titlePos.X);

        drawList.AddText(UiBuilder.IconFont, ImGui.GetFontSize(), iconPos, ImGui.GetColorU32(status.Accent), icon);
        DrawClippedText(drawList, titlePos, ImGui.GetColorU32(EditorColors.Text), status.Title, titleWidth);

        float detailTop = min.Y + padding.Y + ImGui.GetTextLineHeight() + Scaled(5f);
        float detailBottom = status.InlineAction.HasValue
            ? max.Y - padding.Y - ResolveStatusActionButtonHeight(size.Y) - Scaled(3f)
            : contentMax.Y;
        float detailHeight = MathF.Max(ImGui.GetTextLineHeight(), detailBottom - detailTop);
        float messageWidth = MathF.Max(1f, contentMax.X - min.X - padding.X);
        MessageLines message = SplitMessage(status.Message, messageWidth, detailHeight);
        float messageHeight = string.IsNullOrEmpty(message.Second)
            ? ImGui.GetTextLineHeight()
            : ImGui.GetTextLineHeight() * 2f;
        Vector2 messagePos = new(min.X + padding.X, detailTop + ((detailHeight - messageHeight) * 0.5f));
        uint messageColor = ImGui.GetColorU32(status.MessageColor);

        drawList.AddText(messagePos, messageColor, message.First);
        if (!string.IsNullOrEmpty(message.Second))
        {
            drawList.AddText(new Vector2(messagePos.X, messagePos.Y + ImGui.GetTextLineHeight()), messageColor, message.Second);
        }

        EditorTextUtility.AttachTooltipIfClipped(messagePos, new Vector2(messageWidth, messageHeight), status.Message, message.IsClipped);
    }

    private static bool DrawStatusActionButton(StatusAction action, Vector2 panelMin, Vector2 panelSize)
    {
        Vector2 padding = StatusPadding();
        float height = ResolveStatusActionButtonHeight(panelSize.Y);
        Vector2 buttonMin = new(panelMin.X + padding.X, panelMin.Y + panelSize.Y - padding.Y - height);
        Vector2 buttonSize = new(MathF.Max(Scaled(1f), panelSize.X - (padding.X * 2f)), height);
        Vector2 buttonMax = buttonMin + buttonSize;

        ImGui.SetCursorScreenPos(buttonMin);
        bool clicked = ImGui.InvisibleButton("##editorHeroStatusAction", buttonSize);
        bool hovered = ImGui.IsItemHovered();
        bool active = ImGui.IsItemActive();
        EditorTextUtility.ClippedText label = DrawStatusActionButtonFrame(action, buttonMin, buttonMax, hovered, active);
        AttachStatusActionTooltip(action, hovered, label.IsClipped);

        return clicked;
    }

    private static EditorTextUtility.ClippedText DrawStatusActionButtonFrame(StatusAction action, Vector2 min, Vector2 max, bool hovered, bool active)
    {
        float rounding = Scaled(4f);
        (Vector4 fill, Vector4 border) = ResolveStatusActionColors(action.Accent, hovered, active);
        ImDrawListPtr drawList = ImGui.GetWindowDrawList();

        drawList.AddRectFilled(min, max, ImGui.GetColorU32(fill), rounding);
        drawList.AddRect(min, max, ImGui.GetColorU32(border), rounding);

        string icon = action.Icon.ToIconString();
        EditorTextUtility.ClippedText label = EditorTextUtility.ClipTextToWidthResult(action.Label, MathF.Max(1f, max.X - min.X - Scaled(28f)));
        Vector2 iconSize;
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            iconSize = ImGui.CalcTextSize(icon);
        }

        float gap = Scaled(6f);
        Vector2 labelSize = ImGui.CalcTextSize(label.Text);
        float contentWidth = iconSize.X + gap + labelSize.X;
        float contentX = min.X + MathF.Max(Scaled(8f), ((max.X - min.X) - contentWidth) * 0.5f);
        float centerY = min.Y + ((max.Y - min.Y) * 0.5f);
        Vector2 iconPos = new(
            MathF.Round(contentX),
            MathF.Round(centerY - (iconSize.Y * 0.5f)));
        Vector2 labelPos = new(
            MathF.Round(iconPos.X + iconSize.X + gap),
            MathF.Round(centerY - (labelSize.Y * 0.5f)));
        uint textColor = ImGui.GetColorU32(hovered || active
            ? EditorColors.Text
            : EditorColors.WithAlpha(EditorColors.TextDisabled, 0.88f));
        drawList.AddText(UiBuilder.IconFont, ImGui.GetFontSize(), iconPos, textColor, icon);
        drawList.AddText(labelPos, textColor, label.Text);
        return label;
    }

    private static (Vector4 Fill, Vector4 Border) ResolveStatusActionColors(Vector4 accent, bool hovered, bool active)
    {
        if (active)
        {
            return (EditorColors.WithAlpha(accent, 0.16f), EditorColors.WithAlpha(accent, 0.46f));
        }

        if (hovered)
        {
            return (EditorColors.WithAlpha(accent, 0.10f), EditorColors.WithAlpha(accent, 0.34f));
        }

        return (
            EditorColors.WithAlpha(EditorColors.ButtonDefault, 0.38f),
            EditorColors.WithAlpha(EditorColors.Border, 0.34f));
    }

    private static float ResolveStatusActionButtonHeight(float panelHeight)
        => Math.Clamp(panelHeight * 0.30f, Scaled(18f), Scaled(22f));

    private static float ResolveIdentityWidth(Content content, float actionWidth, float availableWidth)
    {
        float titleWidth = ResolveIdentityTitleWidth(content);
        float preferredWidth = MathF.Max(titleWidth, actionWidth);
        float maxWidth = MathF.Max(Scaled(MinIdentityWidth), availableWidth - Scaled(MinStatusWidth + GutterWidth));
        return MathF.Min(MathF.Ceiling(preferredWidth), maxWidth);
    }

    private static float ResolveIdentityTitleWidth(Content content)
    {
        Vector2 iconSize;
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            iconSize = ImGui.CalcTextSize(content.Icon.ToIconString());
        }

        float textWidth = MathF.Max(ImGui.CalcTextSize(content.Title).X, ImGui.CalcTextSize(content.Subtitle).X);
        return iconSize.X + ImGui.GetStyle().ItemSpacing.X + textWidth;
    }

    private static MessageLines SplitMessage(string message, float width, float availableHeight)
    {
        message = message.Trim();
        if (string.IsNullOrEmpty(message) || ImGui.CalcTextSize(message).X <= width)
        {
            return new MessageLines(message, string.Empty, false);
        }

        if (availableHeight < ImGui.GetTextLineHeight() * 1.85f)
        {
            EditorTextUtility.ClippedText clipped = EditorTextUtility.ClipTextToWidthResult(message, width);
            return new MessageLines(clipped.Text, string.Empty, clipped.IsClipped);
        }

        int splitIndex = FindSplitIndex(message, width);
        if (splitIndex <= 0)
        {
            EditorTextUtility.ClippedText clipped = EditorTextUtility.ClipTextToWidthResult(message, width);
            return new MessageLines(clipped.Text, string.Empty, clipped.IsClipped);
        }

        EditorTextUtility.ClippedText firstLine = EditorTextUtility.ClipTextToWidthResult(message[..splitIndex].TrimEnd(), width);
        EditorTextUtility.ClippedText secondLine = EditorTextUtility.ClipTextToWidthResult(message[(splitIndex + 1)..].TrimStart(), width);
        return new MessageLines(firstLine.Text, secondLine.Text, firstLine.IsClipped || secondLine.IsClipped);
    }

    private static int FindSplitIndex(string message, float width)
    {
        int splitIndex = -1;
        for (var index = 0; index < message.Length; ++index)
        {
            if (message[index] != ' ')
            {
                continue;
            }

            if (ImGui.CalcTextSize(message[..index]).X > width)
            {
                break;
            }

            splitIndex = index;
        }

        return splitIndex;
    }

    private static void DrawClippedText(ImDrawListPtr drawList, Vector2 pos, uint color, string text, float width)
    {
        EditorTextUtility.ClippedText clipped = EditorTextUtility.ClipTextToWidthResult(text, width);
        drawList.AddText(pos, color, clipped.Text);
        EditorTextUtility.AttachTooltipIfClipped(pos, new Vector2(width, ImGui.GetTextLineHeight()), text, clipped.IsClipped);
    }

    private static void AttachStatusActionTooltip(StatusAction action, bool hovered, bool labelClipped)
    {
        if (!hovered)
        {
            return;
        }

        string tooltip = labelClipped
            ? ResolveActionTooltip(action)
            : action.Tooltip;
        if (!string.IsNullOrWhiteSpace(tooltip))
        {
            ImGui.SetTooltip(tooltip);
        }
    }

    private static string ResolveActionTooltip(StatusAction action)
    {
        if (string.IsNullOrWhiteSpace(action.Tooltip) || string.Equals(action.Label, action.Tooltip, StringComparison.Ordinal))
        {
            return action.Label;
        }

        return $"{action.Label}\n{action.Tooltip}";
    }

    private static Vector2 StatusPadding()
        => ScaledVector(10f, 7f);

    private static float Scaled(float value)
        => value * ImGuiHelpers.GlobalScale;

    private static Vector2 ScaledVector(float x, float y)
        => new(Scaled(x), Scaled(y));
}
