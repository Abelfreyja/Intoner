using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Intoner.Objects.Models;
using Intoner.Objects.Rendering.Drawing;
using Intoner.Objects.UI.Services;
using Intoner.Services.Input;
using Intoner.Objects.Utils;
using Intoner.Scene;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;

namespace Intoner.Objects.UI;

/// <summary> host callbacks and state the gizmo needs from the editor </summary>
internal interface IGizmoHost
{
    /// <summary> gets the current editor selection revision </summary>
    int GetSelectionRevision();

    /// <summary> gets the current scene revision </summary>
    long GetSceneRevision();

    /// <summary> captures current editor selection ids for history entries </summary>
    Guid[] CaptureCurrentSelectionIds();

    /// <summary> commits pending edits and validates history before a gizmo mutation starts </summary>
    void PrepareHistoryMutation();

    /// <summary> records a completed history action after a gizmo drag finishes </summary>
    bool TryRecordCompletedHistoryAction(
        SceneHistoryKind kind,
        string title,
        IReadOnlyList<SceneItemSnapshot> beforeSnapshots,
        IReadOnlyList<SceneItemSnapshot> afterSnapshots,
        IReadOnlyList<Guid>? selectionAfterApply,
        IReadOnlyList<Guid>? selectionAfterRevert);

    /// <summary> duplicates the provided selected items through the normal history path </summary>
    bool TryDuplicateSelectedItems(IReadOnlyList<SceneItemSnapshot> selectedSnapshots);

    /// <summary> removes the provided selected items through the normal history path </summary>
    bool TryRemoveSelectedItems(IReadOnlyList<SceneItemSnapshot> selectedSnapshots);

    /// <summary> moves one selected item to the player through the normal history path </summary>
    bool TryMoveItemToPlayerWithHistory(Guid itemId);

    /// <summary> applies a selected item update through the normal history path </summary>
    bool TryApplySelectedSnapshotUpdateWithHistory(
        SceneHistoryKind kind,
        string title,
        IReadOnlyList<SceneItemSnapshot> selectedSnapshots,
        Func<SceneItemSnapshot, SceneItemSnapshot> updateFactory);
}

internal sealed partial class Gizmo : IDisposable
{
    private readonly IGizmoHost _host;
    private readonly IKeyboardInputService _keyboardInput;
    private readonly ISceneItemService _sceneItemService;
    private readonly ISceneSurfaceService _surfaceService;
    private readonly DrawManager _drawManager;
    private IReadOnlyList<SceneItemBoundsSnapshot>? _boundsLookupSource;
    private SceneItemBoundsLookup _boundsLookup = new([]);

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

    private IKeyboardInputLease? SurfaceDragKeyboardInputLease
    {
        get => State.SurfaceDragKeyboardInputLease;
        set => State.SurfaceDragKeyboardInputLease = value;
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
        IKeyboardInputService keyboardInput,
        ISceneItemService sceneItemService,
        ISceneSurfaceService surfaceService)
    {
        _host = host;
        _drawManager = drawManager;
        _keyboardInput = keyboardInput;
        _sceneItemService = sceneItemService;
        _surfaceService = surfaceService;
    }

    public void Dispose()
        => DisposeSurfaceDragKeyboardInputLease();

    public void NormalizeMode(IReadOnlyList<SceneItemSnapshot> selectedItems)
    {
        if (!CanUseScaleGizmo(selectedItems) && Mode == GizmoTransformMode.Scale)
        {
            Mode = GizmoTransformMode.Rotation;
        }

        if (selectedItems.Count == 0 && HasActiveTransformDrag)
        {
            CompleteGizmoDrag();
        }

        if (selectedItems.Count == 0 && SurfaceDragState.IsDragging)
        {
            CompleteGizmoSurfaceDrag();
        }
    }

    public void CancelInteractions()
    {
        CompleteGizmoDrag();
        CompleteGizmoSurfaceDrag();
    }

    internal bool CanUseScaleGizmo(SceneItemSnapshot snapshot)
        => TryGetManipulation(snapshot, out _, out SceneItemManipulation manipulation)
           && manipulation.SupportsScale;

    internal bool CanUseScaleGizmo(IReadOnlyList<SceneItemSnapshot> selectedItems)
        => selectedItems.Count == 1 && CanUseScaleGizmo(selectedItems[0]);

