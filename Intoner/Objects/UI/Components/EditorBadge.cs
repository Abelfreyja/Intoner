using Dalamud.Interface;
using Dalamud.Interface.Textures.TextureWraps;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Intoner.Objects.UI.Components;

internal enum EditorBadgeStyle
{
    Standard,
    IconOnly,
    Muted,
}

[StructLayout(LayoutKind.Auto)]
internal readonly record struct EditorBadgeThumbnailStrip(
    int Count,
    Func<int, IDalamudTextureWrap?> GetTexture);

[StructLayout(LayoutKind.Auto)]
internal readonly record struct EditorBadge(
    string Text,
    EditorBadgeStyle Style = EditorBadgeStyle.Standard,
    FontAwesomeIcon? Icon = null,
    string? Tooltip = null,
    string? TooltipDetail = null,
    Action? DrawTooltip = null,
    Vector4? Color = null,
    EditorBadgeThumbnailStrip? Thumbnails = null)
{
    public static EditorBadge IconOnly(
        FontAwesomeIcon icon,
        string tooltip,
        string tooltipDetail,
        Vector4? color = null)
        => new(
            string.Empty,
            EditorBadgeStyle.IconOnly,
            Icon: icon,
            Tooltip: tooltip,
            TooltipDetail: tooltipDetail,
            Color: color ?? ThemeColors.TextDisabled);

    public static EditorBadge Label(
        string text,
        FontAwesomeIcon? icon = null,
        Vector4? color = null,
        string? tooltip = null)
        => new(
            text,
            Icon: icon,
            Tooltip: tooltip,
            Color: color);

    public static EditorBadge Count(
        FontAwesomeIcon icon,
        int count,
        string singular,
        string plural,
        Vector4? color = null)
        => Label(
            $"{count.ToString(CultureInfo.InvariantCulture)} {(count == 1 ? singular : plural)}",
            icon,
            color);

    public static EditorBadge Count(
        FontAwesomeIcon icon,
        int count,
        Action drawTooltip,
        Vector4? color = null)
        => Count(icon, count.ToString(CultureInfo.InvariantCulture), drawTooltip, color);

    public static EditorBadge Count(
        FontAwesomeIcon icon,
        string text,
        Action drawTooltip,
        Vector4? color = null)
        => new(
            text,
            Icon: icon,
            DrawTooltip: drawTooltip,
            Color: color);

    public static EditorBadge ThumbnailStrip(
        FontAwesomeIcon fallbackIcon,
        string text,
        int thumbnailCount,
        Func<int, IDalamudTextureWrap?> getThumbnail,
        Action drawTooltip,
        Vector4? color = null)
        => new(
            text,
            Icon: fallbackIcon,
            DrawTooltip: drawTooltip,
            Color: color,
            Thumbnails: new EditorBadgeThumbnailStrip(thumbnailCount, getThumbnail));
}
