using Dalamud.Bindings.ImGui;
using Intoner.UI.Performance;
using System.Numerics;

namespace Intoner.Objects.UI.Components;

internal static class EditorTextUtility
{
    public readonly record struct ClippedText(string Text, bool IsClipped);

    public static string ClipTextToWidth(string text, float width)
        => UiText.ClipToWidth(text, width);

    public static ClippedText ClipTextToWidthResult(string text, float width)
    {
        string visibleText = ClipTextToWidth(text, width);
        return new ClippedText(visibleText, !string.Equals(text, visibleText, StringComparison.Ordinal));
    }

    public static void AttachTooltipIfClipped(Vector2 min, Vector2 size, string text, bool clipped)
    {
        if (clipped && !string.IsNullOrWhiteSpace(text) && ImGui.IsMouseHoveringRect(min, min + size))
        {
            ImGui.SetTooltip(text.Trim());
        }
    }
}

