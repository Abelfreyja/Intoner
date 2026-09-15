using Intoner.Objects.Utils;
using Intoner.Scene;
using Intoner.Services.Input;
using System.Numerics;

namespace Intoner.Objects.UI;

/// <summary> transient gizmo runtime state for active interaction and drawing </summary>
internal sealed class GizmoState
{
    public GizmoAxisVisualState[] TranslationAxes { get; } = new GizmoAxisVisualState[GizmoAxisUtility.AxisCount];

    public GizmoAxisVisualState[] ScaleAxes { get; } = new GizmoAxisVisualState[GizmoAxisUtility.AxisCount];

    public GizmoAxisProjectionState[] LocalAxisProjections { get; } = new GizmoAxisProjectionState[GizmoAxisUtility.AxisCount];

    public GizmoAxisProjectionState[] WorldAxisProjections { get; } = new GizmoAxisProjectionState[GizmoAxisUtility.AxisCount];

    public int InteractionRevision { get; private set; }

    public GizmoTranslationDragSession TranslationDrag { get; } = new();

    public GizmoRotationDragSession RotationDrag { get; } = new();

    public GizmoScaleDragSession ScaleDrag { get; } = new();

    public GizmoSurfaceDragSession SurfaceDrag { get; } = new();

    public IKeyboardInputLease? SurfaceDragKeyboardInputLease { get; set; }

    public Vector2 WheelCenter { get; private set; }

    public bool WheelSuppressNextToggle { get; private set; }

    public bool RadialActionsPage { get; private set; }

    public GizmoRadialTooltipInfo? PendingRadialTooltip { get; set; }

    private GizmoCachedFrame? CachedFrame { get; set; }

    public void ResetTransformDragSessions()
    {
        TranslationDrag.Reset();
        RotationDrag.Reset();
        ScaleDrag.Reset();
        TouchInteraction();
    }

    private void TouchInteraction()
    {
        InteractionRevision++;
        CachedFrame = null;
    }

    private void InvalidateCachedFrame()
        => CachedFrame = null;

    public void NotifyInteractionStateChanged()
        => TouchInteraction();

    public void NotifyFrameStateChanged()
        => InvalidateCachedFrame();

    public void BeginSurfaceDrag(
        IReadOnlyList<SceneItemSnapshot> selectedSnapshots,
        SceneItemBoundsLookup boundsLookup,
        SceneItemSnapshot primarySnapshot,
        Vector3 pivotPosition,
        IReadOnlySet<Guid> selectedItemIds,
        SceneSurfaceTargetSnapshot surfaceTargets,
        bool itemTargetsEnabled,
        SceneSurfaceTargetShape targetShape)
    {
        SurfaceDrag.Begin(
            selectedSnapshots,
            boundsLookup,
            primarySnapshot,
            pivotPosition,
            selectedItemIds,
            surfaceTargets,
            itemTargetsEnabled,
            targetShape);
        TouchInteraction();
    }

    public void ResetSurfaceDrag()
    {
        SurfaceDrag.Reset();
        TouchInteraction();
    }

    public void OpenRadialMenu(Vector2 wheelCenter)
    {
        WheelCenter = wheelCenter;
        TouchInteraction();
    }

    public void SetRadialActionsPage(bool value)
    {
        if (RadialActionsPage == value)
        {
            return;
        }

        RadialActionsPage = value;
        TouchInteraction();
    }

    public void ToggleRadialActionsPage()
    {
        RadialActionsPage = !RadialActionsPage;
        TouchInteraction();
    }

    public void SetWheelSuppressNextToggle(bool value)
    {
        if (WheelSuppressNextToggle == value)
        {
            return;
        }

        WheelSuppressNextToggle = value;
        TouchInteraction();
    }

    public bool TryGetCachedFrame(in GizmoFrameRequest request, out GizmoFrame frame)
    {
        if (CachedFrame.HasValue && CachedFrame.Value.Matches(request))
        {
            frame = CachedFrame.Value.Frame;
            return true;
        }

        frame = default;
        return false;
    }

    public void StoreCachedFrame(in GizmoFrameRequest request, in GizmoFrame frame)
        => CachedFrame = new GizmoCachedFrame(request, frame);
}

