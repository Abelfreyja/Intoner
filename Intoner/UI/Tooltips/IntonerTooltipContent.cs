using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Intoner.Services.Input;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Intoner.UI.Tooltips;

[StructLayout(LayoutKind.Auto)]
internal readonly record struct IntonerTooltipKeyHint(string Prefix, string Keys, string Description)
{
    public IntonerTooltipKeyHint(KeyboardModifiers modifiers, string description)
        : this("Hold", KeyboardGestureFormatter.Format(modifiers), description)
    { }

    public IntonerTooltipKeyHint(KeyboardGesture gesture, string description)
        : this("Press", KeyboardGestureFormatter.Format(gesture), description)
    { }

    public static IntonerTooltipKeyHint? HoldOverride(
        KeyboardGesture? gesture, bool enabled, string enabledDescription, string disabledDescription)
    {
        if (gesture is not { } binding)
        {
            return null;
        }

        return new("Hold", KeyboardGestureFormatter.Format(binding), enabled ? enabledDescription : disabledDescription);
    }
}

internal static class IntonerTooltipContent
{
    private const float TextBlockIconSpacing = 6f;
    private const float TextBlockLineSpacing = 2f;
    private const float HeaderVerticalPadding = 6f;
    private const float SectionSpacing = 8f;
    private const float NoticeRailWidth = 2f;
    private const float NoticeContentInset = 9f;
    private const float NoticeVerticalPadding = 4f;
    private const float NoticeIconSpacing = 7f;
    private const float PropertyCellPaddingX = 4f;
    private const float PropertyCellPaddingY = 2f;
    private const float PropertyLabelMinWidth = 62f;
    private const float KeyHintHorizontalPadding = 5f;
    private const float KeyHintSegmentSpacing = 5f;

    internal static Vector2 TableCellPadding
    {
        get
        {
            float scale = ImGuiHelpers.GlobalScale;
            return new Vector2(MathF.Round(8f * scale), MathF.Round(2f * scale));
        }
    }

    public static float MeasureHeaderWidth(FontAwesomeIcon? icon, string title, string subtitle = "")
        => MathF.Max(
            MeasureItemWidth(icon, title),
            ImGui.CalcTextSize(subtitle).X);

    public static float MeasureItemWidth(FontAwesomeIcon? icon, string title, string subtitle = "")
        => EditorIconText.MeasureWidth(icon, title, subtitle, TextBlockIconSpacing);

    public static float MeasureNoticeWidth(FontAwesomeIcon icon, string text)
        => (NoticeContentInset * ImGuiHelpers.GlobalScale)
         + EditorIconText.MeasureWidth(icon, text, string.Empty, NoticeIconSpacing);

    public static float MeasureKeyHintWidth(IntonerTooltipKeyHint hint)
        => MeasureKeyHintWidth(hint.Prefix, hint.Keys, hint.Description);

    public static float MeasureKeyHintWidth(string prefix, string key, string suffix)
    {
        if (string.IsNullOrWhiteSpace(prefix)
         || string.IsNullOrWhiteSpace(key)
         || string.IsNullOrWhiteSpace(suffix))
        {
            return 0f;
        }

        return ImGui.CalcTextSize(prefix).X
             + ImGui.CalcTextSize(key).X
             + ImGui.CalcTextSize(suffix).X
             + ((KeyHintHorizontalPadding * 2f) + (KeyHintSegmentSpacing * 2f)) * ImGuiHelpers.GlobalScale;
    }

    public static float MeasurePropertyWidth(string label, string value)
        => ResolvePropertyLabelWidth(label)
         + ImGui.CalcTextSize(value).X
         + (PropertyCellPaddingX * 2f * ImGuiHelpers.GlobalScale);

    public static void Header(
        FontAwesomeIcon icon,
        string title,
        string subtitle = "",
        Vector4? accentOverride = null,
        Vector4? titleColorOverride = null)
        => DrawHeader(icon, title, subtitle, accentOverride, titleColorOverride, hasBody: true);

    public static void Label(
        FontAwesomeIcon? icon,
        string title,
        Vector4? accentOverride = null,
        Vector4? titleColorOverride = null)
        => DrawHeader(icon, title, string.Empty, accentOverride, titleColorOverride, hasBody: false);

