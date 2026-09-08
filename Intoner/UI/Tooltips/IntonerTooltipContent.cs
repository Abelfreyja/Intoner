using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Intoner.Services.Input;
using System.Numerics;

namespace Intoner.UI.Tooltips;

internal static class IntonerTooltipContent
{
    private const float TextBlockIconSpacing = 6f;
    private const float TextBlockLineSpacing = 2f;
    private const float NoticeRailWidth = 2f;
    private const float NoticeContentInset = 9f;
    private const float NoticeVerticalPadding = 4f;
    private const float NoticeIconSpacing = 7f;
    private const float PropertyCellPaddingX = 4f;
    private const float PropertyCellPaddingY = 2f;
    private const float PropertyLabelMinWidth = 62f;
    private const float KeyHintHorizontalPadding = 5f;
    private const float KeyHintSegmentSpacing = 5f;

    public static float MeasureHeaderWidth(FontAwesomeIcon icon, string title, string subtitle = "")
        => EditorIconText.MeasureWidth(icon, title, subtitle, TextBlockIconSpacing);

    public static float MeasureNoticeWidth(FontAwesomeIcon icon, string text)
        => (NoticeContentInset * ImGuiHelpers.GlobalScale)
         + EditorIconText.MeasureWidth(icon, text, string.Empty, NoticeIconSpacing);

    public static float MeasureKeyHintWidth(string prefix, string key, string suffix)
    {
        if (string.IsNullOrWhiteSpace(prefix)
         || string.IsNullOrWhiteSpace(key)
         || string.IsNullOrWhiteSpace(suffix))
        {
            return 0f;
        }

        float scale = ImGuiHelpers.GlobalScale;
        return ImGui.CalcTextSize(prefix).X
             + ImGui.CalcTextSize(key).X
             + ImGui.CalcTextSize(suffix).X
             + ((KeyHintHorizontalPadding * 2f) + (KeyHintSegmentSpacing * 2f)) * scale;
    }

    public static float MeasurePropertyWidth(string label, string value)
        => ResolvePropertyLabelWidth(label)
         + ImGui.CalcTextSize(value).X
         + (PropertyCellPaddingX * 2f * ImGuiHelpers.GlobalScale);

    public static void Heading(
        FontAwesomeIcon icon,
        string title,
        string subtitle = "",
        Vector4? accentOverride = null,
        float separatorTopSpacing = 0f)
    {
        Header(icon, title, subtitle, accentOverride);
        Separator(separatorTopSpacing);
    }

    public static void Header(
        FontAwesomeIcon icon,
        string title,
        string subtitle = "",
        Vector4? accentOverride = null,
        Vector4? titleColorOverride = null)
    {
        Vector4 accent = accentOverride ?? ThemeColors.AccentPrimary;
        DrawTextBlock(icon, title, subtitle, accent, titleColorOverride);
    }

    public static void Item(
        FontAwesomeIcon icon,
        string title,
        string detail,
        Vector4? accentOverride = null,
        Vector4? titleColorOverride = null)
        => Header(icon, title, detail, accentOverride, titleColorOverride);

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
        if (topSpacing > 0f)
        {
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + (topSpacing * ImGuiHelpers.GlobalScale));
        }

        using var color = ImRaii.PushColor(ImGuiCol.Separator, ThemeColors.Separator with { W = 0.44f });
        ImGui.Separator();
    }

    public static void SectionLabel(string label, Vector4? accentOverride = null)
    {
        Vector4 accent = accentOverride ?? ThemeColors.AccentPrimary;
        using var color = ImRaii.PushColor(ImGuiCol.Text, accent with { W = 0.86f });
        ImGui.TextUnformatted(label);
    }

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
            new EditorIconTextOptions
            {
                WrapTitle = true,
                WrapSubtitle = true,
                IconSpacing = TextBlockIconSpacing,
                LineSpacing = TextBlockLineSpacing,
                TitleColor = titleColorOverride ?? ThemeColors.Text,
                SubtitleColor = ThemeColors.TextDisabled,
            });
    }

    private static float ResolvePropertyLabelWidth(string label)
        => MathF.Max(
            PropertyLabelMinWidth * ImGuiHelpers.GlobalScale,
            ImGui.CalcTextSize(label).X + (PropertyCellPaddingX * 2f * ImGuiHelpers.GlobalScale));

}
