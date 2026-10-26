using System.Numerics;

namespace Intoner.Objects.UI;

internal static class CollectionModStyle
{
    public const float RowHeight = 40f;
    public const float ActionSize = 26f;
    public const float PriorityWidth = 140f;

    public static Vector4 Row => ThemeColors.ButtonDefault with { W = 0.28f };
    public static Vector4 RowHover => Vector4.Lerp(ThemeColors.ButtonDefault, ThemeColors.AccentPrimary, 0.10f)
        with { W = 0.36f };
    public static Vector4 Settings => ThemeColors.ButtonDefault with { W = 0.12f };
    public static Vector4 Setting => ThemeColors.ButtonDefault with { W = 0.22f };
    public static Vector4 SettingChanged => Vector4.Lerp(ThemeColors.ButtonDefault, ThemeColors.AccentPrimary, 0.10f)
        with { W = 0.28f };
    public static Vector4 SettingHover => Vector4.Lerp(ThemeColors.ButtonDefault, ThemeColors.AccentPrimary, 0.08f)
        with { W = 0.34f };
    public static Vector4 Field => ThemeColors.ButtonDefault with { W = 0.34f };
    public static Vector4 FieldHover => Vector4.Lerp(ThemeColors.ButtonDefault, ThemeColors.AccentPrimary, 0.10f)
        with { W = 0.46f };
    public static Vector4 FieldBorder => ThemeColors.Border with { W = 0.44f };
}