    public void Draw(
        IReadOnlyList<SceneItemSnapshot> selectedItems,
        IReadOnlyList<SceneItemBoundsSnapshot> boundsSnapshots,
        in GizmoDrawOptions options)
    {
        if (!TryBuildCurrentFrame(selectedItems, boundsSnapshots, ImGui.GetIO().MousePos, options, out var frame))
        {
            return;
        }

        try
        {
            DrawGizmoFrame(frame, options);
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
        IReadOnlyList<SceneItemSnapshot> selectedItems,
        IReadOnlyList<SceneItemBoundsSnapshot> boundsSnapshots,
        Vector2 mousePos,
        in GizmoDrawOptions options,
        out GizmoFrame frame)
    {
        bool pointerAvailable = options.CanUsePointer(mousePos);
        var request = CreateFrameRequest(mousePos, pointerAvailable);
        if (State.TryGetCachedFrame(request, out frame))
        {
            return true;
        }

        frame = default;
        if (!TryPrepareGizmoContext(selectedItems, boundsSnapshots, out var context))
        {
            return false;
        }

        var scale = ImGuiHelpers.GlobalScale;
        if (Mode == GizmoTransformMode.Rotation)
        {
            var radius = ResolveWorldScaledScreenSize(GizmoConstants.RotationRingBaseRadius, context.AxisWorldLength, scale);
            var rotationProjection = IsGizmoDragActive(context.PrimarySnapshot.Id, GizmoTransformMode.Rotation) && RotationDragState.RotationProjection.HasValue
                ? RotationDragState.RotationProjection.Value
                : CreateRotationProjectionContext(context, radius);
            frame = new GizmoFrame(
                context,
                rotationProjection,
                ResolveRotationInteractionState(context, rotationProjection, mousePos, scale, pointerAvailable));
            State.StoreCachedFrame(request, frame);
            return true;
        }

        var worldSpace = Mode == GizmoTransformMode.Translation && context.UseWorldSpace;
        var axisCount = BuildLinearGizmoAxisVisualStates(context, worldSpace);
        if (axisCount <= 0)
        {
            return false;
        }

        frame = new GizmoFrame(
            context,
            axisCount,
            ResolveLinearInteractionState(context, Mode, axisCount, mousePos, scale, pointerAvailable));
        State.StoreCachedFrame(request, frame);
        return true;
    }

    private GizmoFrameRequest CreateFrameRequest(Vector2 mousePos, bool pointerAvailable)
        => new(
            ImGui.GetFrameCount(),
            State.InteractionRevision,
            Mode,
            CurrentBoundsOverlaySpace,
            _host.GetSelectionRevision(),
            _host.GetSceneRevision(),
            mousePos,
            pointerAvailable);

    private bool TryPrepareGizmoContext(
        IReadOnlyList<SceneItemSnapshot> selectedItems,
        IReadOnlyList<SceneItemBoundsSnapshot> boundsSnapshots,
        out GizmoContext context)
    {
        if (!TryBuildGizmoContext(selectedItems, boundsSnapshots, out context))
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

        if (SurfaceDragState.IsDragging && SurfaceDragState.ItemId != context.PrimarySnapshot.Id)
        {
            CompleteGizmoSurfaceDrag();
        }
    }

    private bool TryBuildGizmoContext(
        IReadOnlyList<SceneItemSnapshot> selectedItems,
        IReadOnlyList<SceneItemBoundsSnapshot> boundsSnapshots,
        out GizmoContext context)
    {
        context = default;

        if (selectedItems.Count == 0 || Mode == GizmoTransformMode.None)
        {
            return false;
        }

        if (!SceneViewportProjection.TryGetEditorCameraProjection(out var viewProjection, out var viewMatrix, out _))
        {
            return false;
        }

        var primarySnapshot = selectedItems[^1];
        if (!TryGetManipulation(
                primarySnapshot,
                out SceneItemManipulationPolicy? manipulation,
                out SceneItemManipulation manipulationOptions))
        {
            return false;
        }

        SceneItemBoundsLookup boundsLookup = GetBoundsLookup(boundsSnapshots);
        var pivotPosition = SceneSelectionTransformMath.ResolveSelectionPivotPosition(selectedItems, boundsLookup);
        var viewport = ImGui.GetMainViewport();
        if (!SceneViewportProjection.TryProjectWorldPointToViewport(
                viewProjection,
                pivotPosition,
                viewport.Pos,
                viewport.Size,
                out var screenPos))
        {
            return false;
        }
        ResolveCameraOrientation(viewMatrix, pivotPosition, out var cameraViewDirection, out var cameraRight, out var cameraUp);
        ResolveSelectionManipulation(
            selectedItems,
            boundsLookup,
            manipulationOptions,
            out float axisWorldLength,
            out bool surfaceDragSupported);

        var boundsSnapshot = selectedItems.Count == 1
            ? SceneSelectionTransformMath.FindManipulationBoundsSnapshot(primarySnapshot, boundsLookup)
            : null;
        context = new GizmoContext(
            selectedItems,
            primarySnapshot,
            boundsSnapshots,
            boundsLookup,
            boundsSnapshot,
            manipulation,
            manipulationOptions,
            pivotPosition,
            screenPos,
            viewport.Pos,
            viewport.Size,
            viewProjection,
            SceneTransformMath.CreateRotationQuaternion(primarySnapshot.Transform.RotationDegrees),
            cameraViewDirection,
            cameraRight,
            cameraUp,
            axisWorldLength,
            CurrentBoundsOverlaySpace == BoundsOverlaySpace.World,
            selectedItems.Count == 1 && manipulationOptions.SupportsScale,
            surfaceDragSupported);
        return true;
    }

    private bool TryGetManipulation(
        SceneItemSnapshot snapshot,
        [NotNullWhen(true)] out SceneItemManipulationPolicy? policy,
        out SceneItemManipulation manipulation)
    {
        if (_sceneItemService.TryGetManipulation(snapshot, out SceneItemManipulationPolicy resolvedPolicy))
        {
            policy = resolvedPolicy;
            manipulation = resolvedPolicy.Describe(snapshot);
            return true;
        }

        policy = null;
        manipulation = SceneItemManipulation.Default;
        return false;
    }

    private void ResolveSelectionManipulation(
        IReadOnlyList<SceneItemSnapshot> selectedItems,
        SceneItemBoundsLookup boundsLookup,
        SceneItemManipulation primaryManipulation,
        out float axisLength,
        out bool surfaceDragSupported)
    {
        if (selectedItems.Count == 1)
        {
            axisLength = SceneSelectionTransformMath.ResolveItemGizmoAxisLength(
                selectedItems[0],
                boundsLookup,
                primaryManipulation.GizmoAxisLength);
            surfaceDragSupported = primaryManipulation.SupportsSurfaceDrag;
            return;
        }

        axisLength = SceneSelectionTransformMath.ResolveSelectionGizmoAxisLength(selectedItems, boundsLookup);
        surfaceDragSupported = primaryManipulation.SupportsSurfaceDrag;
        IncludePreferredAxisLength(primaryManipulation, ref axisLength);
        for (int index = 0; index < selectedItems.Count - 1; ++index)
        {
            if (!TryGetManipulation(selectedItems[index], out _, out SceneItemManipulation manipulation))
            {
                surfaceDragSupported = false;
                continue;
            }

            surfaceDragSupported &= manipulation.SupportsSurfaceDrag;
            IncludePreferredAxisLength(manipulation, ref axisLength);
        }

        axisLength = Math.Clamp(axisLength, 0.25f, 4f);
    }

    private SceneItemBoundsLookup GetBoundsLookup(IReadOnlyList<SceneItemBoundsSnapshot> boundsSnapshots)
    {
        if (ReferenceEquals(_boundsLookupSource, boundsSnapshots))
        {
            return _boundsLookup;
        }

        _boundsLookupSource = boundsSnapshots;
        _boundsLookup = new SceneItemBoundsLookup(boundsSnapshots);
        return _boundsLookup;
    }

    private static void IncludePreferredAxisLength(SceneItemManipulation manipulation, ref float axisLength)
    {
        if (manipulation.GizmoAxisLength is float preferredLength
            && preferredLength > 0f
            && float.IsFinite(preferredLength))
        {
            axisLength = MathF.Max(axisLength, preferredLength);
        }
    }

    private static void ResolveCameraOrientation(
        Matrix4x4 viewMatrix,
        Vector3 itemPosition,
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

        var toCamera = cameraWorld.Translation - itemPosition;
        if (NumericsUtility.TryNormalize(toCamera, out var normalizedViewDirection))
        {
            cameraViewDirection = normalizedViewDirection;
        }

        var right = Vector3.TransformNormal(Vector3.UnitX, cameraWorld);
        if (NumericsUtility.TryNormalize(right, out var normalizedRight))
        {
            cameraRight = normalizedRight;
        }

        var up = Vector3.TransformNormal(Vector3.UnitY, cameraWorld);
        if (NumericsUtility.TryNormalize(up, out var normalizedUp))
        {
            cameraUp = normalizedUp;
        }
    }

    private void DrawGizmoFrame(in GizmoFrame frame, in GizmoDrawOptions options)
    {
        switch (Mode)
        {
            case GizmoTransformMode.Translation:
                DrawLinearGizmo(frame, GizmoTransformMode.Translation, options);
                break;
            case GizmoTransformMode.Rotation:
                DrawRotationGizmo(frame, options);
                break;
            case GizmoTransformMode.Scale:
                if (frame.Context.ScaleSupported)
                {
                    DrawLinearGizmo(frame, GizmoTransformMode.Scale, options);
                }
                break;
        }
    }

    public bool IsSelectionBlocked(
        IReadOnlyList<SceneItemSnapshot> activeSelectedItems,
        IReadOnlyList<SceneItemBoundsSnapshot> boundsSnapshots,
        Vector2 mousePos)
        => TryGetSelectionBlockFrame(activeSelectedItems, boundsSnapshots, mousePos, out var frame)
            && frame.BlocksSelection;

    private bool TryGetSelectionBlockFrame(
        IReadOnlyList<SceneItemSnapshot> activeSelectedItems,
        IReadOnlyList<SceneItemBoundsSnapshot> boundsSnapshots,
        Vector2 mousePos,
        out GizmoFrame frame)
    {
        frame = default;
        if (activeSelectedItems.Count == 0
            || Mode == GizmoTransformMode.None
            || HasActiveTransformDrag
            || SurfaceDragState.IsDragging
            || IsGizmoWheelOpen())
        {
            return false;
        }

        return TryBuildCurrentFrame(
            activeSelectedItems,
            boundsSnapshots,
            mousePos,
            GizmoDrawOptions.Unobstructed,
            out frame);
    }

    private void DrawLinearGizmo(
        in GizmoFrame frame,
        GizmoTransformMode mode,
        in GizmoDrawOptions options)
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
            GizmoAxis preferredAxis = GizmoAxis.None;
            if (mode == GizmoTransformMode.Translation)
            {
                preferredAxis = common.DragActive
                    ? common.ActiveAxis
                    : interaction.HoveredAxis;
            }

            DrawTranslationSnapGrid(context, scale, preferredAxis);
        }

