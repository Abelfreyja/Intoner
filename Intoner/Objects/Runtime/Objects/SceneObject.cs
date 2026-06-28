using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using Intoner.Objects.Interop;
using Intoner.Objects.Models;
using Intoner.Objects.Utils;
using Microsoft.Extensions.Logging;
using System.Numerics;
using AxisAlignedBounds = FFXIVClientStructs.FFXIV.Common.Math.AxisAlignedBounds;
using OrientedBounds = FFXIVClientStructs.FFXIV.Common.Math.OrientedBounds;

namespace Intoner.Objects.Runtime;

/// <summary>
/// Describes the outcome of applying one sanitized snapshot to an active scene object.
/// </summary>
internal enum SceneObjectUpdateResult
{
    /// <summary>
    /// The snapshot was applied in place.
    /// </summary>
    Applied,

    /// <summary>
    /// The snapshot is valid, but this scene object must be recreated to apply it.
    /// </summary>
    RequiresRecreate,

    /// <summary>
    /// The snapshot was rejected and should not be recreated automatically.
    /// </summary>
    Rejected,
}

/// <summary>
/// Represents one active object instance in the game scene.
/// </summary>
internal interface ISceneObject : IDisposable
{
    /// <summary>
    /// Gets the object kind handled by this scene object.
    /// </summary>
    ObjectKind Kind { get; }

    /// <summary>
    /// Gets the current sanitized snapshot applied to this scene object.
    /// </summary>
    ObjectSnapshot Snapshot { get; }

    /// <summary>
    /// Checks whether this scene object needs per frame framework updates.
    /// </summary>
    bool NeedsFrameworkUpdates { get; }

    /// <summary>
    /// Gets the current native address for this scene object.
    /// </summary>
    nint Address { get; }

    /// <summary>
    /// Attempts to apply the given sanitized snapshot to this scene object.
    /// </summary>
    /// <param name="snapshot">The sanitized snapshot to apply.</param>
    /// <returns>
    /// The update result. 'Applied' means the scene object updated in place, 'RequiresRecreate' means the manager should recreate it,
    /// and 'Rejected' means the snapshot should not be recreated automatically.
    /// </returns>
    SceneObjectUpdateResult TryUpdate(ObjectSnapshot snapshot);

    /// <summary>
    /// Applies a collection id only snapshot change while resource materialization is still pending.
    /// </summary>
    /// <param name="snapshot">the sanitized snapshot with the pending collection id</param>
    /// <returns>the update result</returns>
    SceneObjectUpdateResult TryUpdateCollectionAssignment(ObjectSnapshot snapshot);

    /// <summary>
    /// Attempts to reload collection backed resources without changing the stored object snapshot.
    /// </summary>
    /// <param name="snapshot">the desired active snapshot</param>
    /// <returns>the refresh result</returns>
    SceneObjectUpdateResult TryRefreshResources(ObjectSnapshot snapshot);

    /// <summary>
    /// Runs one framework update step for this scene object.
    /// </summary>
    void FrameworkUpdate();

    /// <summary>
    /// Appends one or more GPU selection draws for this scene object.
    /// </summary>
    /// <param name="collector">The collector that receives the selection draws.</param>
    void AppendSelectionDraws(ObjectSelectionCollector collector);

    /// <summary>
    /// Tries to resolve axis aligned bounds for this scene object.
    /// </summary>
    /// <param name="bounds">The resolved bounds when available.</param>
    /// <returns>True when bounds were available.</returns>
    bool TryGetBounds(out AxisAlignedBounds bounds);

    /// <summary>
    /// Tries to resolve oriented bounds for this scene object.
    /// </summary>
    /// <param name="bounds">The resolved oriented bounds when available.</param>
    /// <returns>True when oriented bounds were available.</returns>
    bool TryGetOrientedBounds(out OrientedBounds bounds);

    /// <summary>
    /// Tries to resolve the native clearance radius used by housing floor placement checks.
    /// </summary>
    /// <param name="radius">the resolved clearance radius when available</param>
    /// <returns>true when a clearance radius was available</returns>
    bool TryGetPlacementClearanceRadius(out float radius);

