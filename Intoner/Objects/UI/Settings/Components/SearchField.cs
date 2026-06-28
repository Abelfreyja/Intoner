using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Intoner.Objects.UI;
using Intoner.UI;
using System.Numerics;
using static Intoner.Objects.UI.Settings.Components.SettingsChrome;

namespace Intoner.Objects.UI.Settings.Components;

internal static class SearchField
{
    private const int MaxTextLength = 128;
    private const float Height = 30f;

    public static float DrawHeight
        => ResolveHeight();

    public static bool Draw(string id, ref string text, SearchFieldStatus status, Vector4 accent)
    {
        Vector2 startCursorPos = ImGui.GetCursorPos();
        Vector2 barMin = ImGui.GetCursorScreenPos();
        Vector2 barSize = new(Positive(ImGui.GetContentRegionAvail().X), ResolveHeight());
        Vector2 barMax = barMin + barSize;
        ImDrawListPtr drawList = ImGui.GetWindowDrawList();
        var rounding = barSize.Y * 0.45f;
        string before = text;

        drawList.AddRectFilled(barMin, barMax, ImGui.GetColorU32(EditorColors.ButtonDefault with { W = 0.48f }), rounding);

        SearchInputState inputState;
        using (ImRaii.PushId(id))
        {
            inputState = DrawInput(barMin, barSize, status, ref text);
            DrawActions(drawList, barMin, barSize, status, ref text);
        }

        bool active = inputState.Active || ImGui.IsItemActive();
        bool hovered = ImGui.IsMouseHoveringRect(barMin, barMax, true);
        Vector4 borderColor = active
            ? accent with { W = 0.48f }
            : hovered
                ? EditorColors.Border with { W = 0.50f }
                : EditorColors.Border with { W = 0.26f };

        drawList.AddRect(barMin, barMax, ImGui.GetColorU32(borderColor), rounding, ImDrawFlags.None, Scaled(1f));
        ImGui.SetCursorPos(startCursorPos);
        ImGui.Dummy(barSize);

        return !string.Equals(before, text, StringComparison.Ordinal);
    }

    private static SearchInputState DrawInput(Vector2 barMin, Vector2 barSize, SearchFieldStatus status, ref string text)
    {
        float leftInset = Scaled(34f);
        float inputPaddingY = MathF.Max(0f, (barSize.Y - ImGui.GetTextLineHeight()) * 0.5f);
        float inputWidth = Positive(barSize.X - leftInset - ResolveActionAreaWidth(status, !string.IsNullOrEmpty(text)));
        bool inputActive;

        ImGui.SetCursorScreenPos(new Vector2(barMin.X + leftInset, barMin.Y));
        using (ImRaii.ItemWidth(inputWidth))
        using (ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, 0f))
        using (ImRaii.PushStyle(ImGuiStyleVar.FrameBorderSize, 0f))
        using (ImRaii.PushStyle(ImGuiStyleVar.FramePadding, new Vector2(0f, inputPaddingY)))
        using (ImRaii.PushColor(ImGuiCol.FrameBg, Vector4.Zero))
        using (ImRaii.PushColor(ImGuiCol.FrameBgHovered, Vector4.Zero))
        using (ImRaii.PushColor(ImGuiCol.FrameBgActive, Vector4.Zero))
        using (ImRaii.PushColor(ImGuiCol.Text, EditorColors.Text with { W = 0.96f }))
        using (ImRaii.PushColor(ImGuiCol.TextDisabled, EditorColors.TextDisabled with { W = 0.82f }))
        {
            ImGui.InputTextWithHint("##input", "Search object settings", ref text, MaxTextLength);
            inputActive = ImGui.IsItemActive();
            if (ImGui.IsItemFocused() && ImGui.IsKeyPressed(ImGuiKey.Escape) && !string.IsNullOrEmpty(text))
            {
                text = string.Empty;
            }
        }

        return new SearchInputState(inputActive);
    }

    private static void DrawActions(ImDrawListPtr drawList, Vector2 barMin, Vector2 barSize, SearchFieldStatus status, ref string text)
    {
        bool hasText = !string.IsNullOrEmpty(text);
        string searchIcon = FontAwesomeIcon.Search.ToIconString();
        string clearIcon = FontAwesomeIcon.Times.ToIconString();
        string counterText = status.HasQuery ? $"{status.MatchCount}/{status.TotalCount}" : $"{status.TotalCount} settings";
        Vector2 counterTextSize = ImGui.CalcTextSize(counterText);
        Vector2 searchIconSize;
        Vector2 clearIconSize;

        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            searchIconSize = ImGui.CalcTextSize(searchIcon);
            clearIconSize = ImGui.CalcTextSize(clearIcon);
        }

        Vector2 searchIconPos = new(
            barMin.X + Scaled(14f),
            barMin.Y + ((barSize.Y - searchIconSize.Y) * 0.5f));
        Vector2 counterPos = new(
            barMin.X + barSize.X - Scaled(16f) - counterTextSize.X,
            barMin.Y + ((barSize.Y - counterTextSize.Y) * 0.5f));

        drawList.AddText(UiBuilder.IconFont, ImGui.GetFontSize(), searchIconPos, ImGui.GetColorU32(EditorColors.TextDisabled), searchIcon);
        drawList.AddText(counterPos, ImGui.GetColorU32(ResolveCounterColor(status, hasText)), counterText);

        if (!hasText)
        {
            return;
        }

        Vector2 clearPos = new(
            counterPos.X - Scaled(24f),
            barMin.Y + ((barSize.Y - clearIconSize.Y) * 0.5f));
        float buttonPadding = Scaled(6f);
        ImGui.SetCursorScreenPos(new Vector2(clearPos.X - buttonPadding, barMin.Y));
        bool clearClicked = ImGui.InvisibleButton("##clear", new Vector2(clearIconSize.X + (buttonPadding * 2f), barSize.Y));
        bool clearHovered = ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem);
        Vector4 clearColor = clearHovered
            ? EditorColors.Text
            : EditorColors.TextDisabled;

        if (clearClicked)
        {
            text = string.Empty;
        }

        drawList.AddText(UiBuilder.IconFont, ImGui.GetFontSize(), clearPos, ImGui.GetColorU32(clearColor), clearIcon);
        if (clearHovered)
        {
            UiSharedService.AttachToolTip("Clear search filter");
        }
    }

    private static Vector4 ResolveCounterColor(SearchFieldStatus status, bool hasText)
    {
        if (!hasText || !status.HasQuery)
        {
            return EditorColors.TextDisabled with { W = 0.68f };
        }

        return status.MatchCount > 0
            ? EditorColors.TextDisabled with { W = 0.86f }
            : EditorColors.DimRed with { W = 0.86f };
    }

    private static float ResolveActionAreaWidth(SearchFieldStatus status, bool hasText)
    {
        string counterText = status.TotalCount > 9 ? "00/00" : "0/0";
        float counterWidth = MathF.Max(ImGui.CalcTextSize(counterText).X, ImGui.CalcTextSize($"{status.TotalCount} settings").X);
        return counterWidth + (hasText ? Scaled(42f) : Scaled(18f)) + Scaled(16f);
    }

    private static float ResolveHeight()
        => MathF.Min(Scaled(Height), MathF.Max(1f, ImGui.GetFrameHeight() + Scaled(10f)));
}

internal readonly record struct SearchFieldStatus(bool HasQuery, int MatchCount, int TotalCount);

internal readonly record struct SearchInputState(bool Active);

