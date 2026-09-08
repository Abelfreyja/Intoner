using Intoner.Objects.Utils;
using Intoner.Scene;
using Intoner.Scene.Rendering;
using Intoner.Services.Gpu;
using Intoner.Services.Input;
using Microsoft.Extensions.Logging;
using System.Numerics;

namespace Intoner.Displays;

/// <summary> owns the capture source and world surface for one active display </summary>
internal sealed class DisplayRuntime : ISceneSelectableRuntime, ISceneInputTarget, IDisposable
{
    private const long CaptureRetryIntervalMilliseconds = 2000;
    private const float SelectionThickness = 0.02f;

    private readonly ILogger<DisplayRuntime> _logger;
    private readonly IWorldSurfaceHandle _surface;
    private readonly WindowsCaptureTargetService _targetService;
    private readonly WindowsCaptureService _captureService;
    private readonly ISceneInputRegistry _inputRegistry;
    private readonly Action _requestMaintenance;

    private WindowsCaptureService.SourceLease? _capture;
    private WindowPointerSession? _pointerSession;
    private IDisposable? _inputRegistration;
    private WorldSurfaceState _surfaceState;
    private Matrix4x4 _inverseWorldTransform;
    private long _nextCaptureRetryMilliseconds;
    private bool _hasInverseWorldTransform;
    private bool _surfaceSynchronized;
    private bool _loggedTargetResolutionFailure;
    private bool _loggedCaptureFailure;
    private bool _disposed;

    public DisplaySnapshot Snapshot { get; private set; }
    SceneItemSnapshot ISceneItemRuntime.Snapshot => Snapshot;
    Guid ISceneInputTarget.ItemId => Snapshot.Id;

    public long NextMaintenanceMilliseconds
        => !_disposed
           && Snapshot.Visible
           && Snapshot.Settings.Target.IsConfigured
           && (_capture is null || _capture.IsClosed)
            ? _nextCaptureRetryMilliseconds
            : long.MaxValue;

    bool ISceneInputTarget.CanReceivePointerInput
        => !_disposed && Snapshot.Visible && _pointerSession != null;

    public DisplayRuntime(
        ILogger<DisplayRuntime> logger,
        DisplaySnapshot snapshot,
        IWorldSurfaceHandle surface,
        WindowsCaptureTargetService targetService,
        WindowsCaptureService captureService,
        ISceneInputRegistry inputRegistry,
        Action requestMaintenance)
    {
        _logger = logger;
        Snapshot = snapshot;
        _surface = surface;
        _targetService = targetService;
        _captureService = captureService;
        _inputRegistry = inputRegistry;
        _requestMaintenance = requestMaintenance;
    }

    public bool TryUpdate(DisplaySnapshot snapshot)
    {
        if (_disposed || !TryEnsureInputRegistration())
        {
            return false;
        }

        if (_surfaceSynchronized && Equals(Snapshot, snapshot))
        {
            return true;
        }

        DisplaySnapshot previous = Snapshot;
        bool targetChanged = !snapshot.Settings.Target.HasSameCaptureSource(previous.Settings.Target);
        if (targetChanged)
        {
            _loggedTargetResolutionFailure = false;
            _loggedCaptureFailure = false;
        }

        if (!snapshot.Visible)
        {
            StopCapture();
        }
        else
        {
            bool retryDue = Environment.TickCount64 >= _nextCaptureRetryMilliseconds;
            if (targetChanged
                || !previous.Visible
                || retryDue
                   && (_capture?.IsClosed == true
                       || _capture is null && snapshot.Settings.Target.IsConfigured))
            {
                ReplaceCaptureSource(snapshot.Settings.Target);
            }
        }

        WorldSurfaceState surfaceState = CreateSurfaceState(snapshot, _capture?.FrameSource);
        return TryApplySurfaceState(snapshot, surfaceState);
    }

    public bool MaintainCapture(long now)
    {
        if (_disposed)
        {
            return false;
        }

        WindowsCaptureTargetDescriptor target = Snapshot.Settings.Target;
        if (!Snapshot.Visible
            || !target.IsConfigured
            || _capture is { IsClosed: false }
            || now < _nextCaptureRetryMilliseconds)
        {
            return true;
        }

        ReplaceCaptureSource(target);
        return TryApplySurfaceState(
            Snapshot,
            _surfaceState with { FrameSource = _capture?.FrameSource });
    }

