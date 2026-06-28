using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using System.Numerics;
using static Intoner.Objects.UI.Components.EditorCard;

namespace Intoner.Objects.UI;

internal sealed partial class EditorWindow
{
    private void DrawSettingsWorkspace()
        => _settingsPage.Draw();

    private void DrawDebugWorkspace()
    {
        EnsureObjectIpcTesterInitialized();
        DrawChildPanel("##objectDebugWorkspacePanel", Vector2.Zero, false, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse, () =>
        {
            DrawUtilityWorkspaceHero(
                "objectDebugWorkspaceHero",
                FontAwesomeIcon.Bug,
                "Debug",
                "just a placeholder2");
            DrawObjectIpcTesterCard("objectDebugIpcTester");
        }, transparentBackground: false);
    }

    private static void DrawUtilityWorkspaceHero(string id, FontAwesomeIcon icon, string title, string subtitle)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var accent = EditorColors.AccentPurple;

        DrawPanelCard(
            id,
            EditorColors.ButtonDefault with { W = 0.30f },
            accent with { W = 0.24f },
            8f * scale,
            new Vector2(10f * scale, 8f * scale),
            () =>
            {
                DrawCardHeader($"{id}Header", icon, title, subtitle, accent);
            });
    }

    private static void DrawUtilityWorkspacePlaceholderCard(string id, string emptyStateText)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var padding = new Vector2(10f * scale, 8f * scale);
        var availableHeight = MathF.Max(1f, ImGui.GetContentRegionAvail().Y - ImGui.GetStyle().ItemSpacing.Y);
        var innerHeight = MathF.Max(1f, availableHeight - (padding.Y * 2f) - ImGui.GetStyle().ItemSpacing.Y);

        DrawPanelCard(
            id,
            EditorColors.ButtonDefault with { W = 0.24f },
            EditorColors.AccentPurple with { W = 0.18f },
            8f * scale,
            padding,
            () => DrawPlacedObjectsEmptyState(emptyStateText, innerHeight));
    }
}

