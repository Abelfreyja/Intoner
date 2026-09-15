using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Intoner.Objects.Models;
using Intoner.Objects.Rendering.Drawing;
using Intoner.Objects.Runtime;
using Intoner.Objects.UI.Bounds;
using Intoner.Objects.UI.Components;
using Intoner.Objects.UI.Services;
using Intoner.Scene;
using Intoner.Services.Input;
using Intoner.UI;
using System.Numerics;

namespace Intoner.Objects.UI;

internal sealed class EditorSceneOverlay
{
    private readonly EditorSceneSelection _sceneSelection;
    private readonly IViewportMouseInputService _viewportMouseInput;
    private readonly ISceneInputService _sceneInputService;
    private readonly DrawManager _drawManager;
    private readonly Gizmo _gizmo;
    private readonly EditorInteraction _interaction;
    private readonly EditorSceneState _sceneState;
    private readonly IObjectSceneView _sceneView;
    private readonly ISceneItemService _sceneItemService;
    private readonly PlacementBoundsAnnotationProvider _placementBoundsAnnotationProvider = new();
    private readonly BoundsAnnotationRenderer _boundsAnnotationRenderer = new();
    private readonly List<BoundsAnnotation> _boundsAnnotations = [];
    private IReadOnlyList<SceneItemBoundsSnapshot>? _editorBoundsSource;
    private IReadOnlyDictionary<Guid, SceneItemSnapshot>? _editorBoundsItems;
    private IReadOnlyList<SceneItemBoundsSnapshot> _editorBoundsSnapshots = [];

    public EditorSceneOverlay(
        ISceneSelectionService sceneSelectionService,
        IViewportMouseInputService viewportMouseInput,
        ISceneInputService sceneInputService,
        DrawManager drawManager,
        Gizmo gizmo,
        EditorInteraction interaction,
        EditorSceneState sceneState,
        IObjectSceneView sceneView,
        ISceneItemService sceneItemService)
    {
        _sceneSelection        = new EditorSceneSelection(sceneSelectionService, interaction);
        _viewportMouseInput    = viewportMouseInput;
        _sceneInputService     = sceneInputService;
        _drawManager           = drawManager;
        _gizmo                 = gizmo;
        _interaction           = interaction;
        _sceneState            = sceneState;
        _sceneView             = sceneView;
        _sceneItemService      = sceneItemService;
    }

    private IReadOnlyList<SceneItemBoundsSnapshot> GetEditorBoundsSnapshots(
        IReadOnlyList<SceneItemBoundsSnapshot> boundsSnapshots,
        IReadOnlyDictionary<Guid, SceneItemSnapshot> placedItems)
    {
        if (!ReferenceEquals(_editorBoundsSource, boundsSnapshots) || !ReferenceEquals(_editorBoundsItems, placedItems))
        {
            _editorBoundsSource = boundsSnapshots;
            _editorBoundsItems = placedItems;
            _editorBoundsSnapshots = boundsSnapshots.Where(bounds => placedItems.ContainsKey(bounds.Id)).ToArray();
        }

        return _editorBoundsSnapshots;
    }

    private void SubmitBoundsOverlay(IReadOnlyList<SceneItemBoundsSnapshot> boundsSnapshots)
    {
        if (!_gizmo.Settings.BoundsInteractionSettings.BoundsEnabled || boundsSnapshots.Count == 0)
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

        if (!_drawManager.HasPendingLayer(DrawLayer.CurrentWindow) && _boundsAnnotations.Count == 0 && !_sceneSelection.IsDragging)
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

        using ImRaiiScope.WindowScope overlay = ImRaiiScope.Window("##objectEditorOverlay", overlayFlags);
        if (!overlay.Success)
        {
            return;
        }

        _sceneSelection.Draw(ImGui.GetWindowDrawList());
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
        IReadOnlyList<SceneItemBoundsSnapshot> boundsSnapshots,
        float thickness,
        bool selected)
    {
        SceneBoundsInteractionSettings settings = _gizmo.Settings.BoundsInteractionSettings;
        float opacity = selected ? settings.SelectedBoundsOpacity : settings.InactiveBoundsOpacity;
        foreach (SceneItemBoundsSnapshot boundsSnapshot in boundsSnapshots)
        {
            bool isSelected = _interaction.Selection.Contains(boundsSnapshot.Id);
            if (isSelected != selected || !settings.ShouldDraw(boundsSnapshot.Category, isSelected))
            {
                continue;
            }

            AddBoundsOverlayBox(
                batch,
                boundsSnapshot,
                EditorColors.BoundsOverlay(boundsSnapshot.Category, opacity),
                thickness);
        }
    }

