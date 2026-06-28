using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Intoner.Objects.Models;
using Intoner.Objects.Rendering.Drawing;
using Intoner.Objects.Runtime;
using Intoner.Objects.UI.Services;
using Intoner.Objects.Utils;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;

namespace Intoner.Objects.UI;

/// <summary> host callbacks and state the gizmo needs from the editor </summary>
internal interface IGizmoHost
{
    /// <summary> gets the current editor selection revision </summary>
    int GetSelectionRevision();

    /// <summary> gets the current object scene revision </summary>
    long GetSceneRevision();

    /// <summary> captures current editor selection ids for history entries </summary>
    Guid[] CaptureCurrentSelectionIds();

    /// <summary> records a completed history action after a gizmo drag finishes </summary>
    bool TryRecordCompletedHistoryAction(
        ObjectHistoryKind kind,
        string title,
        IReadOnlyList<ObjectSnapshot> beforeSnapshots,
        IReadOnlyList<ObjectSnapshot> afterSnapshots,
        IReadOnlyList<Guid>? selectionAfterApply,
        IReadOnlyList<Guid>? selectionAfterRevert);

    /// <summary> duplicates the provided selected objects through the normal history path </summary>
    bool TryDuplicateSelectedObjects(IReadOnlyList<ObjectSnapshot> selectedSnapshots);

    /// <summary> removes the provided selected objects through the normal history path </summary>
    bool TryRemoveSelectedObjects(IReadOnlyList<ObjectSnapshot> selectedSnapshots);

    /// <summary> moves one selected object to the player through the normal history path </summary>
    bool TryMoveObjectToPlayerWithHistory(Guid objectId);

    /// <summary> applies a batch object update through the normal history path </summary>
    bool TryApplySelectedSnapshotUpdateWithHistory(
        ObjectHistoryKind kind,
        string title,
        IReadOnlyList<ObjectSnapshot> selectedSnapshots,
        Func<ObjectSnapshot, ObjectSnapshot> updateFactory);
}

internal sealed partial class Gizmo : IDisposable
{
    private readonly IGizmoHost                       _host;
    private readonly IGameInputSuppressionService     _gameInputSuppressionService;
    private readonly IObjectMutationService           _mutationService;
    private readonly IObjectPlacementResolver         _placementResolver;
    private readonly IObjectSurfaceTargetService      _surfaceTargetService;
    private readonly SurfacePlacementService          _surfacePlacementService;
    private readonly SurfaceAttachmentService         _surfaceAttachmentService;
    private readonly DrawManager                      _drawManager;

    public GizmoSettings Settings { get; } = new();

    private GizmoState State { get; } = new();

    private GizmoAxisVisualState[] AxisVisualStates
        => State.AxisVisualStates;

    private GizmoTranslationDragSession TranslationDragState
        => State.TranslationDrag;

    private GizmoRotationDragSession RotationDragState
        => State.RotationDrag;

    private GizmoScaleDragSession ScaleDragState
        => State.ScaleDrag;

    private GizmoSurfaceDragSession SurfaceDragState
        => State.SurfaceDrag;

    private bool HasActiveTransformDrag
        => TranslationDragState.IsDragging || RotationDragState.IsDragging || ScaleDragState.IsDragging;

    private IGameInputSuppressionLease? SurfaceDragInputSuppressionLease
    {
        get => State.SurfaceDragInputSuppressionLease;
        set => State.SurfaceDragInputSuppressionLease = value;
    }

    private Vector2 WheelCenter
        => State.WheelCenter;

    private bool WheelSuppressNextToggle
    {
        get => State.WheelSuppressNextToggle;
        set => State.SetWheelSuppressNextToggle(value);
    }

    private bool RadialActionsPage
        => State.RadialActionsPage;

    private GizmoRadialTooltipInfo? PendingRadialTooltip
    {
        get => State.PendingRadialTooltip;
        set => State.PendingRadialTooltip = value;
    }

