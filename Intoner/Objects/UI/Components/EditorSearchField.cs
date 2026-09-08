using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Intoner.Objects.UI.Components;

internal static class EditorSearchField
{
    private const float FieldHeight = 30f;

    public static float Height => ResolveHeight();

    public static bool Draw(string id, ref string text, EditorSearchFieldOptions options)
    {
        text ??= string.Empty;
        options = options with
        {
            Hint = options.Hint ?? string.Empty,
            StatusText = options.StatusText ?? string.Empty,
        };
        Vector2 startCursorPos = ImGui.GetCursorPos();
        Vector2 fieldMin = ImGui.GetCursorScreenPos();
        float availableWidth = MathF.Max(1f, ImGui.GetContentRegionAvail().X);
        float width = options.Width > 0f
            ? MathF.Min(availableWidth, options.Width)
            : availableWidth;
        Vector2 fieldSize = new(width, ResolveHeight(options.Height));
        Vector2 fieldMax = fieldMin + fieldSize;
        ImDrawListPtr drawList = ImGui.GetWindowDrawList();
        float rounding = Scaled(5f);
        string before = text;
        EditorIcon.Metrics searchIconMetrics = EditorIcon.Measure(FontAwesomeIcon.Search);
        EditorIcon.Metrics clearIconMetrics = EditorIcon.Measure(FontAwesomeIcon.Times);

        drawList.AddRectFilled(
            fieldMin,
            fieldMax,
            ImGui.GetColorU32(ThemeColors.ButtonDefault with { W = 0.48f }),
            rounding);

        bool inputActive;
        using (ImRaii.PushId(id))
        {
            inputActive = DrawInput(fieldMin, fieldSize, options, clearIconMetrics.Size.X, ref text);
            DrawActions(drawList, fieldMin, fieldSize, options, searchIconMetrics, clearIconMetrics, ref text);
        }

        bool active = inputActive || ImGui.IsItemActive();
        bool hovered = ImGui.IsMouseHoveringRect(fieldMin, fieldMax, true);
        Vector4 borderColor;
        if (active)
        {
            borderColor = options.Accent with { W = 0.52f };
        }
        else
        {
            borderColor = hovered
                ? ThemeColors.Border with { W = 0.54f }
                : ThemeColors.Border with { W = 0.30f };
        }

        drawList.AddRect(
            fieldMin,
            fieldMax,
            ImGui.GetColorU32(borderColor),
            rounding,
            ImDrawFlags.None,
            Scaled(1f));
        ImGui.SetCursorPos(startCursorPos);
        ImGui.Dummy(fieldSize);

        return !string.Equals(before, text, StringComparison.Ordinal);
    }

    private static bool DrawInput(
        Vector2 fieldMin,
        Vector2 fieldSize,
        EditorSearchFieldOptions options,
        float clearIconWidth,
        ref string text)
    {
        float leftInset = Scaled(34f);
        float inputPaddingY = MathF.Max(0f, (fieldSize.Y - ImGui.GetTextLineHeight()) * 0.5f);
        float inputWidth = MathF.Max(
            1f,
            fieldSize.X - leftInset - ResolveActionAreaWidth(options.StatusText, text.Length > 0, clearIconWidth));
        bool inputActive;

        ImGui.SetCursorScreenPos(new Vector2(fieldMin.X + leftInset, fieldMin.Y));
        using (ImRaii.ItemWidth(inputWidth))
        using (ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, 0f))
        using (ImRaii.PushStyle(ImGuiStyleVar.FrameBorderSize, 0f))
        using (ImRaii.PushStyle(ImGuiStyleVar.FramePadding, new Vector2(0f, inputPaddingY)))
        using (ImRaii.PushColor(ImGuiCol.FrameBg, Vector4.Zero))
        using (ImRaii.PushColor(ImGuiCol.FrameBgHovered, Vector4.Zero))
        using (ImRaii.PushColor(ImGuiCol.FrameBgActive, Vector4.Zero))
        using (ImRaii.PushColor(ImGuiCol.Text, ThemeColors.Text with { W = 0.96f }))
        using (ImRaii.PushColor(ImGuiCol.TextDisabled, ThemeColors.TextDisabled with { W = 0.82f }))
        {
            ImGui.InputTextWithHint("##input", options.Hint, ref text, options.MaxLength);
            inputActive = ImGui.IsItemActive();
            if (ImGui.IsItemFocused() && ImGui.IsKeyPressed(ImGuiKey.Escape) && text.Length > 0)
            {
                text = string.Empty;
            }
        }

