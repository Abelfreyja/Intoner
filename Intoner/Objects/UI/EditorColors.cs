using Dalamud.Bindings.ImGui;
using Intoner.Objects.Catalog;
using Intoner.Objects.Models;
using Intoner.Objects.Runtime;
using Intoner.UI.Theme;
using System.Numerics;

namespace Intoner.Objects.UI;

internal static class EditorColors
{
    public static Vector4 AccentPurple        => UIColors.Get("AccentPurple");
    public static Vector4 AccentPurpleActive  => UIColors.Get("AccentPurpleActive");
    public static Vector4 AccentPurpleDefault => UIColors.Get("AccentPurpleDefault");
    public static Vector4 ButtonDefault          => UIColors.Get("ButtonDefault");
    public static Vector4 FullBlack              => UIColors.Get("FullBlack");
    public static Vector4 AccentBlue          => UIColors.Get("AccentBlue");
    public static Vector4 AccentYellow        => UIColors.Get("AccentYellow");
    public static Vector4 AccentYellowMuted   => UIColors.Get("AccentYellowMuted");
    public static Vector4 AccentGreen         => UIColors.Get("AccentGreen");
    public static Vector4 AccentGreenDefault  => UIColors.Get("AccentGreenDefault");
    public static Vector4 AccentOrange        => UIColors.Get("AccentOrange");
    public static Vector4 AccentGrey          => UIColors.Get("AccentGrey");
    public static Vector4 DimRed                 => UIColors.Get("DimRed");

    public static Vector4 Text                            => Style(ImGuiCol.Text);
    public static Vector4 TextDisabled                    => Style(ImGuiCol.TextDisabled);
    public static Vector4 Border                          => Style(ImGuiCol.Border);
    public static Vector4 Separator                       => Style(ImGuiCol.Separator);
    public static Vector4 Button                          => Style(ImGuiCol.Button);
    public static Vector4 ButtonActive                    => Style(ImGuiCol.ButtonActive);
    public static Vector4 WindowBg                        => Style(ImGuiCol.WindowBg);
    public static Vector4 BoundsOverlayAccent             => AccentPurple;
    public static Vector4 HousingPlacementInvalid         => Color(1f, 0.16f, 0.14f, 0.82f);
    public static Vector4 GizmoTranslationDragActive      => Color(1f, 0.65f, 0.2f, 1f);
    public static Vector4 GizmoTranslationDragSuppressed  => Color(0.55f, 0.55f, 0.58f, 0.25f);
    public static Vector4 GizmoTranslationDragPath        => Color(0.75f, 0.75f, 0.80f, 0.90f);
    public static Vector4 GizmoRotationDragHighlight      => Color(1f, 0.70f, 0.30f, 0.75f);

    public const string FolderPurple = "#AD8AF5";
    public const string FolderBlue   = "#A6C2FF";
    public const string FolderGreen  = "#7CD68A";
    public const string FolderYellow = "#FFE97A";
    public const string FolderOrange = "#FFB366";
    public const string FolderRed    = "#D44444";

    public static readonly IReadOnlyList<string> FolderSwatches =
    [
        FolderPurple,
        FolderBlue,
        FolderGreen,
        FolderYellow,
        FolderOrange,
        FolderRed,
    ];

    public static Vector4 Style(ImGuiCol color)
        => ImGui.GetStyle().Colors[(int)color];

    public static Vector4 Color(float red, float green, float blue, float alpha = 1f)
        => new(red, green, blue, alpha);

    public static Vector4 Color(Vector3 rgb, float alpha = 1f)
        => new(rgb, alpha);

    public static Vector4 WithAlpha(Vector4 color, float alpha)
        => color with { W = alpha };

    public static Vector4 TransformModeAccent(GizmoTransformMode mode)
        => mode switch
        {
            GizmoTransformMode.Translation => Color(0.95f, 0.55f, 0.35f, 1f),
            GizmoTransformMode.Rotation    => Color(0.50f, 0.80f, 1.00f, 1f),
            GizmoTransformMode.Scale       => Color(0.50f, 0.90f, 0.60f, 1f),
            _                              => Text,
        };

    public static Vector4 BoundsOverlay(ObjectKind kind)
        => kind switch
        {
            ObjectKind.BgObject  => WithAlpha(AccentOrange, 0.95f),
            ObjectKind.Furniture => WithAlpha(AccentBlue, 0.95f),
            ObjectKind.Vfx       => WithAlpha(AccentYellow, 0.95f),
            ObjectKind.Light     => WithAlpha(AccentGreen, 0.95f),
            _                    => WithAlpha(Text, 0.90f),
        };

    public static Vector4 CatalogAccent(ObjectCatalogKind kind)
        => kind switch
        {
            ObjectCatalogKind.BgObject  => AccentOrange,
            ObjectCatalogKind.Furniture => AccentBlue,
            _                           => AccentPurple,
        };

    public static Vector4 HistoryEntryAccent(ObjectHistoryKind? kind)
        => kind switch
        {
            ObjectHistoryKind.Create       => AccentGreen,
            ObjectHistoryKind.Import       => AccentBlue,
            ObjectHistoryKind.Move         => AccentOrange,
            ObjectHistoryKind.Transform    => Color(0.50f, 0.80f, 1.00f, 1f),
            ObjectHistoryKind.Organization => Color(0.50f, 0.88f, 0.78f, 1f),
            ObjectHistoryKind.Appearance   => AccentPurple,
            ObjectHistoryKind.Visibility   => AccentYellow,
            ObjectHistoryKind.Remove       => DimRed,
            ObjectHistoryKind.Clear        => DimRed,
            _                              => AccentPurple,
        };

    public static Vector4 GizmoAxisBase(GizmoAxis axis)
        => axis switch
        {
            GizmoAxis.X => Color(0.95f, 0.30f, 0.30f, 1f),
            GizmoAxis.Y => Color(0.40f, 0.85f, 0.45f, 1f),
            GizmoAxis.Z => Color(0.35f, 0.60f, 1.00f, 1f),
            _           => Text,
        };

    public static Vector4 BoundsSpaceAccent(BoundsOverlaySpace space)
        => space == BoundsOverlaySpace.World
            ? AccentBlue
            : AccentOrange;
}