    private bool TryGetActiveTransformDragState([NotNullWhen(true)] out GizmoTransformDragSession? dragState)
    {
        if (TranslationDragState.IsDragging)
        {
            dragState = TranslationDragState;
            return true;
        }

        if (RotationDragState.IsDragging)
        {
            dragState = RotationDragState;
            return true;
        }

        if (ScaleDragState.IsDragging)
        {
            dragState = ScaleDragState;
            return true;
        }

        dragState = null;
        return false;
    }

    private BoundsOverlaySpace CurrentBoundsOverlaySpace
    {
        get => Settings.BoundsOverlaySpace;
        set
        {
            if (Settings.BoundsOverlaySpace == value)
            {
                return;
            }

            Settings.BoundsOverlaySpace = value;
            State.NotifyInteractionStateChanged();
        }
    }

    private GizmoTransformMode Mode
    {
        get => Settings.Mode;
        set
        {
            if (Settings.Mode == value)
            {
                return;
            }

            Settings.Mode = value;
            State.NotifyInteractionStateChanged();
        }
    }

    private bool SurfaceAlignToNormal
        => Settings.SurfaceAlignToNormal;

    public Gizmo(
        IGizmoHost host,
        DrawManager drawManager,
        IGameInputSuppressionService gameInputSuppressionService,
        IObjectMutationService mutationService,
        IObjectPlacementResolver placementResolver,
        IObjectSurfaceTargetService surfaceTargetService,
        SurfacePlacementService surfacePlacementService,
        SurfaceAttachmentService surfaceAttachmentService)
    {
        _host = host;
        _drawManager = drawManager;
        _gameInputSuppressionService = gameInputSuppressionService;
        _mutationService = mutationService;
        _placementResolver = placementResolver;
        _surfaceTargetService = surfaceTargetService;
        _surfacePlacementService = surfacePlacementService;
        _surfaceAttachmentService = surfaceAttachmentService;
    }

    public void Dispose()
        => DisposeSurfaceDragInputSuppressionLease();

    public void NormalizeMode(IReadOnlyList<ObjectSnapshot> selectedObjects)
    {
        if (!CanUseScaleGizmo(selectedObjects) && Mode == GizmoTransformMode.Scale)
        {
            Mode = GizmoTransformMode.Rotation;
        }

        if (selectedObjects.Count == 0 && HasActiveTransformDrag)
        {
            CompleteGizmoDrag();
        }

        if (selectedObjects.Count == 0 && SurfaceDragState.IsDragging)
        {
            CompleteGizmoSurfaceDrag();
        }
    }

    public void CancelInteractions()
    {
        CompleteGizmoDrag();
        CompleteGizmoSurfaceDrag();
    }

    internal static bool CanUseScaleGizmo(ObjectSnapshot snapshot)
        => snapshot.Kind is ObjectKind.BgObject or ObjectKind.Furniture;

    internal static bool CanUseScaleGizmo(IReadOnlyList<ObjectSnapshot> selectedObjects)
        => selectedObjects.Count == 1 && CanUseScaleGizmo(selectedObjects[0]);

    public void Draw(IReadOnlyList<ObjectSnapshot> selectedObjects, IReadOnlyList<ObjectBoundsSnapshot> boundsSnapshots)
    {
        if (!TryBuildCurrentFrame(selectedObjects, boundsSnapshots, ImGui.GetIO().MousePos, out var frame))
        {
            return;
        }

        try
        {
            DrawGizmoFrame(frame);
            DrawGizmoWheel(frame.Context);
        }
        finally
        {
            if (PendingRadialTooltip.HasValue)
            {
                DrawGizmoRadialTooltip(PendingRadialTooltip.Value);
                PendingRadialTooltip = null;
            }
        }
    }

