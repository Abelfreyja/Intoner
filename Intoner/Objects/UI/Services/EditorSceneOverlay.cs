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
using Intoner.UI;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Intoner.Objects.UI;

internal sealed class EditorSceneOverlay
{
    private readonly ISceneSelectionService _sceneSelectionService;
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
    private bool _objectSelectionLeftMouseWasDown;

    public EditorSceneOverlay(
        ISceneSelectionService sceneSelectionService,
        ISceneInputService sceneInputService,
        DrawManager drawManager,
        Gizmo gizmo,
        EditorInteraction interaction,
        EditorSceneState sceneState,
        IObjectSceneView sceneView,
        ISceneItemService sceneItemService)
    {
        _sceneSelectionService = sceneSelectionService;
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
        IReadOnlyList<SceneItemBoundsSnapshot> boundsSnapshots,
        float thickness,
        bool selected)
    {
        foreach (SceneItemBoundsSnapshot boundsSnapshot in boundsSnapshots)
        {
            if (!_gizmo.Settings.BoundsInteractionSettings.ShouldDraw(boundsSnapshot.Category, _interaction.Selection.Contains(boundsSnapshot.Id)))
            {
                continue;
            }

            if (_interaction.Selection.Contains(boundsSnapshot.Id) != selected)
            {
                continue;
            }

            AddBoundsOverlayBox(
                batch,
                boundsSnapshot,
                ResolveBoundsOverlayColor(boundsSnapshot.Category, selected),
                thickness);
        }
    }

    private static ImRaiiScope.WindowScope BeginEditorOverlayWindow(string name, ImGuiWindowFlags flags)
        => ImRaiiScope.Window(name, flags);

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

    private Vector4 ResolveBoundsOverlayColor(SceneBoundsCategory category, bool selected)
    {
        Vector4 color = selected
            ? ResolveSelectedBoundsOverlayColor(category)
            : ResolveBoundsOverlayColor(category);
        return ThemeColors.WithAlpha(color, ResolveBoundsOverlayOpacity(selected));
    }

    private static Vector4 ResolveSelectedBoundsOverlayColor(SceneBoundsCategory category)
        => category switch
        {
            SceneBoundsCategory.BgObject => ThemeColors.AccentOrange,
            SceneBoundsCategory.Furniture => ThemeColors.AccentBlue,
            SceneBoundsCategory.Light => ThemeColors.AccentGreen,
            SceneBoundsCategory.Vfx => ThemeColors.AccentYellow,
            SceneBoundsCategory.Display => ThemeColors.AccentPrimary,
            _ => ThemeColors.AccentPrimary,
        };

    private static Vector4 ResolveBoundsOverlayColor(SceneBoundsCategory category)
        => category switch
        {
            SceneBoundsCategory.BgObject => EditorColors.BoundsOverlay(ObjectKind.BgObject),
            SceneBoundsCategory.Furniture => EditorColors.BoundsOverlay(ObjectKind.Furniture),
            SceneBoundsCategory.Light => EditorColors.BoundsOverlay(ObjectKind.Light),
            SceneBoundsCategory.Vfx => EditorColors.BoundsOverlay(ObjectKind.Vfx),
            SceneBoundsCategory.Display => ThemeColors.AccentPrimary,
            _ => ThemeColors.AccentPrimary,
        };

    private float ResolveBoundsOverlayOpacity(bool selected)
        => Math.Clamp(
            selected
                ? _gizmo.Settings.BoundsInteractionSettings.SelectedBoundsOpacity
                : _gizmo.Settings.BoundsInteractionSettings.InactiveBoundsOpacity,
            0f,
            1f);

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
            ScenePointerButtons pointerButtons = EditorInputUtility.CaptureScenePointerButtons(_sceneInputService.IsActive);
            bool inputActive = ProcessScenePointerInput(pointerButtons, allowSceneInteraction);
            if (allowSceneInteraction)
            {
                HandleSceneSelectionInput(activeSelection, sceneBounds, pointerButtons, inputActive);
            }

