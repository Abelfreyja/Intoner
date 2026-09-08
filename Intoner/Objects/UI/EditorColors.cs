using Intoner.Objects.Catalog;
using Intoner.Objects.Models;
using Intoner.Scene;
using System.Numerics;

namespace Intoner.Objects.UI;

internal static class EditorColors
{
    public static Vector4 BoundsOverlayAccent => ThemeColors.AccentPrimary;
    public static Vector4 HousingPlacementInvalid => ThemeColors.Color(1f, 0.16f, 0.14f, 0.82f);
    public static Vector4 GizmoTranslationDragActive => ThemeColors.Color(1f, 0.65f, 0.2f, 1f);
    public static Vector4 GizmoTranslationDragSuppressed => ThemeColors.Color(0.55f, 0.55f, 0.58f, 0.25f);
    public static Vector4 GizmoTranslationDragPath => ThemeColors.Color(0.75f, 0.75f, 0.80f, 0.90f);
    public static Vector4 GizmoRotationDragHighlight => ThemeColors.Color(1f, 0.70f, 0.30f, 0.75f);

    public const string FolderPurple = "#AD8AF5";

    public static readonly IReadOnlyList<string> FolderSwatches =
    [
        FolderPurple,
        "#A6C2FF",
        "#7CD68A",
        "#FFE97A",
        "#FFB366",
        "#D44444",
    ];

    public static Vector4 TransformModeAccent(GizmoTransformMode mode)
        => mode switch
        {
            GizmoTransformMode.Translation => ThemeColors.Color(0.95f, 0.55f, 0.35f, 1f),
            GizmoTransformMode.Rotation => ThemeColors.Color(0.50f, 0.80f, 1.00f, 1f),
            GizmoTransformMode.Scale => ThemeColors.Color(0.50f, 0.90f, 0.60f, 1f),
            _ => ThemeColors.Text,
        };

    public static Vector4 BoundsOverlay(ObjectKind kind)
        => kind switch
        {
            ObjectKind.BgObject => ThemeColors.WithAlpha(ThemeColors.AccentOrange, 0.95f),
            ObjectKind.Furniture => ThemeColors.WithAlpha(ThemeColors.AccentBlue, 0.95f),
            ObjectKind.Vfx => ThemeColors.WithAlpha(ThemeColors.AccentYellow, 0.95f),
            ObjectKind.Light => ThemeColors.WithAlpha(ThemeColors.AccentGreen, 0.95f),
            _ => ThemeColors.WithAlpha(ThemeColors.Text, 0.90f),
        };

    public static Vector4 CatalogAccent(ObjectCatalogKind kind)
        => kind switch
        {
            ObjectCatalogKind.BgObject => ThemeColors.AccentOrange,
            ObjectCatalogKind.Furniture => ThemeColors.AccentBlue,
            _ => ThemeColors.AccentPrimary,
        };

    public static Vector4 HistoryEntryAccent(SceneHistoryKind? kind)
        => kind switch
        {
            SceneHistoryKind.Create => ThemeColors.AccentGreen,
            SceneHistoryKind.Import => ThemeColors.AccentBlue,
            SceneHistoryKind.Move => ThemeColors.AccentOrange,
            SceneHistoryKind.Transform => ThemeColors.Color(0.50f, 0.80f, 1.00f, 1f),
            SceneHistoryKind.Organization => ThemeColors.Color(0.50f, 0.88f, 0.78f, 1f),
            SceneHistoryKind.Appearance => ThemeColors.AccentPrimary,
            SceneHistoryKind.Visibility => ThemeColors.AccentYellow,
            SceneHistoryKind.Remove or SceneHistoryKind.Clear => ThemeColors.DimRed,
            _ => ThemeColors.AccentPrimary,
        };

    public static Vector4 GizmoAxisBase(GizmoAxis axis)
        => axis switch
        {
            GizmoAxis.X => ThemeColors.Color(0.95f, 0.30f, 0.30f, 1f),
            GizmoAxis.Y => ThemeColors.Color(0.40f, 0.85f, 0.45f, 1f),
            GizmoAxis.Z => ThemeColors.Color(0.35f, 0.60f, 1.00f, 1f),
            _ => ThemeColors.Text,
        };

    public static Vector4 BoundsSpaceAccent(BoundsOverlaySpace space)
        => space == BoundsOverlaySpace.World
            ? ThemeColors.AccentBlue
            : ThemeColors.AccentOrange;
}
