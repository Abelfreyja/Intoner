using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Intoner.Objects.Models;
using Intoner.Objects.Rendering.Drawing;
using Intoner.Objects.Runtime;
using Intoner.Objects.UI.Bounds;
using Intoner.Objects.Utils;
using Intoner.UI;
using System.Numerics;

namespace Intoner.Objects.UI;

internal sealed partial class EditorWindow
{
    private readonly PlacementBoundsAnnotationProvider _placementBoundsAnnotationProvider = new();
    private readonly BoundsAnnotationRenderer          _boundsAnnotationRenderer = new();
    private readonly List<BoundsAnnotation>            _boundsAnnotations = [];

    private void SubmitBoundsOverlay(IReadOnlyList<ObjectBoundsSnapshot> boundsSnapshots)
    {
        if (!BoundsInteractionSettings.BoundsEnabled || boundsSnapshots.Count == 0)
        {
            return;
        }

        float thickness = MathF.Max(1.5f * ImGuiHelpers.GlobalScale, 1f);
        DrawBatch batch = _drawManager.BeginPass(DrawPassKind.Bounds, "Bounds", DrawLayer.CurrentWindow);
        AddBoundsDrawPass(batch, boundsSnapshots, thickness, selected: false);
        AddBoundsDrawPass(batch, boundsSnapshots, thickness, selected: true);
    }

    private void SubmitHousingPlacementOverlay(
        IReadOnlyList<ObjectBoundsSnapshot> boundsSnapshots,
        IReadOnlyDictionary<Guid, PlacementEvaluation> evaluations)
    {
        if (boundsSnapshots.Count == 0 || evaluations.Count == 0)
        {
            return;
        }

        float thickness = MathF.Max(2.25f * ImGuiHelpers.GlobalScale, 1.4f);
        Vector4 color = EditorColors.HousingPlacementInvalid;
        DrawBatch batch = _drawManager.BeginPass(DrawPassKind.HousingPlacement, "Housing Placement", DrawLayer.CurrentWindow);
        foreach (ObjectBoundsSnapshot boundsSnapshot in boundsSnapshots)
        {
            if (!evaluations.TryGetValue(boundsSnapshot.Id, out PlacementEvaluation? evaluation)
                || evaluation.Status != PlacementValidationStatus.Invalid)
            {
                continue;
            }

            AddBoundsOverlayBox(batch, boundsSnapshot, color, thickness);
        }
    }

    private void DrawCurrentWindowLayer(
        IReadOnlyList<ObjectBoundsSnapshot> boundsSnapshots,
        IReadOnlyDictionary<Guid, PlacementEvaluation> placementEvaluations)
    {
        _boundsAnnotations.Clear();
        _placementBoundsAnnotationProvider.Append(placementEvaluations, _boundsAnnotations);

        if (!_drawManager.HasPendingLayer(DrawLayer.CurrentWindow) && _boundsAnnotations.Count == 0)
        {
            return;
        }

        ImGuiViewportPtr viewport = ImGui.GetMainViewport();

        ImGuiHelpers.ForceNextWindowMainViewport();
        ImGui.SetNextWindowPos(viewport.Pos);
        ImGui.SetNextWindowSize(viewport.Size);
        using var windowRounding = ImRaii.PushStyle(ImGuiStyleVar.WindowRounding, 0f);
        using var windowBorderSize = ImRaii.PushStyle(ImGuiStyleVar.WindowBorderSize, 0f);

        const ImGuiWindowFlags overlayFlags =
            ImGuiWindowFlags.NoDecoration
          | ImGuiWindowFlags.NoInputs
          | ImGuiWindowFlags.NoNav
          | ImGuiWindowFlags.NoSavedSettings
          | ImGuiWindowFlags.NoBackground;

        using ImRaiiScope.WindowScope overlay = BeginEditorOverlayWindow("##objectEditorOverlay", overlayFlags);
        if (!overlay.Success)
        {
            return;
        }

        if (!DrawContext.TryCaptureEditor(viewport.Pos, viewport.Size, DrawLayer.CurrentWindow, 1f, out DrawContext context))
        {
            return;
        }

        _drawManager.DrawLayer(context);
        _boundsAnnotationRenderer.Draw(
            ImGui.GetWindowDrawList(),
            boundsSnapshots,
            _boundsAnnotations,
            _gizmo.Settings.BoundsOverlaySpace,
            context);
    }