            activeSelection = _interaction.Selection.ResolveSelectedItems(scene.ActiveItemLookup);
        }

        _gizmo.NormalizeMode(activeSelection);
        return new EditorSceneFrame(scene, objectBounds, sceneBounds, activeSelection);
    }

    public void DrawFrame(EditorSceneFrame frame, EditorScreenArea? obscuredArea, bool drawGizmo)
        => DrawSceneTools(frame.ActiveSelection, frame.SceneBounds, frame.ObjectBounds, _sceneState.PlacementEvaluations,
            drawGizmo ? CreateGizmoDrawOptions(obscuredArea) : null);

    public void Deactivate()
    {
        _gizmo.CancelInteractions();
        _sceneInputService.Deactivate();
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

    private static GizmoDrawOptions CreateGizmoDrawOptions(EditorScreenArea? obscuredArea)
        => new(IsSceneInputBlockedByUi(), obscuredArea);

    [StructLayout(LayoutKind.Auto)]
    internal readonly record struct SceneSelectionClick(Vector2 ViewportPos, Vector2 ViewportSize, Vector2 MousePos, bool ToggleSelection);

    private void HandleSceneSelectionInput(
        IReadOnlyList<SceneItemSnapshot> activeSelectedItems,
        IReadOnlyList<SceneItemBoundsSnapshot> boundsSnapshots,
        ScenePointerButtons pointerButtons,
        bool sceneInputActive)
    {
        if (!_gizmo.Settings.BoundsInteractionSettings.SelectionEnabled
            || !TryGetSceneSelectionClick(pointerButtons, out var click)
            || sceneInputActive)
        {
            return;
        }

        if (IsSceneSelectionBlocked(activeSelectedItems, boundsSnapshots, click.MousePos))
        {
            return;
        }

        TryApplySceneSelection(click);
    }

    private bool TryGetSceneSelectionClick(ScenePointerButtons pointerButtons, out SceneSelectionClick click)
    {
        click = default;

        var viewport = ImGui.GetMainViewport();
        var io = ImGui.GetIO();
        var mousePos = io.MousePos;

        bool isLeftMouseDown = (pointerButtons & ScenePointerButtons.Left) != ScenePointerButtons.None;
        var isLeftMouseClicked = isLeftMouseDown && !_objectSelectionLeftMouseWasDown;
        _objectSelectionLeftMouseWasDown = isLeftMouseDown;
        if (!isLeftMouseClicked)
        {
            return false;
        }

        if (mousePos.X < viewport.Pos.X
            || mousePos.X >= viewport.Pos.X + viewport.Size.X
            || mousePos.Y < viewport.Pos.Y
            || mousePos.Y >= viewport.Pos.Y + viewport.Size.Y)
        {
            return false;
        }

        click = new SceneSelectionClick(viewport.Pos, viewport.Size, mousePos, io.KeyCtrl);
        return true;
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

    private bool IsSceneSelectionBlocked(
        IReadOnlyList<SceneItemSnapshot> activeSelectedItems,
        IReadOnlyList<SceneItemBoundsSnapshot> boundsSnapshots,
        Vector2 mousePos)
        => IsSceneSelectionBlockedByUi()
            || IsSceneSelectionBlockedByGizmo(activeSelectedItems, boundsSnapshots, mousePos);

    private static bool IsSceneSelectionBlockedByUi()
    {
        var io = ImGui.GetIO();
        return ImGui.IsAnyItemActive() || io.WantCaptureMouse;
    }

    private static bool IsSceneInputBlockedByUi()
        => ImGui.IsAnyItemActive()
           || ImGui.IsWindowHovered(ImGuiHoveredFlags.AnyWindow);

    private bool IsSceneSelectionBlockedByGizmo(
        IReadOnlyList<SceneItemSnapshot> activeSelectedItems,
        IReadOnlyList<SceneItemBoundsSnapshot> boundsSnapshots,
        Vector2 mousePos)
        => _gizmo.IsSelectionBlocked(activeSelectedItems, boundsSnapshots, mousePos);

    private void TryApplySceneSelection(SceneSelectionClick click)
    {
        if (!_sceneSelectionService.TrySelectActiveItem(click.ViewportPos, click.ViewportSize, click.MousePos, out var selectedSnapshot)
            || selectedSnapshot.Locked)
        {
            return;
        }

        ApplySceneSelection(selectedSnapshot.Id, click.ToggleSelection);
    }

    private void ApplySceneSelection(Guid itemId, bool toggleSelection)
        => _interaction.SelectionChanged(_interaction.Selection.TrySelect(itemId, toggleSelection));
}

internal readonly record struct EditorSceneFrame(
    EditorSceneState.EditorSceneData Scene,
    IReadOnlyList<ObjectBoundsSnapshot> ObjectBounds,
    IReadOnlyList<SceneItemBoundsSnapshot> SceneBounds,
    IReadOnlyList<SceneItemSnapshot> ActiveSelection);
