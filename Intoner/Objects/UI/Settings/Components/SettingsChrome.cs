using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
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

    public static void DrawCenteredText(ImDrawListPtr drawList, Vector2 min, Vector2 max, string text, Vector4 color)
    {
        Vector2 size = max - min;
        Vector2 textSize = ImGui.CalcTextSize(text);
        Vector2 textPos = new(
            min.X + MathF.Max(0f, (size.X - textSize.X) * 0.5f),
            min.Y + MathF.Max(0f, (size.Y - textSize.Y) * 0.5f));
        drawList.AddText(textPos, ImGui.GetColorU32(color), text);
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

    public static FontAwesomeIcon ResolveCategoryIcon(SettingsTabDefinition? tab)
        => tab?.Icon ?? FontAwesomeIcon.ListUl;

    public static Vector4 ResolveCategoryColor(SettingsTabDefinition? tab)
        => tab?.Accent ?? ThemeColors.AccentPrimary;

    public static float Scaled(float value)
        => value * ImGuiHelpers.GlobalScale;

    public static float Positive(float value)
        => MathF.Max(1f, value);
}