    private void AddBoundsOverlayBox(
        DrawBatch batch,
        SceneItemBoundsSnapshot boundsSnapshot,
        Vector4 color,
        float thickness)
    {
        if (boundsSnapshot is ObjectBoundsSnapshot { OverlayShapes: { } overlayShapes })
        {
            foreach (ObjectOverlayShapeSnapshot overlayShape in overlayShapes)
            {
                AddOverlayShape(
                    batch,
                    overlayShape,
                    color with { W = color.W * Math.Clamp(overlayShape.OpacityScale, 0f, 1f) },
                    thickness);
            }

            return;
        }

        Span<Vector3> worldCorners = stackalloc Vector3[BoundsOverlayGeometry.BoxCornerCount];
        BoundsOverlayGeometry.CopyBoxCorners(boundsSnapshot, _gizmo.Settings.BoundsOverlaySpace, worldCorners);
        ShapeBuilder.AddBox(batch, worldCorners, color, thickness);
    }

    private static void AddOverlayShape(DrawBatch batch, ObjectOverlayShapeSnapshot overlayShape, Vector4 color, float thickness)
    {
        switch (overlayShape.Kind)
        {
            case ObjectOverlayShapeKind.Sphere:
                ShapeBuilder.AddSphere(batch, overlayShape.Transform, overlayShape.Extent, color, thickness);
                break;
            case ObjectOverlayShapeKind.Cone:
                ShapeBuilder.AddCone(batch, overlayShape.Transform, overlayShape.Extent, overlayShape.AngleDegrees, color, thickness);
                break;
            case ObjectOverlayShapeKind.Box:
                ShapeBuilder.AddBox(batch, overlayShape.Transform, color, thickness);
                break;
        }
    }

    public bool IsSelecting => _sceneSelection.IsActive || _viewportMouseInput.IsCapturing;

    public EditorSceneFrame PrepareFrame(bool allowSceneInteraction, bool processPointer = true)
    {
        EditorSceneState.EditorSceneData scene = _sceneState.GetEditorSceneData();
        IReadOnlyList<ObjectBoundsSnapshot> objectBounds = _sceneView.GetObjectBoundsSnapshots();
        IReadOnlyList<SceneItemBoundsSnapshot> sceneBounds = GetEditorBoundsSnapshots(_sceneItemService.GetBoundsSnapshots(), scene.ItemLookup);
        _sceneState.EvaluatePlacement(objectBounds);
        _interaction.SelectionChanged(_interaction.Selection.TryPrune(scene.SelectableItemIds));

        IReadOnlyList<SceneItemSnapshot> activeSelection = _interaction.Selection.ResolveSelectedItems(scene.ActiveItemLookup);
        if (processPointer)
        {
            ScenePointerButtons pointerButtons = EditorInputUtility.CaptureScenePointerButtons(includeAuxiliaryButtons: true);
            bool inputActive = ProcessScenePointerInput(pointerButtons, allowSceneInteraction && !IsSelecting);
            HandleSceneSelectionInput(activeSelection, sceneBounds, allowSceneInteraction, inputActive);

            activeSelection = _interaction.Selection.ResolveSelectedItems(scene.ActiveItemLookup);
        }
        else
        {
            CancelSceneSelection();
        }

        _gizmo.NormalizeMode(activeSelection);
        return new EditorSceneFrame(scene, objectBounds, sceneBounds, activeSelection);
    }

    public void DrawFrame(EditorSceneFrame frame, EditorScreenArea? obscuredArea, bool drawGizmo)
        => DrawSceneTools(frame.ActiveSelection, frame.SceneBounds, frame.ObjectBounds, _sceneState.PlacementEvaluations,
            drawGizmo ? CreateGizmoDrawOptions(obscuredArea) : null);

    public void Deactivate()
    {
        CancelSceneSelection();
        _gizmo.CancelInteractions();
        _sceneInputService.Deactivate();
    }

    private void CancelSceneSelection()
    {
        _sceneSelection.Cancel();
        _viewportMouseInput.Cancel();
    }

