using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Intoner.Objects.UI;
using System.Numerics;

namespace Intoner.Objects.UI.Settings.Components;

internal static class SettingsChrome
{
    public const float SidebarWidth = 172f;
    public const float HeaderHeight = 36f;
    public const float CategoryRowHeight = 32f;
    public const float DefaultControlWidth = 190f;
    public const float WideControlWidth = 270f;
    public const float WiderControlWidth = 310f;
    public const float BodyDividerColumnWidth = 9f;
    public const float CompactCardSpacingY = 2f;
    public const int BodyColumnCount = 3;
    public const int HeaderColumnCount = 5;

    public static Vector2 PanelPadding
        => new(10f * ImGuiHelpers.GlobalScale, 8f * ImGuiHelpers.GlobalScale);

    public static Vector2 CompactPanelPadding
        => new(8f * ImGuiHelpers.GlobalScale, 8f * ImGuiHelpers.GlobalScale);

    public static float PanelRounding
        => 8f * ImGuiHelpers.GlobalScale;

    public static void DrawSectionIcon(FontAwesomeIcon icon, Vector4 accent)
    {
        DrawIcon(icon, accent);
    }

    public static void DrawTextBadge(string text, Vector4 color)
    {
        var scale = ImGuiHelpers.GlobalScale;
        Vector2 padding = new(7f * scale, 2f * scale);
        Vector2 textSize = ImGui.CalcTextSize(text);
        Vector2 badgeSize = textSize + (padding * 2f);
        Vector2 badgeMin = ImGui.GetCursorScreenPos();
        DrawBadge(ImGui.GetWindowDrawList(), badgeMin, badgeSize, text, padding, color);
        ImGui.Dummy(badgeSize);
    }

    public static void DrawBadge(ImDrawListPtr drawList, Vector2 min, Vector2 size, string text, Vector2 padding, Vector4 color)
    {
        Vector2 max = min + size;
        var rounding = 4f * ImGuiHelpers.GlobalScale;
        drawList.AddRectFilled(min, max, ImGui.GetColorU32(color with { W = 0.13f }), rounding);
        drawList.AddRect(min, max, ImGui.GetColorU32(color with { W = 0.28f }), rounding, ImDrawFlags.None, 1f * ImGuiHelpers.GlobalScale);
        drawList.AddText(min + padding, ImGui.GetColorU32(color with { W = 0.92f }), text);
    }

    public static void DrawIcon(FontAwesomeIcon icon, Vector4 color)
    {
        using (ImRaii.PushFont(UiBuilder.IconFont))
        using (ImRaii.PushColor(ImGuiCol.Text, color))
        {
            ImGui.TextUnformatted(icon.ToIconString());
        }
    }

    public static void DrawCenteredText(ImDrawListPtr drawList, Vector2 min, Vector2 max, string text, Vector4 color)
    {
        Vector2 size = max - min;
        Vector2 textSize = ImGui.CalcTextSize(text);
        Vector2 textPos = new(
            min.X + MathF.Max(0f, (size.X - textSize.X) * 0.5f),
            min.Y + MathF.Max(0f, (size.Y - textSize.Y) * 0.5f));
        drawList.AddText(textPos, ImGui.GetColorU32(color), text);
    }

    public static void DrawCenteredIcon(ImDrawListPtr drawList, Vector2 min, Vector2 max, FontAwesomeIcon icon, Vector4 color)
    {
        string text = icon.ToIconString();
        Vector2 size = max - min;
        Vector2 iconSize;
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            iconSize = ImGui.CalcTextSize(text);
        }

        Vector2 iconPos = new(
            min.X + MathF.Max(0f, (size.X - iconSize.X) * 0.5f),
            min.Y + MathF.Max(0f, (size.Y - iconSize.Y) * 0.5f));
        drawList.AddText(UiBuilder.IconFont, ImGui.GetFontSize(), iconPos, ImGui.GetColorU32(color), text);
    }

    public static void SetupBodyColumns()
    {
        ImGui.TableSetupColumn("Categories", ImGuiTableColumnFlags.WidthFixed, Scaled(SidebarWidth));
        ImGui.TableSetupColumn("Divider", ImGuiTableColumnFlags.WidthFixed, Scaled(BodyDividerColumnWidth));
        ImGui.TableSetupColumn("Settings", ImGuiTableColumnFlags.WidthStretch);
    }

    public static void SetupHeaderColumns()
    {
        ImGui.TableSetupColumn("LeftPadding", ImGuiTableColumnFlags.WidthFixed, CompactPanelPadding.X);
        SetupBodyColumns();
        ImGui.TableSetupColumn("RightPadding", ImGuiTableColumnFlags.WidthFixed, CompactPanelPadding.X);
    }

    public static FontAwesomeIcon ResolveCategoryIcon(SettingsTab? tab)
        => tab switch
        {
            null                    => FontAwesomeIcon.ListUl,
            SettingsTab.Assets      => FontAwesomeIcon.Cube,
            SettingsTab.Housing     => FontAwesomeIcon.Home,
            SettingsTab.Layouts     => FontAwesomeIcon.LayerGroup,
            SettingsTab.Ui          => FontAwesomeIcon.WindowMaximize,
            SettingsTab.Diagnostics => FontAwesomeIcon.Bug,
            _                       => FontAwesomeIcon.Cog,
        };

    public static Vector4 ResolveCategoryColor(SettingsTab? tab)
        => tab.HasValue
            ? ResolveTabAccent(tab.Value)
            : EditorColors.AccentPurple;

    public static Vector4 ResolveTabAccent(SettingsTab tab)
        => tab switch
        {
            SettingsTab.Assets      => EditorColors.AccentBlue,
            SettingsTab.Housing     => EditorColors.AccentOrange,
            SettingsTab.Layouts     => EditorColors.AccentGreen,
            SettingsTab.Ui          => EditorColors.AccentPurple,
            SettingsTab.Diagnostics => EditorColors.AccentYellow,
            _                       => EditorColors.AccentPurple,
        };

    public static float Scaled(float value)
        => value * ImGuiHelpers.GlobalScale;

    public static float Positive(float value)
        => MathF.Max(1f, value);
}