    private static void DrawHeader(
        FontAwesomeIcon? icon,
        string title,
        string subtitle,
        Vector4? accentOverride,
        Vector4? titleColorOverride,
        bool hasBody)
    {
        float scale = ImGuiHelpers.GlobalScale;
        ImGuiStylePtr style = ImGui.GetStyle();
        Vector2 windowMin = ImGui.GetWindowPos();
        Vector2 windowMax = windowMin + ImGui.GetWindowSize();
        Vector2 contentStart = ImGui.GetCursorScreenPos();
        Vector2 headerMin = windowMin + new Vector2(style.WindowBorderSize);
        EditorIconTextOptions textOptions = CreateTextBlockOptions(titleColorOverride);
        float headerBottom = windowMax.Y - style.WindowBorderSize;

        if (hasBody)
        {
            float contentWidth = MathF.Max(1f, ImGui.GetContentRegionAvail().X);
            float titleHeight = EditorIconText.MeasureHeight(icon, title, string.Empty, contentWidth, textOptions);
            headerBottom = headerMin.Y + titleHeight + HeaderVerticalPadding * 2f * scale;
        }

        Vector2 headerMax = new(windowMax.X - style.WindowBorderSize, headerBottom);
        Vector4 background = IntonerTooltip.ResolveHeaderBackground(ThemeColors.Style(ImGuiCol.PopupBg));
        ImDrawListPtr drawList = ImGui.GetWindowDrawList();

        drawList.PushClipRect(windowMin, windowMax, false);
        try
        {
            drawList.AddRectFilled(
                headerMin,
                headerMax,
                ImGui.GetColorU32(background),
                MathF.Max(0f, style.WindowRounding - style.WindowBorderSize),
                hasBody ? ImDrawFlags.RoundCornersTop : ImDrawFlags.RoundCornersAll);

            if (hasBody)
            {
                drawList.AddLine(
                    new Vector2(headerMin.X, headerBottom),
                    headerMax,
                    ImGui.GetColorU32(ThemeColors.Border with { W = 0.58f }),
                    scale);
            }
        }
        finally
        {
            drawList.PopClipRect();
        }

        Vector4 accent = accentOverride ?? ThemeColors.AccentPrimary;
        ImGui.SetCursorScreenPos(hasBody
            ? new Vector2(contentStart.X, headerMin.Y + HeaderVerticalPadding * scale)
            : contentStart);
        EditorIconText.Draw(icon, title, string.Empty, accent, textOptions);

        if (!hasBody)
        {
            return;
        }

        ImGui.SetCursorScreenPos(new Vector2(contentStart.X, headerBottom));
        ImGui.Dummy(new Vector2(0f, MathF.Max(0f, SectionSpacing * scale - style.ItemSpacing.Y)));

        if (!string.IsNullOrWhiteSpace(subtitle))
        {
            using var color = ImRaii.PushColor(ImGuiCol.Text, ThemeColors.TextDisabled);
            using var wrap = ImRaii.TextWrapPos(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X);
            ImGui.TextUnformatted(subtitle);
        }
    }

    public static void Item(
        FontAwesomeIcon icon,
        string title,
        string detail,
        Vector4? accentOverride = null,
        Vector4? titleColorOverride = null)
        => DrawTextBlock(icon, title, detail, accentOverride ?? ThemeColors.AccentPrimary, titleColorOverride);

    public static void Property(string label, string value)
    {
        using var cellPadding = ImRaii.PushStyle(
            ImGuiStyleVar.CellPadding,
            new Vector2(PropertyCellPaddingX, PropertyCellPaddingY) * ImGuiHelpers.GlobalScale);
        using var table = ImRaii.Table(
            $"##tooltipProperty:{label}",
            2,
            ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.NoPadOuterX);
        if (!table)
        {
            return;
        }

        ImGui.TableSetupColumn("Label", ImGuiTableColumnFlags.WidthFixed, ResolvePropertyLabelWidth(label));
        ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.TextDisabled(label);
        ImGui.TableNextColumn();
        using var wrap = ImRaii.TextWrapPos(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X);
        ImGui.TextUnformatted(value);
    }

    public static void Notice(
        FontAwesomeIcon icon,
        string text,
        Vector4? accentOverride = null)
    {
        Vector4 accent = accentOverride ?? ThemeColors.AccentPrimary;
        float scale = ImGuiHelpers.GlobalScale;
        float width = MathF.Max(1f, ImGui.GetContentRegionAvail().X);
        Vector2 cursor = ImGui.GetCursorPos();
        Vector2 start = ImGui.GetCursorScreenPos();
        Vector2 contentStart = start + new Vector2(NoticeContentInset * scale, NoticeVerticalPadding * scale);
        ImGui.SetCursorScreenPos(contentStart);
        using (ImRaii.Group())
        {
            EditorIconText.Draw(
                icon,
                text,
                string.Empty,
                accent,
                new EditorIconTextOptions
                {
                    WrapTitle = true,
                    IconSpacing = NoticeIconSpacing,
                    TitleColor = ThemeColors.Text,
                });
        }

        float contentHeight = MathF.Max(ImGui.GetTextLineHeight(), ImGui.GetItemRectMax().Y - contentStart.Y);
        float height = contentHeight + (NoticeVerticalPadding * 2f * scale);
        ImDrawListPtr drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(
            start,
            start + new Vector2(NoticeRailWidth * scale, height),
            ImGui.GetColorU32(accent with { W = 0.78f }),
            scale);

        ImGui.SetCursorPos(cursor);
        ImGui.Dummy(new Vector2(width, height));
    }