    private bool TryBuildCurrentFrame(
        IReadOnlyList<ObjectSnapshot> selectedObjects,
        IReadOnlyList<ObjectBoundsSnapshot> boundsSnapshots,
        Vector2 mousePos,
        out GizmoFrame frame)
    {
        var request = CreateFrameRequest(mousePos);
        if (State.TryGetCachedFrame(request, out frame))
        {
            return true;
        }

        frame = default;
        if (!TryPrepareGizmoContext(selectedObjects, boundsSnapshots, out var context))
        {
            return false;
        }

        var scale = ImGuiHelpers.GlobalScale;
        if (Mode == GizmoTransformMode.Rotation)
        {
            var radius = ResolveObjectAwareScreenSize(GizmoConstants.RotationRingBaseRadius, context.AxisWorldLength, scale);
            var rotationProjection = IsGizmoDragActive(context.PrimarySnapshot.Id, GizmoTransformMode.Rotation) && RotationDragState.RotationProjection.HasValue
                ? RotationDragState.RotationProjection.Value
                : CreateRotationProjectionContext(context, radius);
            frame = new GizmoFrame(context, rotationProjection, ResolveRotationInteractionState(context, rotationProjection, mousePos, scale));
            State.StoreCachedFrame(request, frame);
            return true;
        }

        var worldSpace = Mode == GizmoTransformMode.Translation && context.UseWorldSpace;
        var axisCount = BuildLinearGizmoAxisVisualStates(context, worldSpace);
        if (axisCount <= 0)
        {
            return false;
        }

        frame = new GizmoFrame(context, axisCount, ResolveLinearInteractionState(context, Mode, axisCount, mousePos, scale));
        State.StoreCachedFrame(request, frame);
        return true;
    }

    private GizmoFrameRequest CreateFrameRequest(Vector2 mousePos)
        => new(
            ImGui.GetFrameCount(),
            State.InteractionRevision,
            Mode,
            CurrentBoundsOverlaySpace,
            _host.GetSelectionRevision(),
            _host.GetSceneRevision(),
            mousePos);

    private bool TryPrepareGizmoContext(
        IReadOnlyList<ObjectSnapshot> selectedObjects,
        IReadOnlyList<ObjectBoundsSnapshot> boundsSnapshots,
        out GizmoContext context)
    {
        if (!TryBuildGizmoContext(selectedObjects, boundsSnapshots, out context))
        {
            CompleteGizmoDrag();
            CompleteGizmoSurfaceDrag();
            return false;
        }

        ValidateActiveDragTargets(context);
        return true;
    }

    private void ValidateActiveDragTargets(in GizmoContext context)
    {
        if (TryGetActiveTransformDragState(out var activeTransformDrag)
            && !activeTransformDrag.Matches(context.PrimarySnapshot.Id, Mode))
        {
            CompleteGizmoDrag();
        }

        if (SurfaceDragState.IsDragging && SurfaceDragState.ObjectId != context.PrimarySnapshot.Id)
        {
            CompleteGizmoSurfaceDrag();
        }
    }