    public void AppendSelectionDraws(SceneSelectionCollector collector)
    {
        if (!Snapshot.Visible)
        {
            return;
        }

        collector.AddPrimitive(
            Snapshot,
            SceneSelectionPrimitiveKind.Box,
            _surfaceState.CreateBoundsTransform(SelectionThickness));
    }

    public static SceneItemBoundsSnapshot CreateBoundsSnapshot(DisplaySnapshot snapshot)
    {
        Matrix4x4 boundsTransform = CreateSurfaceState(snapshot, null).CreateBoundsTransform(SelectionThickness);
        if (!ObjectShapeMath.TryCreateOrientedBounds(boundsTransform, out SceneOrientedBounds orientedBounds))
        {
            Vector3 position = snapshot.Transform.Position;
            return new SceneItemBoundsSnapshot(
                snapshot.Id,
                SceneBoundsCategory.Display,
                position,
                position,
                null,
                IsManipulationBounds: true);
        }

        Span<Vector3> corners = stackalloc Vector3[8];
        ObjectShapeMath.CopyOrientedBoxCorners(orientedBounds, corners);
        Vector3 min = corners[0];
        Vector3 max = corners[0];
        for (int index = 1; index < corners.Length; ++index)
        {
            min = Vector3.Min(min, corners[index]);
            max = Vector3.Max(max, corners[index]);
        }

        return new SceneItemBoundsSnapshot(
            snapshot.Id,
            SceneBoundsCategory.Display,
            min,
            max,
            orientedBounds,
            IsManipulationBounds: true);
    }

    bool ISceneInputTarget.TryMapPointer(in ScenePointerRay ray, out Vector2 position)
    {
        if (_hasInverseWorldTransform)
        {
            return _surfaceState.TryMapPointer(ray, _inverseWorldTransform, out position);
        }

        position = default;
        return false;
    }

    bool ISceneInputTarget.TrySendPointer(in ScenePointerEvent pointerEvent)
        => _pointerSession?.TrySend(pointerEvent) == true;

    void ISceneInputTarget.CancelPointerInput()
        => _pointerSession?.Cancel();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        IDisposable? inputRegistration = _inputRegistration;
        _inputRegistration = null;
        TryDispose(inputRegistration, "input registration");
        StopCapture();
        TryDispose(_surface, "world surface");
    }

    private void StopCapture()
    {
        WindowPointerSession? pointerSession = _pointerSession;
        _pointerSession = null;
        TryDispose(pointerSession, "pointer session");
        WindowsCaptureService.SourceLease? capture = _capture;
        _capture = null;
        if (capture != null)
        {
            capture.Closed -= HandleCaptureClosed;
        }

        TryDispose(capture, "capture source");
    }

    public static WorldSurfaceState CreateSurfaceState(
        DisplaySnapshot snapshot,
        IGpuFrameSource? frameSource)
    {
        DisplaySettings settings = snapshot.Settings;
        Vector4 tint = snapshot.Visible
            ? settings.Tint
            : settings.Tint with { W = 0f };
        return new WorldSurfaceState(
            SceneTransformMath.CreateWorldTransform(snapshot.Transform),
            settings.Size,
            tint,
            settings.UvRect,
            settings.TwoSided,
            settings.UseSceneDepth
                ? WorldSurfaceOcclusion.SceneDepth
                : WorldSurfaceOcclusion.Disabled,
            settings.NameplatesAboveDisplay
                ? SceneRenderPhase.BeforeGameUi3D
                : SceneRenderPhase.AfterGameUi3D,
            frameSource);
    }

    private bool TryApplySurfaceState(DisplaySnapshot snapshot, WorldSurfaceState surfaceState)
    {
        _surfaceSynchronized = false;
        try
        {
            if (!_surface.Update(surfaceState))
            {
                return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "display surface update failed for {DisplayId}", snapshot.Id);
            return false;
        }

        Snapshot = snapshot;
        _surfaceState = surfaceState;
        _hasInverseWorldTransform = Matrix4x4.Invert(
            surfaceState.Transform,
            out _inverseWorldTransform);
        _surfaceSynchronized = true;
        return true;
    }

    private void ReplaceCaptureSource(WindowsCaptureTargetDescriptor target)
    {
        StopCapture();
        _nextCaptureRetryMilliseconds = Environment.TickCount64 + CaptureRetryIntervalMilliseconds;
        if (!target.IsConfigured)
        {
            return;
        }

        if (!_targetService.TryResolve(target, out WindowsCaptureTarget resolvedTarget))
        {
            if (!_loggedTargetResolutionFailure)
            {
                _loggedTargetResolutionFailure = true;
                _logger.LogWarning("display capture target could not be resolved for {Target}", target.Name);
            }

            return;
        }

        _loggedTargetResolutionFailure = false;
        try
        {
            _capture = _captureService.AcquireSource(resolvedTarget);
            _capture.Closed += HandleCaptureClosed;
            _pointerSession = resolvedTarget.Target.Kind == WindowsCaptureTargetKind.Window
                ? WindowPointerSession.TryCreate(resolvedTarget.Handle)
                : null;
            _loggedCaptureFailure = false;
            if (_capture.IsClosed)
            {
                HandleCaptureClosed();
            }
        }
        catch (Exception ex)
        {
            StopCapture();
            if (_loggedCaptureFailure)
            {
                return;
            }

            _loggedCaptureFailure = true;
            _logger.LogWarning(ex, "display capture source creation failed for {Target}", target.Name);
        }
    }

    private void HandleCaptureClosed()
        => _requestMaintenance();

    private bool TryEnsureInputRegistration()
    {
        if (_inputRegistration != null)
        {
            return true;
        }

        IDisposable? registration = null;
        try
        {
            if (!_inputRegistry.TryRegister(this, out registration)
                || registration is null)
            {
                TryDispose(registration, "input registration");
                return false;
            }

            _inputRegistration = registration;
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "display input registration failed for {DisplayId}", Snapshot.Id);
            TryDispose(registration, "input registration");
            return false;
        }
    }

    private void TryDispose(IDisposable? resource, string resourceName)
    {
        try
        {
            resource?.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "failed to dispose display {ResourceName} for {DisplayId}", resourceName, Snapshot.Id);
        }
    }
}