    public static void Separator(float topSpacing = 0f)
    {
        float scale = ImGuiHelpers.GlobalScale;
        float inset = MathF.Max(0f, SectionSpacing * scale - ImGui.GetStyle().ItemSpacing.Y);
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + inset + topSpacing * scale);
        using var color = ImRaii.PushColor(ImGuiCol.Separator, ThemeColors.Separator with { W = 0.44f });
        ImGui.Separator();
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + inset);
    }

    public static void SectionLabel(string label, Vector4? accentOverride = null)
    {
        Vector4 accent = accentOverride ?? ThemeColors.AccentPrimary;
        using var color = ImRaii.PushColor(ImGuiCol.Text, accent with { W = 0.86f });
        ImGui.TextUnformatted(label);
    }

    public static void KeyHint(IntonerTooltipKeyHint hint, Vector4? accentOverride = null)
        => KeyHint(hint.Prefix, hint.Keys, hint.Description, accentOverride);

    public static void KeyHint(
        string prefix,
        KeyboardGesture gesture,
        string suffix,
        Vector4? accentOverride = null,
        float topSpacing = 0f)
        => KeyHint(prefix, KeyboardGestureFormatter.Format(gesture), suffix, accentOverride, topSpacing);

    public static void KeyHint(
        string prefix,
        KeyboardModifiers modifiers,
        string suffix,
        Vector4? accentOverride = null,
        float topSpacing = 0f)
        => KeyHint(prefix, KeyboardGestureFormatter.Format(modifiers), suffix, accentOverride, topSpacing);

    public static void KeyHint(
        string prefix,
        ImGuiKey key,
        string suffix,
        Vector4? accentOverride = null,
        float topSpacing = 0f)
        => KeyHint(prefix, UiKeyboard.GetKeyLabel(key), suffix, accentOverride, topSpacing);

    public static void KeyHint(
        string prefix,
        string key,
        string suffix,
        Vector4? accentOverride = null,
        float topSpacing = 0f)
    {
        if (string.IsNullOrWhiteSpace(prefix)
         || string.IsNullOrWhiteSpace(key)
         || string.IsNullOrWhiteSpace(suffix))
        {
            return;
        }

        Vector4 accent = accentOverride ?? ThemeColors.AccentPrimary;
        float scale = ImGuiHelpers.GlobalScale;
        Vector2 keyPadding = new(KeyHintHorizontalPadding * scale, 1f * scale);
        Vector2 keyTextSize = ImGui.CalcTextSize(key);
        Vector2 keySize = keyTextSize + (keyPadding * 2f);
        ImDrawListPtr drawList = ImGui.GetWindowDrawList();
        Vector4 textColor = ThemeColors.TextDisabled with { W = 0.88f };

        if (topSpacing > 0f)
        {
            ImGuiHelpers.ScaledDummy(topSpacing);
        }

        using var group = ImRaii.Group();
        using (ImRaii.PushColor(ImGuiCol.Text, textColor))
        {
            ImGui.TextUnformatted(prefix);
        }

        ImGui.SameLine(0f, KeyHintSegmentSpacing * scale);
        Vector2 keyMin = ImGui.GetCursorScreenPos();
        Vector2 keyMax = keyMin + keySize;
        ImGui.Dummy(keySize);
        drawList.AddRectFilled(keyMin, keyMax, ImGui.GetColorU32(accent with { W = 0.10f }), 3f * scale);
        drawList.AddRect(
            keyMin,
            keyMax,
            ImGui.GetColorU32(accent with { W = 0.32f }),
            3f * scale,
            ImDrawFlags.None,
            MathF.Max(scale, 1f));
        drawList.AddText(keyMin + keyPadding, ImGui.GetColorU32(ThemeColors.Text with { W = 0.94f }), key);

        ImGui.SameLine(0f, KeyHintSegmentSpacing * scale);
        using var text = ImRaii.PushColor(ImGuiCol.Text, textColor);
        using var wrap = ImRaii.TextWrapPos(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X);
        ImGui.TextUnformatted(suffix);
    }

    private static void DrawTextBlock(
        FontAwesomeIcon icon,
        string title,
        string detail,
        Vector4 accent,
        Vector4? titleColorOverride = null)
    {
        EditorIconText.Draw(
            icon,
            title,
            detail,
            accent,
            CreateTextBlockOptions(titleColorOverride));
    }

    private static EditorIconTextOptions CreateTextBlockOptions(Vector4? titleColorOverride)
        => new()
        {
            WrapTitle = true,
            WrapSubtitle = true,
            IconSpacing = TextBlockIconSpacing,
            LineSpacing = TextBlockLineSpacing,
            TitleColor = titleColorOverride ?? ThemeColors.Text,
            SubtitleColor = ThemeColors.TextDisabled,
        };

    private static float ResolvePropertyLabelWidth(string label)
        => MathF.Max(
            PropertyLabelMinWidth * ImGuiHelpers.GlobalScale,
            ImGui.CalcTextSize(label).X + (PropertyCellPaddingX * 2f * ImGuiHelpers.GlobalScale));
}