    private void AddBoundsDrawPass(
        DrawBatch batch,
        IReadOnlyList<ObjectBoundsSnapshot> boundsSnapshots,
        float thickness,
        bool selected)
    {
        foreach (ObjectBoundsSnapshot boundsSnapshot in boundsSnapshots)
        {
            if (!ShouldDrawBoundsSnapshot(boundsSnapshot))
            {
                continue;
            }

            if (_editorSelection.Contains(boundsSnapshot.Id) != selected)
            {
                continue;
            }

            AddBoundsOverlayBox(
                batch,
                boundsSnapshot,
                ResolveBoundsOverlayColor(boundsSnapshot.Kind, selected),
                thickness);
        }
    }

    private static ImRaiiScope.WindowScope BeginEditorOverlayWindow(string name, ImGuiWindowFlags flags)
        => ImRaiiScope.Window(name, flags);

    private void AddBoundsOverlayBox(
        DrawBatch batch,
        ObjectBoundsSnapshot boundsSnapshot,
        Vector4 color,
        float thickness)
    {
        if (boundsSnapshot.OverlayShape is not null)
        {
            AddOverlayShape(batch, boundsSnapshot.OverlayShape, color, thickness);
            return;
        }

        Span<Vector3> worldCorners = stackalloc Vector3[BoundsOverlayGeometry.BoxCornerCount];
        BoundsOverlayGeometry.CopyBoxCorners(boundsSnapshot, _gizmo.Settings.BoundsOverlaySpace, worldCorners);
        ShapeBuilder.AddBox(batch, worldCorners, color, thickness);
    }

    private Vector4 ResolveBoundsOverlayColor(ObjectKind kind, bool selected)
        => selected
            ? EditorColors.WithAlpha(ResolveSelectedBoundsOverlayColor(kind), ResolveBoundsOverlayOpacity(selected: true))
            : EditorColors.WithAlpha(EditorColors.BoundsOverlay(kind), ResolveBoundsOverlayOpacity(selected: false));

    private Vector4 ResolveSelectedBoundsOverlayColor(ObjectKind kind)
        => kind switch
        {
            ObjectKind.BgObject => BoundsInteractionSettings.SelectedBoundsColors.BgObject,
            ObjectKind.Furniture => BoundsInteractionSettings.SelectedBoundsColors.Furniture,
            ObjectKind.Light => BoundsInteractionSettings.SelectedBoundsColors.Light,
            ObjectKind.Vfx => BoundsInteractionSettings.SelectedBoundsColors.Vfx,
            _ => EditorColors.AccentPurpleActive,
        };

    private float ResolveBoundsOverlayOpacity(bool selected)
        => Math.Clamp(
            selected
                ? BoundsInteractionSettings.SelectedBoundsOpacity
                : BoundsInteractionSettings.InactiveBoundsOpacity,
            0f,
            1f);

    private static void AddOverlayShape(DrawBatch batch, ObjectOverlayShapeSnapshot overlayShape, Vector4 color, float thickness)
    {
        switch (overlayShape.Kind)
        {
            case ObjectOverlayShapeKind.Sphere:
                ShapeBuilder.AddSphere(batch, overlayShape.Transform, overlayShape.Range, color, thickness);
                break;
            case ObjectOverlayShapeKind.Cone:
                ShapeBuilder.AddCone(batch, overlayShape.Transform, overlayShape.Range, overlayShape.AngleDegrees, color, thickness);
                break;
            case ObjectOverlayShapeKind.SquarePyramid:
                ShapeBuilder.AddSquarePyramid(batch, overlayShape.Transform, overlayShape.Range, overlayShape.AngleDegrees, color, thickness);
                break;
        }
    }
}