    private void DrawSceneTools(
        IReadOnlyList<SceneItemSnapshot> activeSelectedItems,
        IReadOnlyList<SceneItemBoundsSnapshot> sceneBoundsSnapshots,
        IReadOnlyList<ObjectBoundsSnapshot> objectBoundsSnapshots,
        IReadOnlyDictionary<Guid, PlacementEvaluation> placementEvaluations,
        GizmoDrawOptions? gizmoOptions)
    {
        _drawManager.BeginFrame();
        SubmitBoundsOverlay(sceneBoundsSnapshots);
        SubmitHousingPlacementOverlay(objectBoundsSnapshots, placementEvaluations);
        DrawCurrentWindowLayer(objectBoundsSnapshots, placementEvaluations);
        if (gizmoOptions.HasValue)
        {
            _gizmo.Draw(activeSelectedItems, sceneBoundsSnapshots, gizmoOptions.Value);
        }
    }

    private GizmoDrawOptions CreateGizmoDrawOptions(EditorScreenArea? obscuredArea)
        => new(IsSelecting || IsSceneInputBlockedByUi(), obscuredArea);

    private void HandleSceneSelectionInput(
        IReadOnlyList<SceneItemSnapshot> activeSelectedItems,
        IReadOnlyList<SceneItemBoundsSnapshot> boundsSnapshots,
        bool allowSceneInteraction,
        bool sceneInputActive)
    {
        ImGuiViewportPtr viewport = ImGui.GetMainViewport();
        ImGuiIOPtr io = ImGui.GetIO();
        bool cancel = !allowSceneInteraction || !_gizmo.Settings.BoundsInteractionSettings.SelectionEnabled
            || sceneInputActive || _sceneInputService.IsActive || !DesktopInputGuard.CanRoutePointerToCurrentProcess()
            || !ImGui.IsMousePosValid() || ImGui.IsAnyItemActive() || ImGui.IsKeyPressed(ImGuiKey.Escape, false);
        if (cancel)
        {
            CancelSceneSelection();
            return;
        }

        bool boxSelectionEnabled = _gizmo.Settings.BoundsInteractionSettings.BoxSelectionEnabled;
        if (!boxSelectionEnabled)
        {
            _sceneSelection.Cancel();
        }

        _viewportMouseInput.Configure(viewport.Pos, viewport.Size, !IsSceneSelectionBlockedByUi(), boxSelectionEnabled);
        for (int sample = 0; sample < 2 && _viewportMouseInput.TryRead(out ViewportMouseInput input); ++sample)
        {
            if (input.Pressed && (IsSceneSelectionBlockedByUi()
                || _gizmo.IsSelectionBlocked(activeSelectedItems, boundsSnapshots, input.Position)))
            {
                CancelSceneSelection();
                break;
            }

            _sceneSelection.ProcessPointer(new(
                new EditorScreenArea(viewport.Pos, viewport.Pos + viewport.Size), input.Position, input.Buttons,
                input.Pressed, input.Cancelled, input.Control, io.MouseDragThreshold * ImGuiHelpers.GlobalScale, boxSelectionEnabled));
        }
    }

    private bool ProcessScenePointerInput(ScenePointerButtons pointerButtons, bool allowNewInput)
    {
        if (!_sceneInputService.IsActive)
        {
            return false;
        }

        if (ImGui.IsKeyPressed(ImGuiKey.Escape))
        {
            _sceneInputService.Deactivate();
            return false;
        }

        ImGuiViewportPtr viewport = ImGui.GetMainViewport();
        ImGuiIOPtr io = ImGui.GetIO();
        bool ownsPointer = _sceneInputService.ProcessPointer(
            viewport.Pos,
            viewport.Size,
            io.MousePos,
            pointerButtons,
            io.MouseWheel,
            allowNewInput && !IsSceneInputBlockedByUi());
        if (ownsPointer)
        {
            ImGui.SetNextFrameWantCaptureMouse(true);
        }

        return ownsPointer;
    }

    private static bool IsSceneSelectionBlockedByUi()
    {
        var io = ImGui.GetIO();
        return IsSceneInputBlockedByUi() || io.WantCaptureMouse;
    }

    private static bool IsSceneInputBlockedByUi()
        => ImGui.IsAnyItemActive()
           || ImGui.IsWindowHovered(ImGuiHoveredFlags.AnyWindow);

}

internal readonly record struct EditorSceneFrame(
    EditorSceneState.EditorSceneData Scene,
    IReadOnlyList<ObjectBoundsSnapshot> ObjectBounds,
    IReadOnlyList<SceneItemBoundsSnapshot> SceneBounds,
    IReadOnlyList<SceneItemSnapshot> ActiveSelection);
