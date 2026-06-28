using Dalamud.Bindings.ImGui;

namespace Intoner.Objects.UI.Settings.Components;

internal static class InputGesture
{
    public static bool ManualInputRequested(bool hovered, bool enabled)
        => enabled
           && hovered
           && ImGui.GetIO().KeyCtrl
           && (ImGui.IsMouseClicked(ImGuiMouseButton.Left) || ImGui.IsMouseClicked(ImGuiMouseButton.Right));
}