    private bool TryBuildGizmoContext(
        IReadOnlyList<ObjectSnapshot> selectedObjects,
        IReadOnlyList<ObjectBoundsSnapshot> boundsSnapshots,
        out GizmoContext context)
    {
        context = default;

        if (selectedObjects.Count == 0 || Mode == GizmoTransformMode.None)
        {
            return false;
        }

        if (!ObjectViewportProjectionUtility.TryGetEditorCameraProjection(out var viewProjection, out var viewMatrix, out _))
        {
            return false;
        }

        var primarySnapshot = selectedObjects[^1];
        var pivotPosition = ObjectSelectionTransformMath.ResolveSelectionPivotPosition(selectedObjects, boundsSnapshots);
        var viewport = ImGui.GetMainViewport();
        if (!ObjectViewportProjectionUtility.TryProjectWorldPointToViewport(
                viewProjection,
                pivotPosition,
                viewport.Pos,
                viewport.Size,
                out var screenPos))
        {
            return false;
        }
        ResolveCameraOrientation(viewMatrix, pivotPosition, out var cameraViewDirection, out var cameraRight, out var cameraUp);

        var boundsSnapshot = selectedObjects.Count == 1
            ? ObjectSelectionTransformMath.FindBoundsSnapshot(boundsSnapshots, primarySnapshot.Id)
            : null;
        context = new GizmoContext(
            selectedObjects,
            primarySnapshot,
            boundsSnapshots,
            boundsSnapshot,
            pivotPosition,
            screenPos,
            viewport.Pos,
            viewport.Size,
            viewProjection,
            ObjectTransformMath.CreateRotationQuaternion(primarySnapshot.Transform.RotationDegrees),
            cameraViewDirection,
            cameraRight,
            cameraUp,
            ObjectSelectionTransformMath.ResolveSelectionGizmoAxisLength(selectedObjects, boundsSnapshots),
            CurrentBoundsOverlaySpace == BoundsOverlaySpace.World,
            CanUseScaleGizmo(selectedObjects));
        return true;
    }

    private static void ResolveCameraOrientation(
        Matrix4x4 viewMatrix,
        Vector3 objectPosition,
        out Vector3? cameraViewDirection,
        out Vector3? cameraRight,
        out Vector3? cameraUp)
    {
        cameraViewDirection = null;
        cameraRight = null;
        cameraUp = null;

        if (!Matrix4x4.Invert(viewMatrix, out var cameraWorld))
        {
            return;
        }

        var toCamera = cameraWorld.Translation - objectPosition;
        if (ObjectMathUtility.TryNormalize(toCamera, out var normalizedViewDirection))
        {
            cameraViewDirection = normalizedViewDirection;
        }

        var right = Vector3.TransformNormal(Vector3.UnitX, cameraWorld);
        if (ObjectMathUtility.TryNormalize(right, out var normalizedRight))
        {
            cameraRight = normalizedRight;
        }

        var up = Vector3.TransformNormal(Vector3.UnitY, cameraWorld);
        if (ObjectMathUtility.TryNormalize(up, out var normalizedUp))
        {
            cameraUp = normalizedUp;
        }
    }

    private void DrawGizmoFrame(in GizmoFrame frame)
    {
        switch (Mode)
        {
            case GizmoTransformMode.Translation:
                DrawLinearGizmo(frame, GizmoTransformMode.Translation);
                break;
            case GizmoTransformMode.Rotation:
                DrawRotationGizmo(frame);
                break;
            case GizmoTransformMode.Scale:
                if (frame.Context.ScaleSupported)
                {
                    DrawLinearGizmo(frame, GizmoTransformMode.Scale);
                }
                break;
        }
    }

    public bool IsSelectionBlocked(
        IReadOnlyList<ObjectSnapshot> activeSelectedObjects,
        IReadOnlyList<ObjectBoundsSnapshot> boundsSnapshots,
        Vector2 mousePos)
        => TryGetSelectionBlockFrame(activeSelectedObjects, boundsSnapshots, mousePos, out var frame)
            && frame.BlocksSelection;

    private bool TryGetSelectionBlockFrame(
        IReadOnlyList<ObjectSnapshot> activeSelectedObjects,
        IReadOnlyList<ObjectBoundsSnapshot> boundsSnapshots,
        Vector2 mousePos,
        out GizmoFrame frame)
    {
        frame = default;
        if (activeSelectedObjects.Count == 0
            || Mode == GizmoTransformMode.None
            || HasActiveTransformDrag
            || SurfaceDragState.IsDragging
            || IsGizmoWheelOpen())
        {
            return false;
        }

        return TryBuildCurrentFrame(activeSelectedObjects, boundsSnapshots, mousePos, out frame);
    }

