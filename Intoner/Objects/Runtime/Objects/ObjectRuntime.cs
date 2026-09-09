using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using Intoner.Objects.Interop;
using Intoner.Objects.Models;
using Intoner.Objects.Utils;
using Intoner.Scene;
using Intoner.Utils;
using Microsoft.Extensions.Logging;
using System.Numerics;
using AxisAlignedBounds = FFXIVClientStructs.FFXIV.Common.Math.AxisAlignedBounds;
using OrientedBounds = FFXIVClientStructs.FFXIV.Common.Math.OrientedBounds;

namespace Intoner.Objects.Runtime;

/// <summary>
/// Describes the outcome of applying one sanitized snapshot to an active object runtime.
/// </summary>
internal enum ObjectRuntimeUpdateResult
{
    /// <summary>
    /// The snapshot was applied in place.
    /// </summary>
    Applied,

    /// <summary>
    /// The snapshot is valid, but this object runtime must be recreated to apply it.
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
internal interface IObjectRuntime : ISceneSelectableRuntime, IDisposable
{
    /// <summary>
    /// Gets the object kind handled by this object runtime.
    /// </summary>
    ObjectKind Kind { get; }

    /// <summary>
    /// Gets the current sanitized snapshot applied to this object runtime.
    /// </summary>
    new ObjectSnapshot Snapshot { get; }

    /// <summary>
    /// Gets the current native address for this object runtime.
    /// </summary>
    nint Address { get; }

    /// <summary>
    /// Gets whether the object runtime needs framework updates.
    /// </summary>
    bool NeedsFrameworkUpdates { get; }

    /// <summary>
    /// Runs one framework update step.
    /// </summary>
    void FrameworkUpdate();

    /// <summary>
    /// Tries to resolve axis aligned world bounds.
    /// </summary>
    bool TryGetBounds(out AxisAlignedBounds bounds);

    /// <summary>
    /// Tries to resolve oriented local bounds.
    /// </summary>
    bool TryGetOrientedBounds(out OrientedBounds bounds);

    /// <summary>
    /// Attempts to apply the given sanitized snapshot to this object runtime.
    /// </summary>
    /// <param name="snapshot">The sanitized snapshot to apply.</param>
    /// <returns>
    /// The update result. 'Applied' means the object runtime updated in place, 'RequiresRecreate' means the manager should recreate it,
    /// and 'Rejected' means the snapshot should not be recreated automatically.
    /// </returns>
    ObjectRuntimeUpdateResult TryUpdate(ObjectSnapshot snapshot);

    /// <summary>
    /// Applies a collection id only snapshot change while resource materialization is still pending.
    /// </summary>
    /// <param name="snapshot">the sanitized snapshot with the pending collection id</param>
    /// <returns>the update result</returns>
    ObjectRuntimeUpdateResult TryUpdateCollectionAssignment(ObjectSnapshot snapshot);

    /// <summary>
    /// Attempts to reload collection backed resources without changing the stored object snapshot.
    /// </summary>
    /// <param name="snapshot">the desired active snapshot</param>
    /// <returns>the refresh result</returns>
    ObjectRuntimeUpdateResult TryRefreshResources(ObjectSnapshot snapshot);

    /// <summary>
    /// Tries to resolve the native clearance used by housing floor placement checks.
    /// </summary>
    /// <param name="clearance">the resolved clearance when available</param>
    /// <returns>true when clearance was available</returns>
    bool TryGetPlacementClearance(out ObjectPlacementClearance clearance);

    /// <summary>
    /// gets native placement surfaces exposed by this object runtime
    /// </summary>
    ObjectPlacementSurfaceSupport PlacementSurfaceSupport { get; }
}

internal struct DeferredVisualState
{
    private bool _needsReplay;

    public void Reset()
        => _needsReplay = false;

    public ObjectRuntimeUpdateResult Apply<TModel>(
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
            return ObjectRuntimeUpdateResult.Applied;
        }

        _needsReplay = !tryApplyVisualState(model);
        return ObjectRuntimeUpdateResult.Applied;
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

internal abstract class ObjectRuntime : IObjectRuntime
{
    protected const byte DestroyFlagsFree = 1;

    private bool _disposed;

    protected IFramework Framework { get; }
    protected ILogger Logger { get; }

    public ObjectSnapshot Snapshot { get; protected set; }
    SceneItemSnapshot ISceneItemRuntime.Snapshot => Snapshot;

    public abstract ObjectKind Kind { get; }
    public virtual bool NeedsFrameworkUpdates => false;
    public virtual ObjectPlacementSurfaceSupport PlacementSurfaceSupport => ObjectPlacementSurfaceSupport.None;
    public abstract nint Address { get; }

    protected ObjectRuntime(IFramework framework, ILogger logger, ObjectSnapshot snapshot)
    {
        Framework = framework;
        Logger = logger;
        Snapshot = snapshot;
    }