        return inputActive;
    }

    private static void DrawActions(
        ImDrawListPtr drawList,
        Vector2 fieldMin,
        Vector2 fieldSize,
        EditorSearchFieldOptions options,
        EditorIcon.Metrics searchIconMetrics,
        EditorIcon.Metrics clearIconMetrics,
        ref string text)
    {
        Vector2 searchIconPos = new(
            fieldMin.X + Scaled(12f),
            CenterY(fieldMin.Y, fieldSize.Y, searchIconMetrics.Size.Y));
        EditorIcon.Draw(
            drawList,
            FontAwesomeIcon.Search,
            searchIconMetrics,
            searchIconPos,
            ThemeColors.TextDisabled with { W = 0.78f });

        float right = fieldMin.X + fieldSize.X - Scaled(12f);
        if (options.StatusText.Length > 0)
        {
            Vector2 statusSize = ImGui.CalcTextSize(options.StatusText);
            Vector2 statusPos = new(
                right - statusSize.X,
                CenterY(fieldMin.Y, fieldSize.Y, statusSize.Y));
            Vector4 statusColor = options.StatusIsWarning
                ? ThemeColors.DimRed with { W = 0.88f }
                : ThemeColors.TextDisabled with { W = 0.72f };
            drawList.AddText(statusPos, ImGui.GetColorU32(statusColor), options.StatusText);
            right = statusPos.X - Scaled(10f);
        }

        if (text.Length == 0)
        {
            return;
        }

        float buttonWidth = clearIconMetrics.Size.X + Scaled(12f);
        Vector2 buttonMin = new(right - buttonWidth, fieldMin.Y);
        ImGui.SetCursorScreenPos(buttonMin);
        bool clearClicked = ImGui.InvisibleButton("##clear", new Vector2(buttonWidth, fieldSize.Y));
        bool clearHovered = ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem);
        Vector2 clearPos = new(
            buttonMin.X + ((buttonWidth - clearIconMetrics.Size.X) * 0.5f),
            CenterY(fieldMin.Y, fieldSize.Y, clearIconMetrics.Size.Y));
        EditorIcon.Draw(
            drawList,
            FontAwesomeIcon.Times,
            clearIconMetrics,
            clearPos,
            clearHovered ? ThemeColors.Text : ThemeColors.TextDisabled);

        if (clearClicked)
        {
            text = string.Empty;
        }

        if (clearHovered)
        {
            IntonerTooltip.DrawDescription(FontAwesomeIcon.Times, "Clear search filter");
        }
    }

    private static float ResolveActionAreaWidth(string statusText, bool hasText, float clearIconWidth)
    {
        float width = Scaled(12f);
        if (statusText.Length > 0)
        {
            width += ImGui.CalcTextSize(statusText).X + Scaled(10f);
        }

        if (hasText)
        {
            width += clearIconWidth + Scaled(12f);
        }

        return width;
    }

    private static float CenterY(float top, float height, float contentHeight)
        => top + MathF.Max(0f, (height - contentHeight) * 0.5f);

    private static float ResolveHeight(float requestedHeight = 0f)
    {
        float height = requestedHeight > 0f ? requestedHeight : Scaled(FieldHeight);
        return MathF.Min(height, MathF.Max(1f, ImGui.GetFrameHeight() + Scaled(10f)));
    }

    private static float Scaled(float value)
        => value * ImGuiHelpers.GlobalScale;
}

[StructLayout(LayoutKind.Auto)]
internal readonly record struct EditorSearchFieldOptions(
    string Hint,
    Vector4 Accent,
    string StatusText = "",
    bool StatusIsWarning = false,
    int MaxLength = 128,
    float Width = 0f,
    float Height = 0f);
