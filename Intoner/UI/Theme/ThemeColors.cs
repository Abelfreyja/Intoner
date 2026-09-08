using Dalamud.Bindings.ImGui;
using Intoner.Services.Configuration;
using System.Numerics;

namespace Intoner.UI.Theme;

internal static class ThemeColors
{
    private static readonly ThemeColorPalette PurplePalette = new(
        ToVector(new RgbColor(173, 138, 245)),
        ToVector(new RgbColor(190, 158, 255)));

    private static readonly ThemeColorPalette PinkPalette = new(
        ToVector(new RgbColor(217, 140, 184)),
        ToVector(new RgbColor(232, 167, 203)));

    public static Vector4 ButtonDefault { get; } = FromBytes(50, 50, 50);
    public static Vector4 TextDefault { get; } = FromBytes(255, 255, 255);
    public static Vector4 TextDisabledDefault { get; } = FromBytes(128, 128, 128);
    public static Vector4 AccentBlue { get; } = FromBytes(166, 194, 255);
    public static Vector4 AccentYellow { get; } = FromBytes(255, 233, 122);
    public static Vector4 AccentGreen { get; } = FromBytes(124, 214, 138);
    public static Vector4 AccentOrange { get; } = FromBytes(255, 179, 102);
    public static Vector4 AccentGrey { get; } = FromBytes(143, 143, 143);
    public static Vector4 DimRed { get; } = FromBytes(212, 68, 68);

    public static Vector4 AccentPrimary => Style(ImGuiCol.CheckMark);
    public static Vector4 AccentPrimaryMuted => ScaleRgb(AccentPrimary, 0.85f);
    public static Vector4 Text => Style(ImGuiCol.Text);
    public static Vector4 TextDisabled => Style(ImGuiCol.TextDisabled);
    public static Vector4 Border => Style(ImGuiCol.Border);
    public static Vector4 Separator => Style(ImGuiCol.Separator);
    public static Vector4 Button => Style(ImGuiCol.Button);
    public static Vector4 WindowBg => Style(ImGuiCol.WindowBg);

    public static ThemeColorPalette GetThemePalette(UiThemeColorConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return configuration.Source switch
        {
            UiColorSource.Preset => GetPreset(configuration.Preset),
            UiColorSource.Custom => GetCustomThemePalette(configuration.CustomColor),
            _ => throw new ArgumentOutOfRangeException(nameof(configuration), configuration.Source, null),
        };
    }

    public static ThemeColorPalette GetCustomThemePalette(RgbColor color)
        => CreatePalette(ToVector(color));

    public static ThemeColorPalette GetPreset(UiColorPreset preset)
        => preset switch
        {
            UiColorPreset.Purple => PurplePalette,
            UiColorPreset.Pink => PinkPalette,
            _ => throw new ArgumentOutOfRangeException(nameof(preset), preset, null),
        };

    public static Vector4 Style(ImGuiCol color)
        => ImGui.GetStyle().Colors[(int)color];

    public static Vector4 Color(float red, float green, float blue, float alpha = 1f)
        => new(red, green, blue, alpha);

    public static Vector4 Color(Vector3 rgb, float alpha = 1f)
        => new(rgb, alpha);

    public static Vector4 FromBytes(byte red, byte green, byte blue, byte alpha = 255)
        => new(red / 255f, green / 255f, blue / 255f, alpha / 255f);

    public static Vector4 WithAlpha(Vector4 color, float alpha)
        => color with { W = alpha };

    private static ThemeColorPalette CreatePalette(Vector4 primary)
    {
        float luminance = (primary.X * 0.2126f) + (primary.Y * 0.7152f) + (primary.Z * 0.0722f);
        Vector4 target = luminance >= 0.72f
            ? new Vector4(0f, 0f, 0f, 1f)
            : Vector4.One;
        return new ThemeColorPalette(primary, Vector4.Lerp(primary, target, 0.18f));
    }

    private static Vector4 ScaleRgb(Vector4 color, float scale)
        => new(color.X * scale, color.Y * scale, color.Z * scale, color.W);

    private static Vector4 ToVector(RgbColor color)
        => Color(color.ToNormalizedVector3());
}

internal sealed record ThemeColorPalette(Vector4 Primary, Vector4 Active);