    private void DrawLinearGizmo(in GizmoFrame frame, GizmoTransformMode mode)
    {
        var context = frame.Context;
        var drawList = ImGui.GetForegroundDrawList();
        var batch = _drawManager.BeginPass(DrawPassKind.Gizmo, "Gizmo", DrawLayer.Foreground);
        var scale = ImGuiHelpers.GlobalScale;
        var axisCount = frame.AxisCount;
        var interaction = frame.LinearInteraction;
        var common = interaction.Common;
        if (common.ShouldCaptureMouse)
        {
            ImGui.SetNextFrameWantCaptureMouse(true);
        }

        using var alpha = ImRaii.PushStyle(ImGuiStyleVar.Alpha, ClampGizmoAlpha(ImGui.GetStyle().Alpha * ResolveGizmoAlpha(common.IsFocused)));

        if (mode == GizmoTransformMode.Translation || common.SurfaceDragActive)
        {
            var preferredAxis = mode == GizmoTransformMode.Translation
                ? common.DragActive
                    ? common.ActiveAxis
                    : interaction.HoveredAxis
                : GizmoAxis.None;
            DrawTranslationSnapGrid(context, scale, preferredAxis);
        }

        var centerGlowRadius = GizmoConstants.CenterPointRadius * GizmoConstants.CenterGlowRadiusMultiplier * scale;
        batch.AddScreenCircleFilled(
            context.ScreenPos,
            centerGlowRadius,
            EditorColors.Color(0f, 0f, 0f, GizmoConstants.CenterGlowOpacity),
            64);

        if (common.DragActive && mode == GizmoTransformMode.Translation)
        {
            for (var index = 0; index < axisCount; ++index)
            {
                var suppressedState = AxisVisualStates[index];
                if (suppressedState.Axis == common.ActiveAxis)
                {
                    continue;
                }

                DrawSuppressedGizmoAxis(batch, suppressedState);
            }
        }

        for (var index = 0; index < axisCount; ++index)
        {
            var state = AxisVisualStates[index];
            if (common.DragActive && state.Axis != common.ActiveAxis)
            {
                continue;
            }

            var isActive = common.DragActive && state.Axis == common.ActiveAxis;
            var isHovered = common.Phase == GizmoInteractionPhase.HoverAxis
                            && state.Axis == interaction.HoveredAxis;

            var axisColor = common.DragActive && isActive
                ? EditorColors.GizmoTranslationDragActive
                : GetAxisColorVector(state.Axis, isActive, isHovered);
            var glowColor = common.DragActive && isActive
                ? EditorColors.Color(axisColor.X, axisColor.Y, axisColor.Z, 0.45f)
                : GetAxisGlowColorVector(state.Axis, isActive);
            var visualScale = state.VisualScale;
            var trimmedEnd = mode == GizmoTransformMode.Translation
                ? GetTrimmedGizmoEndpoint(state, visualScale)
                : state.ScreenEnd;

            batch.AddScreenLine(
                state.ScreenStart,
                trimmedEnd,
                glowColor,
                GizmoConstants.AxisLineThickness * visualScale * GizmoConstants.AxisGlowThicknessMultiplier);
            batch.AddScreenLine(
                state.ScreenStart,
                trimmedEnd,
                axisColor,
                GizmoConstants.AxisLineThickness * visualScale);

            if (mode == GizmoTransformMode.Scale)
            {
                DrawScaleHandle(batch, state.ScreenEnd, visualScale, axisColor, isActive, isHovered);
            }
            else
            {
                DrawAxisArrowhead(batch, state, visualScale, axisColor);
            }

            DrawAxisLabel(drawList, state, visualScale, axisColor, AxisLabel(state.Axis), isActive, isHovered);
        }

        DrawCommonModeElements(batch, drawList, context, scale, common.CenterHovered, common.SurfaceDragActive);

        if (interaction.CanStartAxisDrag || common.CanStartSurfaceDrag)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        }