    internal virtual void Initialize()
    {
    }

    public ObjectRuntimeUpdateResult TryUpdate(ObjectSnapshot snapshot)
        => RunOnFrameworkThread(() => TryUpdateUnsafe(snapshot));

    public ObjectRuntimeUpdateResult TryUpdateCollectionAssignment(ObjectSnapshot snapshot)
        => RunOnFrameworkThread(() => TryUpdateCollectionAssignmentUnsafe(snapshot));

    public ObjectRuntimeUpdateResult TryRefreshResources(ObjectSnapshot snapshot)
        => RunOnFrameworkThread(() => RefreshResourcesUnsafe(snapshot));

    public void FrameworkUpdate()
        => FrameworkUpdateUnsafe();

    public abstract void AppendSelectionDraws(SceneSelectionCollector collector);

    public abstract bool TryGetBounds(out AxisAlignedBounds bounds);
    public abstract bool TryGetOrientedBounds(out OrientedBounds bounds);
    public virtual bool TryGetPlacementClearance(out ObjectPlacementClearance clearance)
    {
        clearance = default;
        return false;
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed || !disposing)
        {
            return;
        }

        _disposed = true;
        if (FrameworkThreadUtility.TryRun(Framework, DisposeUnsafe))
        {
            return;
        }

        Logger.LogWarning(
            "framework is unloading, skipping object runtime destroy for {Kind} 0x{Address:X}",
            Kind,
            (ulong)Address);
    }

    protected virtual ObjectRuntimeUpdateResult ValidateSnapshotUpdate(ObjectSnapshot snapshot)
        => ObjectRuntimeUpdateResult.Applied;

    protected virtual ObjectRuntimeUpdateResult RefreshResourcesUnsafe(ObjectSnapshot snapshot)
        => ObjectRuntimeUpdateResult.Rejected;

    protected abstract ObjectRuntimeUpdateResult ApplySnapshotUnsafe(ObjectSnapshot snapshot, ObjectSnapshot previousSnapshot);

    protected virtual void FrameworkUpdateUnsafe()
    {
    }

    protected abstract void DisposeUnsafe();

    protected T RunOnFrameworkThread<T>(Func<T> func)
        => FrameworkThreadUtility.Run(Framework, func);

    protected void RunOnFrameworkThread(Action action)
        => FrameworkThreadUtility.Run(Framework, action);

    protected static Quaternion CreateRotation(Vector3 rotationDegrees)
        => SceneTransformMath.CreateRotationQuaternion(rotationDegrees);

    private ObjectRuntimeUpdateResult TryUpdateUnsafe(ObjectSnapshot snapshot)
    {
        var validationResult = ValidateSnapshotUpdate(snapshot);
        if (validationResult != ObjectRuntimeUpdateResult.Applied)
        {
            return validationResult;
        }

        var previousSnapshot = Snapshot;
        var applyResult = ApplySnapshotUnsafe(snapshot, previousSnapshot);
        if (applyResult != ObjectRuntimeUpdateResult.Applied)
        {
            return applyResult;
        }

        Snapshot = snapshot;
        return ObjectRuntimeUpdateResult.Applied;
    }

    private ObjectRuntimeUpdateResult TryUpdateCollectionAssignmentUnsafe(ObjectSnapshot snapshot)
    {
        if (!ObjectSnapshotUtility.IsCollectionOnlyChange(Snapshot, snapshot))
        {
            return ObjectRuntimeUpdateResult.Rejected;
        }

        Snapshot = snapshot;
        return ObjectRuntimeUpdateResult.Applied;
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

internal abstract unsafe class LayoutObjectRuntime : ObjectRuntime
{
    protected abstract ILayoutInstance* LayoutInstance { get; }

    public override nint Address
        => (nint)LayoutInstance;

    protected LayoutObjectRuntime(IFramework framework, ILogger logger, ObjectSnapshot snapshot)
        : base(framework, logger, snapshot)
    {
    }

    public override bool TryGetBounds(out AxisAlignedBounds bounds)
    {
        bounds = default;
        return LayoutInstance != null && ObjectLayoutInterop.TryGetBounds(LayoutInstance, out bounds);
    }
}

internal abstract unsafe class DrawObjectRuntime : ObjectRuntime
{
    internal static void DestroyNative(DrawObject* drawObject)
    {
        drawObject->CleanupRender();
        drawObject->Dtor(DestroyFlagsFree);
    }

    protected abstract DrawObject* DrawObjectPointer { get; }

    protected DrawObjectRuntime(IFramework framework, ILogger logger, ObjectSnapshot snapshot)
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

internal abstract unsafe class LayoutDrawObjectRuntime : LayoutObjectRuntime
{
    protected abstract DrawObject* DrawObjectPointer { get; }

    protected LayoutDrawObjectRuntime(IFramework framework, ILogger logger, ObjectSnapshot snapshot)
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


