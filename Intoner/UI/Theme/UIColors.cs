using System.Globalization;
using System.Numerics;

namespace Intoner.UI.Theme;

internal static class UIColors
{
    private static readonly Dictionary<string, string> DefaultHexColors = new(StringComparer.OrdinalIgnoreCase)
    {
        { "AccentPurple", "#ad8af5" },
        { "AccentPurpleActive", "#be9eff" },
        { "AccentPurpleDefault", "#9375d1" },
        { "ButtonDefault", "#323232" },
        { "FullBlack", "#000000" },
        { "AccentBlue", "#a6c2ff" },
        { "AccentYellow", "#ffe97a" },
        { "AccentYellowMuted", "#cfbd63" },
        { "AccentGreen", "#7cd68a" },
        { "AccentGreenDefault", "#468a50" },
        { "AccentOrange", "#ffb366" },
        { "AccentGrey", "#8f8f8f" },
        { "PairBlue", "#88a2db" },
        { "DimRed", "#d44444" },
        { "AdminText", "#ffd663" },
        { "AdminGlow", "#b09343" },
        { "ModeratorText", "#94ffda" },
        { "Lightfinder", "#ad8af5" },
        { "LightfinderEdge", "#000000" },
        { "ProfileBodyGradientTop", "#2f283fff" },
        { "ProfileBodyGradientBottom", "#372d4d00" },
        { "HeaderGradientTop", "#140D26FF" },
        { "HeaderGradientBottom", "#1F1433FF" },
        { "HeaderStaticStar", "#FFFFFFFF" },
        { "HeaderShootingStar", "#66CCFFFF" },
    };

    private static readonly Dictionary<string, Vector4> ParsedColors =
        DefaultHexColors.ToDictionary(
            static pair => pair.Key,
            static pair => HexToRgba(pair.Value),
            StringComparer.OrdinalIgnoreCase);

    public static Vector4 Get(string name)
        => ParsedColors.TryGetValue(name, out Vector4 color)
            ? color
            : throw new ArgumentException($"Color '{name}' not found in UIColors.", nameof(name));

    public static Vector4 GetDefault(string name)
        => Get(name);

    public static bool IsCustom(string name)
        => false;

    public static IEnumerable<string> GetColorNames()
        => DefaultHexColors.Keys;

    public static Vector4 HexToRgba(string hexColor)
    {
        hexColor = hexColor.TrimStart('#');
        int r = int.Parse(hexColor[..2], NumberStyles.HexNumber);
        int g = int.Parse(hexColor[2..4], NumberStyles.HexNumber);
        int b = int.Parse(hexColor[4..6], NumberStyles.HexNumber);
        int a = hexColor.Length == 8 ? int.Parse(hexColor[6..8], NumberStyles.HexNumber) : 255;
        return new Vector4(r / 255f, g / 255f, b / 255f, a / 255f);
    }

    public static string RgbaToHex(Vector4 color)
    {
        int r = (int)(color.X * 255);
        int g = (int)(color.Y * 255);
        int b = (int)(color.Z * 255);
        int a = (int)(color.W * 255);
        return $"#{r:X2}{g:X2}{b:X2}{(a != 255 ? a.ToString("X2") : string.Empty)}";
    }
}