        var centerGlowRadius = GizmoConstants.CenterPointRadius * GizmoConstants.CenterGlowRadiusMultiplier * scale;
        batch.AddScreenCircleFilled(
            context.ScreenPos,
            centerGlowRadius,
            ThemeColors.Color(0f, 0f, 0f, GizmoConstants.CenterGlowOpacity),
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
                ? ThemeColors.Color(axisColor.X, axisColor.Y, axisColor.Z, 0.45f)
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

            DrawAxisLabel(
                drawList,
                state,
                visualScale,
                axisColor,
                AxisLabel(state.Axis),
                isActive,
                isHovered,
                options);
        }

        DrawCommonModeElements(batch, drawList, context, scale, common.CenterHovered, common.SurfaceDragActive, options);

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

        if (HandleGizmoDragLifecycle(context, mode)
            && mode == GizmoTransformMode.Translation)
        {
            DrawTranslationDragPath(context, scale);
        }

        HandleCommonModeTail(context, common.PointerInRegion, interaction.AxisHovered, common.CenterHovered, common.SurfaceDragActive);

        _drawManager.DrawLayer(context.ViewportPos, context.ViewportSize, DrawLayer.Foreground, ImGui.GetStyle().Alpha);
    }

    private void DrawRotationGizmo(in GizmoFrame frame, in GizmoDrawOptions options)
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
            ThemeColors.Color(0f, 0f, 0f, GizmoConstants.RotationBackgroundAlpha),
            96);

        DrawRotationAxes(batch, rotationProjection, scale, common, interaction.HoverState, RotationAxisSegmentPass.Visible);

        if (common.DragActive)
        {
            DrawRotationDragHighlight(batch, rotationProjection, scale);
        }

        var snapPolicy = ResolveActiveTransformSnapPolicy(context);
        if (snapPolicy.RotationEnabled && snapPolicy.RotationStepDegrees > 0f)
        {
            GizmoAxis tickAxis = GizmoAxis.None;
            if (common.DragActive)
            {
                tickAxis = common.ActiveAxis;
            }
            else if (common.Phase == GizmoInteractionPhase.HoverAxis)
            {
                tickAxis = interaction.HoverState.Axis;
            }

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

        DrawCommonModeElements(batch, drawList, context, scale, common.CenterHovered, common.SurfaceDragActive, options);

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
        bool surfaceDragActive,
        in GizmoDrawOptions options)
    {
        DrawCircularCenterHandle(batch, context.ScreenPos, scale, centerHovered, surfaceDragActive, SurfaceAlignToNormal);
        if (!centerHovered)
        {
            DrawGizmoLabel(drawList, context, scale, options);
        }

        if (centerHovered)
        {
            IntonerTooltip.DrawText(GizmoConstants.SurfaceDragTooltip);
        }
    }

    private bool TryStartSurfaceDragIfRequested(in GizmoContext context, bool canStartSurfaceDrag)
    {
        if (!canStartSurfaceDrag
            || !ImGui.IsMouseClicked(ImGuiMouseButton.Left))
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

    private static bool IsGizmoWheelOpen()
        => ImGui.IsPopupOpen(GizmoConstants.WheelPopupId);

    private bool IsGizmoDragActive(Guid itemId, GizmoTransformMode mode)
        => mode switch
        {
            GizmoTransformMode.Translation => TranslationDragState.Matches(itemId, mode),
            GizmoTransformMode.Rotation => RotationDragState.Matches(itemId, mode),
            GizmoTransformMode.Scale => ScaleDragState.Matches(itemId, mode),
            _ => false,
        };

    private bool TryGetMatchingTransformDragState(
        Guid itemId,
        GizmoTransformMode mode,
        [NotNullWhen(true)] out GizmoTransformDragSession? dragState)
    {
        if (TryGetActiveTransformDragState(out dragState) && dragState.Matches(itemId, mode))
        {
            return true;
        }

        dragState = null;
        return false;
    }

    private bool IsGizmoSurfaceDragActive(Guid itemId)
        => SurfaceDragState.IsDragging
           && SurfaceDragState.ItemId == itemId;

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

    private GizmoTransformSnapPolicy CreateTransformSnapPolicy(Vector3 referencePosition, in SceneSnapBasis positionBasis)
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

    private static SceneSnapBasis WorldTransformSnapBasis
        => GizmoSnapBasisUtility.World;

    private static SceneSnapBasis CreateLocalTransformSnapBasis(Quaternion rotation)
        => GizmoSnapBasisUtility.CreateLocal(rotation);

    private static SceneSnapBasis ResolvePreviewPositionSnapBasis(in GizmoContext context)
        => context.UseWorldSpace
            ? WorldTransformSnapBasis
            : CreateLocalTransformSnapBasis(context.Rotation);

    private SceneSnapBasis ResolveTranslationDragSnapBasis()
        => TranslationDragState.UseWorldSpace
            ? WorldTransformSnapBasis
            : CreateLocalTransformSnapBasis(TranslationDragState.StartRotationQuaternion);

    private SceneSnapBasis ResolveCurrentSurfaceDragSnapBasis()
        => CurrentBoundsOverlaySpace == BoundsOverlaySpace.World
            ? WorldTransformSnapBasis
            : CreateLocalTransformSnapBasis(SceneTransformMath.CreateRotationQuaternion(SurfaceDragState.LastResolvedRotationDegrees));

    private SceneSnapBasis ResolveSurfaceDragSnapBasis()
    {
        if (SurfaceDragState.IsDragging)
        {
            return ResolveCurrentSurfaceDragSnapBasis();
        }

        return CurrentBoundsOverlaySpace == BoundsOverlaySpace.World
            ? WorldTransformSnapBasis
            : CreateLocalTransformSnapBasis(SurfaceDragState.StartRotationQuaternion);
    }

}