    /// <summary>
    /// gets native placement surfaces exposed by this scene object
    /// </summary>
    ObjectPlacementSurfaceSupport PlacementSurfaceSupport { get; }
}

internal struct DeferredVisualState
{
    private bool _needsReplay;

    public void Reset()
        => _needsReplay = false;

    public SceneObjectUpdateResult Apply<TModel>(
        ObjectSnapshot snapshot,
        ObjectSnapshot previousSnapshot,
        Action<ObjectSnapshot> applyRuntimeState,
        Func<TModel, TModel?, bool> needsVisualState,
        Func<TModel, bool> tryApplyVisualState)
        where TModel : ObjectData
    {
        var model = (TModel)snapshot.Model;
        var previousModel = (TModel)previousSnapshot.Model;

        applyRuntimeState(snapshot);
        if (!needsVisualState(model, previousModel))
        {
            _needsReplay = false;
            return SceneObjectUpdateResult.Applied;
        }

        _needsReplay = !tryApplyVisualState(model);
        return SceneObjectUpdateResult.Applied;
    }

    public void Replay<TModel>(
        ObjectSnapshot snapshot,
        Action<ObjectSnapshot> applyRuntimeState,
        Func<TModel, bool> tryApplyVisualState)
        where TModel : ObjectData
    {
        if (!_needsReplay)
        {
            return;
        }

        var model = (TModel)snapshot.Model;
        applyRuntimeState(snapshot);
        if (!tryApplyVisualState(model))
        {
            return;
        }

        _needsReplay = false;
    }
}

internal abstract class SceneObject : ISceneObject
{
    protected const byte DestroyFlagsFree = 1;

    private bool _disposed;

    protected IFramework Framework { get; }
    protected ILogger Logger { get; }

    public ObjectSnapshot Snapshot { get; protected set; }

    public abstract ObjectKind Kind { get; }
    public virtual bool NeedsFrameworkUpdates => false;
    public virtual ObjectPlacementSurfaceSupport PlacementSurfaceSupport => ObjectPlacementSurfaceSupport.None;
    public abstract nint Address { get; }

    protected SceneObject(IFramework framework, ILogger logger, ObjectSnapshot snapshot)
    {
        Framework = framework;
        Logger = logger;
        Snapshot = snapshot;
    }

    public SceneObjectUpdateResult TryUpdate(ObjectSnapshot snapshot)
        => RunOnFrameworkThread(() => TryUpdateUnsafe(snapshot));

    public SceneObjectUpdateResult TryUpdateCollectionAssignment(ObjectSnapshot snapshot)
        => RunOnFrameworkThread(() => TryUpdateCollectionAssignmentUnsafe(snapshot));

    public SceneObjectUpdateResult TryRefreshResources(ObjectSnapshot snapshot)
        => RunOnFrameworkThread(() => RefreshResourcesUnsafe(snapshot));

    public void FrameworkUpdate()
        => FrameworkUpdateUnsafe();

    public abstract void AppendSelectionDraws(ObjectSelectionCollector collector);

    public abstract bool TryGetBounds(out AxisAlignedBounds bounds);
    public abstract bool TryGetOrientedBounds(out OrientedBounds bounds);
    public virtual bool TryGetPlacementClearanceRadius(out float radius)
    {
        radius = 0f;
        return false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (ObjectFrameworkUtility.TryRunOnFrameworkThread(Framework, DisposeUnsafe))
        {
            return;
        }

        Logger.LogWarning(
            "framework is unloading, skipping scene object destroy for {Kind} 0x{Address:X}",
            Kind,
            (ulong)Address);
    }

    protected virtual SceneObjectUpdateResult ValidateSnapshotUpdate(ObjectSnapshot snapshot)
        => SceneObjectUpdateResult.Applied;

    protected virtual SceneObjectUpdateResult RefreshResourcesUnsafe(ObjectSnapshot snapshot)
        => SceneObjectUpdateResult.Rejected;

    protected abstract SceneObjectUpdateResult ApplySnapshotUnsafe(ObjectSnapshot snapshot, ObjectSnapshot previousSnapshot);

    protected virtual void FrameworkUpdateUnsafe()
    {
    }

    protected abstract void DisposeUnsafe();

