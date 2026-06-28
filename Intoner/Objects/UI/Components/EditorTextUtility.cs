using Intoner.UI.Performance;

namespace Intoner.Objects.UI.Components;

internal static class EditorTextUtility
{
    public static string ClipTextToWidth(string text, float width)
        => UiText.ClipToWidth(text, width);
}

