using Dalamud.Bindings.ImGui;
using System.Globalization;

namespace Intoner.UI.Performance;

internal static class UiText
{
    public static string ClipToWidth(string text, float width)
        => ClipToWidth(text, width, static value => ImGui.CalcTextSize(value).X);

    internal static string ClipToWidth(string text, float width, Func<string, float> measure)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        const string ellipsis = "...";
        if (width <= 0f || measure(ellipsis) > width)
        {
            return string.Empty;
        }

        if (measure(text) <= width)
        {
            return text;
        }

        var prefixLength = FindPrefixLength(text, width - measure(ellipsis), measure);
        return prefixLength <= 0 ? ellipsis : text[..prefixLength] + ellipsis;
    }

    private static int FindPrefixLength(string text, float width, Func<string, float> measure)
    {
        var boundaries = StringInfo.ParseCombiningCharacters(text);
        var minCount = 0;
        var maxCount = boundaries.Length;
        while (minCount < maxCount)
        {
            var count = (minCount + maxCount + 1) / 2;
            var length = count == boundaries.Length ? text.Length : boundaries[count];
            if (measure(text[..length]) <= width)
            {
                minCount = count;
            }
            else
            {
                maxCount = count - 1;
            }
        }

        return minCount == boundaries.Length ? text.Length : boundaries[minCount];
    }
}