    protected T RunOnFrameworkThread<T>(Func<T> func)
        => ObjectFrameworkUtility.RunOnFrameworkThread(Framework, func);

    protected void RunOnFrameworkThread(Action action)
        => ObjectFrameworkUtility.RunOnFrameworkThread(Framework, action);

    protected static Quaternion CreateRotation(Vector3 rotationDegrees)
        => ObjectTransformMath.CreateRotationQuaternion(rotationDegrees);

    private SceneObjectUpdateResult TryUpdateUnsafe(ObjectSnapshot snapshot)
    {
        var validationResult = ValidateSnapshotUpdate(snapshot);
        if (validationResult != SceneObjectUpdateResult.Applied)
        {
            return validationResult;
        }

        var previousSnapshot = Snapshot;
        var applyResult = ApplySnapshotUnsafe(snapshot, previousSnapshot);
        if (applyResult != SceneObjectUpdateResult.Applied)
        {
            return applyResult;
        }

        Snapshot = snapshot;
        return SceneObjectUpdateResult.Applied;
    }

    private SceneObjectUpdateResult TryUpdateCollectionAssignmentUnsafe(ObjectSnapshot snapshot)
    {
        if (!ObjectSnapshotUtility.IsCollectionOnlyChange(Snapshot, snapshot))
        {
            return SceneObjectUpdateResult.Rejected;
        }

        Snapshot = snapshot;
        return SceneObjectUpdateResult.Applied;
    }

    protected static unsafe bool TryGetDrawObjectOrientedBounds(DrawObject* drawObject, out OrientedBounds bounds)
    {
        bounds = default;
        return drawObject != null && ObjectSceneInterop.TryGetDrawObjectOrientedBounds(drawObject, out bounds);
    }

    protected static unsafe bool TryGetDrawObjectBounds(DrawObject* drawObject, out AxisAlignedBounds bounds)
    {
        bounds = default;
        return drawObject != null && ObjectSceneInterop.TryGetDrawObjectBounds(drawObject, out bounds);
    }
}

internal abstract unsafe class LayoutSceneObject : SceneObject
{
    protected abstract ILayoutInstance* LayoutInstance { get; }

    public override nint Address
        => (nint)LayoutInstance;

    protected LayoutSceneObject(IFramework framework, ILogger logger, ObjectSnapshot snapshot)
        : base(framework, logger, snapshot)
    {
    }

    public override bool TryGetBounds(out AxisAlignedBounds bounds)
    {
        bounds = default;
        return LayoutInstance != null && ObjectLayoutInterop.TryGetBounds(LayoutInstance, out bounds);
    }
}

internal abstract unsafe class DrawSceneObject : SceneObject
{
    protected abstract DrawObject* DrawObjectPointer { get; }

    protected DrawSceneObject(IFramework framework, ILogger logger, ObjectSnapshot snapshot)
        : base(framework, logger, snapshot)
    {
    }

    public override bool TryGetBounds(out AxisAlignedBounds bounds)
    {
        bounds = default;
        return CanResolveDrawObjectBounds(DrawObjectPointer) && TryGetDrawObjectBounds(DrawObjectPointer, out bounds);
    }

    public override bool TryGetOrientedBounds(out OrientedBounds bounds)
    {
        bounds = default;
        return CanResolveDrawObjectBounds(DrawObjectPointer) && TryGetDrawObjectOrientedBounds(DrawObjectPointer, out bounds);
    }

    protected virtual bool CanResolveDrawObjectBounds(DrawObject* drawObject)
        => drawObject != null;
}

internal abstract unsafe class LayoutDrawSceneObject : LayoutSceneObject
{
    protected abstract DrawObject* DrawObjectPointer { get; }

    protected LayoutDrawSceneObject(IFramework framework, ILogger logger, ObjectSnapshot snapshot)
        : base(framework, logger, snapshot)
    {
    }

    public override bool TryGetOrientedBounds(out OrientedBounds bounds)
    {
        bounds = default;
        return CanResolveDrawObjectBounds(DrawObjectPointer) && TryGetDrawObjectOrientedBounds(DrawObjectPointer, out bounds);
    }

    protected virtual bool CanResolveDrawObjectBounds(DrawObject* drawObject)
        => drawObject != null;
}