        if (!TryStartSurfaceDragIfRequested(context, common.CanStartSurfaceDrag)
            && interaction.CanStartAxisDrag
            && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            ImGui.SetNextFrameWantCaptureMouse(true);
            BeginLinearGizmoDrag(context, interaction.HoveredAxis, interaction.HoveredAxisState, mode);
        }

        if (HandleGizmoDragLifecycle(context, mode) && mode == GizmoTransformMode.Translation)
        {
            DrawTranslationDragPath(context, scale);
        }

        HandleCommonModeTail(context, common.PointerInRegion, interaction.AxisHovered, common.CenterHovered, common.SurfaceDragActive);
        _drawManager.DrawLayer(context.ViewportPos, context.ViewportSize, DrawLayer.Foreground, ImGui.GetStyle().Alpha);
    }

    private void DrawRotationGizmo(in GizmoFrame frame)
    {
        var context = frame.Context;
        var drawList = ImGui.GetForegroundDrawList();
        var batch = _drawManager.BeginPass(DrawPassKind.Gizmo, "Gizmo", DrawLayer.Foreground);
        var scale = ImGuiHelpers.GlobalScale;
        var rotationProjection = frame.RotationProjection;
        var interaction = frame.RotationInteraction;
        var common = interaction.Common;
        if (common.ShouldCaptureMouse)
        {
            ImGui.SetNextFrameWantCaptureMouse(true);
        }

        using var alpha = ImRaii.PushStyle(ImGuiStyleVar.Alpha, ClampGizmoAlpha(ImGui.GetStyle().Alpha * ResolveGizmoAlpha(common.IsFocused)));

        if (common.SurfaceDragActive)
        {
            DrawTranslationSnapGrid(context, scale, GizmoAxis.None);
        }

        DrawRotationAxes(batch, rotationProjection, scale, common, interaction.HoverState, RotationAxisSegmentPass.Hidden);

        var radius = rotationProjection.VisualRadius + (GizmoConstants.RotationRingThickness * scale);
        batch.AddScreenCircleFilled(
            context.ScreenPos,
            radius,
            EditorColors.Color(0f, 0f, 0f, GizmoConstants.RotationBackgroundAlpha),
            96);

        DrawRotationAxes(batch, rotationProjection, scale, common, interaction.HoverState, RotationAxisSegmentPass.Visible);

        if (common.DragActive)
        {
            DrawRotationDragHighlight(batch, rotationProjection, scale);
        }

        var snapPolicy = ResolveActiveTransformSnapPolicy(context);
        if (snapPolicy.RotationEnabled && snapPolicy.RotationStepDegrees > 0f)
        {
            var tickAxis = common.DragActive
                ? common.ActiveAxis
                : common.Phase == GizmoInteractionPhase.HoverAxis
                    ? interaction.HoverState.Axis
                    : GizmoAxis.None;
            if (tickAxis != GizmoAxis.None)
            {
                var tickAngleOffset = common.DragActive && RotationDragState.RotationDragStartAngle.HasValue
                    ? RotationDragState.RotationDragStartAngle.Value
                    : 0f;
                DrawRotationSnapTicks(batch, rotationProjection, scale, tickAxis, snapPolicy.RotationStepDegrees, tickAngleOffset);
            }
        }

        if (common.Phase == GizmoInteractionPhase.HoverAxis && interaction.HoverState.HasPoint)
        {
            batch.AddScreenCircle(
                interaction.HoverState.ScreenPoint,
                GizmoConstants.RotationHoverIndicatorRadius * scale,
                GetAxisColorVector(interaction.HoverState.Axis, false, true),
                GizmoConstants.RotationHoverIndicatorThickness * scale,
                48);
        }
        else if (common.DragActive && RotationDragState.RotationDragStartAngle.HasValue)
        {
            var axisDirection = ResolveAxisWorldDirection(RotationDragState.ActiveAxis, rotationProjection.Rotation, rotationProjection.UseWorldSpace);
            var dragIndicator = GizmoRotationMath.ProjectAxisPoint(
                CreateRotationMathProjection(rotationProjection),
                axisDirection,
                GizmoRotationMath.NormalizeAngle(RotationDragState.RotationDragStartAngle.Value));
            batch.AddScreenCircleFilled(
                dragIndicator,
                GizmoConstants.RotationDragIndicatorRadius * scale,
                GetAxisColorVector(RotationDragState.ActiveAxis, true, false),
                48);
        }

        DrawCommonModeElements(batch, drawList, context, scale, common.CenterHovered, common.SurfaceDragActive);

        if (interaction.CanStartRotationDrag || common.CanStartSurfaceDrag)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        }

        if (!TryStartSurfaceDragIfRequested(context, common.CanStartSurfaceDrag)
            && interaction.CanStartRotationDrag
            && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            ImGui.SetNextFrameWantCaptureMouse(true);
            BeginRotationGizmoDrag(context, interaction.HoverState.Axis, interaction.HoverState, rotationProjection);
        }

        HandleGizmoDragLifecycle(context, GizmoTransformMode.Rotation);
        HandleCommonModeTail(context, common.PointerInRegion, interaction.AxisHovered, common.CenterHovered, common.SurfaceDragActive);
        _drawManager.DrawLayer(context.ViewportPos, context.ViewportSize, DrawLayer.Foreground, ImGui.GetStyle().Alpha);
    }

    private void DrawCommonModeElements(
        DrawBatch batch,
        ImDrawListPtr drawList,
        in GizmoContext context,
        float scale,
        bool centerHovered,
        bool surfaceDragActive)
    {
        DrawCircularCenterHandle(batch, context.ScreenPos, scale, centerHovered, surfaceDragActive, SurfaceAlignToNormal);
        DrawGizmoLabel(drawList, context, scale);
        if (centerHovered)
        {
            ImGui.SetTooltip(GizmoConstants.SurfaceDragTooltip);
        }
    }

    private bool TryStartSurfaceDragIfRequested(in GizmoContext context, bool canStartSurfaceDrag)
    {
        if (!canStartSurfaceDrag || !ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            return false;
        }

        ImGui.SetNextFrameWantCaptureMouse(true);
        BeginGizmoSurfaceDrag(context);
        return true;
    }

    private void HandleCommonModeTail(
        in GizmoContext context,
        bool pointerInRegion,
        bool hasHoveredModeTarget,
        bool centerHovered,
        bool surfaceDragActive)
    {
        HandleGizmoSurfaceDragLifecycle(context);
        HandleGizmoRadialInput(pointerInRegion || hasHoveredModeTarget || centerHovered || surfaceDragActive);
    }

    private bool IsGizmoWheelOpen()
        => ImGui.IsPopupOpen(GizmoConstants.WheelPopupId);

    private bool IsGizmoDragActive(Guid objectId, GizmoTransformMode mode)
        => mode switch
        {
            GizmoTransformMode.Translation => TranslationDragState.Matches(objectId, mode),
            GizmoTransformMode.Rotation => RotationDragState.Matches(objectId, mode),
            GizmoTransformMode.Scale => ScaleDragState.Matches(objectId, mode),
            _ => false,
        };

    private bool TryGetMatchingTransformDragState(
        Guid objectId,
        GizmoTransformMode mode,
        [NotNullWhen(true)] out GizmoTransformDragSession? dragState)
    {
        if (TryGetActiveTransformDragState(out dragState) && dragState.Matches(objectId, mode))
        {
            return true;
        }

        dragState = null;
        return false;
    }

    private bool IsGizmoSurfaceDragActive(Guid objectId)
        => SurfaceDragState.IsDragging
           && SurfaceDragState.ObjectId == objectId;

    private static bool ResolveTransformSnapActive(bool alwaysEnabled)
        => alwaysEnabled
            ? !GizmoInputUtility.IsPrecisionSnapModifierActive()
            : GizmoInputUtility.IsPrecisionSnapModifierActive();

    private GizmoTransformSnapPolicy ResolveActiveTransformSnapPolicy(in GizmoContext context)
    {
        if (TranslationDragState.Matches(context.PrimarySnapshot.Id, GizmoTransformMode.Translation))
        {
            return CreateTransformSnapPolicy(context.PivotPosition, ResolveTranslationDragSnapBasis());
        }

        if (SurfaceDragState.Matches(context.PrimarySnapshot.Id))
        {
            return CreateTransformSnapPolicy(context.PivotPosition, ResolveCurrentSurfaceDragSnapBasis());
        }

        return CreateTransformSnapPolicy(context.PivotPosition, ResolvePreviewPositionSnapBasis(context));
    }

    private GizmoTransformSnapPolicy ResolveTranslationDragSnapPolicy(Vector3 referencePosition)
        => CreateTransformSnapPolicy(referencePosition, ResolveTranslationDragSnapBasis());

    private GizmoTransformSnapPolicy ResolveSurfaceDragSnapPolicy(Vector3 referencePosition)
        => CreateTransformSnapPolicy(referencePosition, ResolveSurfaceDragSnapBasis());

    private GizmoTransformSnapPolicy CreateTransformSnapPolicy(Vector3 referencePosition, in ObjectSnapBasis positionBasis)
    {
        var snapSettings = Settings.TransformSnapSettings;
        var positionEnabled = snapSettings.PositionDragEnabled && ResolveTransformSnapActive(snapSettings.PositionEnabled);
        return new(
            new GizmoPositionSnapPolicy(positionEnabled, snapSettings.PositionStep, positionBasis, referencePosition),
            ResolveTransformSnapActive(snapSettings.RotationEnabled),
            snapSettings.RotationStepDegrees,
            ResolveTransformSnapActive(snapSettings.ScaleEnabled),
            snapSettings.ScaleStep);
    }

    private void ToggleBoundsOverlayEnabled()
        => Settings.BoundsInteractionSettings = Settings.BoundsInteractionSettings with
        {
            BoundsEnabled = !Settings.BoundsInteractionSettings.BoundsEnabled,
        };

    private static ObjectSnapBasis WorldTransformSnapBasis
        => GizmoSnapBasisUtility.World;

    private static ObjectSnapBasis CreateLocalTransformSnapBasis(Quaternion rotation)
        => GizmoSnapBasisUtility.CreateLocal(rotation);

    private static ObjectSnapBasis ResolvePreviewPositionSnapBasis(in GizmoContext context)
        => context.UseWorldSpace
            ? WorldTransformSnapBasis
            : CreateLocalTransformSnapBasis(context.Rotation);

    private ObjectSnapBasis ResolveTranslationDragSnapBasis()
        => TranslationDragState.UseWorldSpace
            ? WorldTransformSnapBasis
            : CreateLocalTransformSnapBasis(TranslationDragState.StartRotationQuaternion);

    private ObjectSnapBasis ResolveCurrentSurfaceDragSnapBasis()
        => CurrentBoundsOverlaySpace == BoundsOverlaySpace.World
            ? WorldTransformSnapBasis
            : CreateLocalTransformSnapBasis(ObjectTransformMath.CreateRotationQuaternion(SurfaceDragState.LastResolvedRotationDegrees));

    private ObjectSnapBasis ResolveSurfaceDragSnapBasis()
        => !SurfaceDragState.IsDragging
            ? CurrentBoundsOverlaySpace == BoundsOverlaySpace.World
                ? WorldTransformSnapBasis
                : CreateLocalTransformSnapBasis(SurfaceDragState.StartRotationQuaternion)
            : ResolveCurrentSurfaceDragSnapBasis();

}