/// <summary> creates fully initialized display runtimes and owns failed construction cleanup </summary>
internal sealed class DisplayRuntimeFactory
{
    private readonly ILogger<DisplayRuntimeFactory> _logger;
    private readonly ILogger<DisplayRuntime> _runtimeLogger;
    private readonly IWorldSurfaceService _surfaceService;
    private readonly WindowsCaptureTargetService _targetService;
    private readonly WindowsCaptureService _captureService;
    private readonly ISceneInputRegistry _inputRegistry;

    public DisplayRuntimeFactory(
        ILogger<DisplayRuntimeFactory> logger,
        ILoggerFactory loggerFactory,
        IWorldSurfaceService surfaceService,
        WindowsCaptureTargetService targetService,
        WindowsCaptureService captureService,
        ISceneInputRegistry inputRegistry)
    {
        _logger = logger;
        _runtimeLogger = loggerFactory.CreateLogger<DisplayRuntime>();
        _surfaceService = surfaceService;
        _targetService = targetService;
        _captureService = captureService;
        _inputRegistry = inputRegistry;
    }

    public bool TryCreate(
        DisplaySnapshot snapshot,
        Action requestMaintenance,
        out DisplayRuntime? runtime)
    {
        runtime = null;
        IWorldSurfaceHandle? surface = null;
        DisplayRuntime? candidate = null;
        try
        {
            if (!_surfaceService.TryCreate(DisplayRuntime.CreateSurfaceState(snapshot, null), out surface)
                || surface is null)
            {
                return false;
            }

            candidate = new DisplayRuntime(
                _runtimeLogger,
                snapshot,
                surface,
                _targetService,
                _captureService,
                _inputRegistry,
                requestMaintenance);
            surface = null;
            if (!candidate.TryUpdate(snapshot))
            {
                return false;
            }

            runtime = candidate;
            candidate = null;
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "failed to create display runtime for {DisplayId}", snapshot.Id);
            return false;
        }
        finally
        {
            TryDispose(candidate, snapshot.Id, "runtime");
            TryDispose(surface, snapshot.Id, "world surface");
        }
    }

    private void TryDispose(IDisposable? resource, Guid displayId, string resourceName)
    {
        try
        {
            resource?.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "failed to dispose display {ResourceName} for {DisplayId}",
                resourceName,
                displayId);
        }
    }
}
