using Intoner.Objects.Utils;
using Intoner.Services.Gpu;
using Intoner.Utils;
using Microsoft.Extensions.Logging;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Intoner.Scene.Rendering;

/// <summary> owns one registered world surface until disposed </summary>
internal interface IWorldSurfaceHandle : IDisposable
{
    /// <summary> replaces the rendered surface state </summary>
    bool Update(WorldSurfaceState state);
}

/// <summary> creates independently owned world surfaces </summary>
internal interface IWorldSurfaceService
{
    /// <summary> creates one surface and returns its ownership handle </summary>
    bool TryCreate(WorldSurfaceState state, out IWorldSurfaceHandle? handle);
}

internal enum WorldSurfaceOcclusion
{
    Disabled,
    SceneDepth,
}

[StructLayout(LayoutKind.Auto)]
internal readonly record struct WorldSurfaceState(
    Matrix4x4 Transform,
    Vector2 Size,
    Vector4 Tint,
    Vector4 UvRect,
    bool TwoSided,
    WorldSurfaceOcclusion Occlusion,
    SceneRenderPhase RenderPhase,
    IGpuFrameSource? FrameSource)
{
    public bool IsValid
        => NumericsUtility.IsFinite(Transform)
           && NumericsUtility.IsFinite(Size)
           && Size.X > NumericsUtility.ScalarEpsilon
           && Size.Y > NumericsUtility.ScalarEpsilon
           && NumericsUtility.IsFinite(Tint)
           && NumericsUtility.IsFinite(UvRect)
           && Occlusion is WorldSurfaceOcclusion.Disabled or WorldSurfaceOcclusion.SceneDepth
           && RenderPhase is SceneRenderPhase.BeforeGameUi3D or SceneRenderPhase.AfterGameUi3D;

    public bool IsVisible
        => IsValid && Tint.W > 0f;

    public bool RequiresSceneDepth
        => Occlusion == WorldSurfaceOcclusion.SceneDepth;

    public Matrix4x4 CreateBoundsTransform(float thickness)
        => Matrix4x4.CreateScale(Size.X, Size.Y, thickness) * Transform;

    public bool TryMapPointer(
        in ScenePointerRay ray,
        in Matrix4x4 inverseTransform,
        out Vector2 position)
    {
        position = default;
        Vector3 localOrigin = Vector3.Transform(ray.Origin, inverseTransform);
        Vector3 localDirection = Vector3.TransformNormal(ray.Direction, inverseTransform);
        if (MathF.Abs(localDirection.Z) <= NumericsUtility.ScalarEpsilon
            || !TwoSided && localDirection.Z >= 0f)
        {
            return false;
        }

        float distanceAlongRay = -localOrigin.Z / localDirection.Z;
        if (distanceAlongRay < 0f)
        {
            return false;
        }

        Vector3 localHit = localOrigin + (localDirection * distanceAlongRay);
        Vector2 halfSize = Size * 0.5f;
        if (localHit.X < -halfSize.X
            || localHit.X > halfSize.X
            || localHit.Y < -halfSize.Y
            || localHit.Y > halfSize.Y)
        {
            return false;
        }

        Vector2 surfacePosition = new(
            (localHit.X / Size.X) + 0.5f,
            0.5f - (localHit.Y / Size.Y));
        position = Vector2.Lerp(
            new Vector2(UvRect.X, UvRect.Y),
            new Vector2(UvRect.Z, UvRect.W),
            surfacePosition);
        return true;
    }
}

[StructLayout(LayoutKind.Auto)]
internal readonly record struct WorldSurfaceRenderState(
    WorldSurfaceState Surface,
    GpuFrameTransferService.Lease? FrameTransfer);

internal sealed class WorldSurfaceService : IWorldSurfaceService, ISceneRenderPass, IDisposable
{
    private const int RenderOrder = -100;

    private readonly ILogger<WorldSurfaceService> _logger;
    private readonly SceneRenderService _sceneRenderer;
    private readonly WorldSurfaceRenderer _renderer;
    private readonly GpuFrameTransferService _frameTransfers;
    private readonly Lock _stateLock = new();
    private readonly Lock _registrationLock = new();
    private readonly DisposalState _disposeState = new();
    private readonly List<SurfaceEntry> _surfaces = [];

    private SceneRenderService.Registration? _registration;
    private RenderSnapshot _snapshot = RenderSnapshot.Empty;

    public WorldSurfaceService(
        ILogger<WorldSurfaceService> logger,
        SceneRenderService sceneRenderer,
        WorldSurfaceRenderer renderer,
        GpuFrameTransferService frameTransfers)
    {
        _logger = logger;
        _sceneRenderer = sceneRenderer;
        _renderer = renderer;
        _frameTransfers = frameTransfers;
    }

    public bool TryCreate(WorldSurfaceState state, out IWorldSurfaceHandle? handle)
    {
        handle = null;
        if (!state.IsValid || _disposeState.IsDisposing)
        {
            return false;
        }

        SurfaceEntry entry;
        try
        {
            entry = new SurfaceEntry(state, _frameTransfers);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "failed to initialize world surface resources");
            return false;
        }

        bool added;
        lock (_stateLock)
        {
            added = !_disposeState.IsDisposing;
            if (added)
            {
                _surfaces.Add(entry);
                RebuildSnapshotLocked();
            }
        }

        if (!added)
        {
            TryDispose(entry);
            return false;
        }

        if (!SyncRegistration())
        {
            Remove(entry);
            return false;
        }

        handle = new Handle(this, entry);
        return true;
    }

    public void Dispose()
    {
        if (!_disposeState.TryBeginDispose())
        {
            return;
        }

        SceneRenderService.Registration? registration;
        SurfaceEntry[] surfaces;
        lock (_registrationLock)
        {
            lock (_stateLock)
            {
                surfaces = [.. _surfaces];
                _surfaces.Clear();
                Volatile.Write(ref _snapshot, RenderSnapshot.Empty);
                registration = _registration;
                _registration = null;
            }

            TryDispose(registration);
        }

        foreach (SurfaceEntry surface in surfaces)
        {
            TryDispose(surface);
        }
    }

    int ISceneRenderPass.Order
        => RenderOrder;

    bool ISceneRenderPass.TryGetRequest(SceneRenderPhase phase, out SceneRenderFeatures features)
    {
        if (_disposeState.IsDisposing)
        {
            features = SceneRenderFeatures.None;
            return false;
        }

        return Volatile.Read(ref _snapshot).TryGetRequest(phase, out features);
    }

    void ISceneRenderPass.Draw(SceneRenderPhase phase, in SceneRenderFrame frame)
    {
        lock (_stateLock)
        {
            RenderSnapshot snapshot = _snapshot;
            _renderer.Draw(snapshot.GetSurfaces(phase), frame);
        }
    }

    private bool SyncRegistration()
    {
        lock (_registrationLock)
        {
            SceneRenderService.Registration? registration;
            bool needsRegistration;
            bool synchronized;
            lock (_stateLock)
            {
                if (_disposeState.IsDisposing)
                {
                    registration = _registration;
                    _registration = null;
                    needsRegistration = false;
                    synchronized = false;
                }
                else if (_snapshot.Count == 0)
                {
                    registration = _registration;
                    _registration = null;
                    needsRegistration = false;
                    synchronized = true;
                }
                else if (_registration != null)
                {
                    return true;
                }
                else
                {
                    registration = null;
                    needsRegistration = true;
                    synchronized = false;
                }
            }

            TryDispose(registration);
            if (!needsRegistration)
            {
                return synchronized;
            }

            SceneRenderService.Registration? sceneRegistration;
            try
            {
                sceneRegistration = _sceneRenderer.TryRegister(this);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "failed to register world surface rendering");
                return false;
            }
            if (sceneRegistration == null)
            {
                lock (_stateLock)
                {
                    return !_disposeState.IsDisposing
                           && (_snapshot.Count == 0 || _registration != null);
                }
            }

            bool releaseRegistration;
            lock (_stateLock)
            {
                releaseRegistration = _disposeState.IsDisposing
                                      || _snapshot.Count == 0
                                      || _registration != null;
                if (!releaseRegistration)
                {
                    _registration = sceneRegistration;
                }

                synchronized = !_disposeState.IsDisposing
                               && (_snapshot.Count == 0 || _registration != null);
            }

            if (releaseRegistration)
            {
                TryDispose(sceneRegistration);
            }

            return synchronized;
        }
    }

    private bool Update(SurfaceEntry entry, WorldSurfaceState state)
    {
        if (!state.IsValid)
        {
            return false;
        }

        try
        {
            lock (_stateLock)
            {
                if (_disposeState.IsDisposing || !_surfaces.Contains(entry))
                {
                    return false;
                }

                if (entry.State != state)
                {
                    entry.UpdateFrameTransfer(state, _frameTransfers);
                    entry.State = state;
                    RebuildSnapshotLocked();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "failed to update world surface resources");
            return false;
        }

        return SyncRegistration();
    }

    private void Remove(SurfaceEntry entry)
    {
        lock (_stateLock)
        {
            if (!_surfaces.Remove(entry))
            {
                return;
            }

            RebuildSnapshotLocked();
        }

        _ = SyncRegistration();
        TryDispose(entry);
    }

    private void TryDispose(IDisposable? resource)
    {
        try
        {
            resource?.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "failed to dispose world surface resources");
        }
    }

    private void RebuildSnapshotLocked()
    {
        int visibleCount = 0;
        int beforeGameUi3DCount = 0;
        for (int index = 0; index < _surfaces.Count; ++index)
        {
            WorldSurfaceState state = _surfaces[index].State;
            if (state.Tint.W <= 0f)
            {
                continue;
            }

            ++visibleCount;
            if (state.RenderPhase == SceneRenderPhase.BeforeGameUi3D)
            {
                ++beforeGameUi3DCount;
            }
        }

        var snapshot = new WorldSurfaceRenderState[visibleCount];
        int beforeGameUi3DIndex = 0;
        int afterGameUi3DIndex = beforeGameUi3DCount;
        SceneRenderFeatures beforeGameUi3DFeatures = SceneRenderFeatures.None;
        SceneRenderFeatures afterGameUi3DFeatures = SceneRenderFeatures.None;
        for (int index = 0; index < _surfaces.Count; ++index)
        {
            SurfaceEntry entry = _surfaces[index];
            WorldSurfaceState state = entry.State;
            if (state.Tint.W <= 0f)
            {
                continue;
            }

            var renderState = new WorldSurfaceRenderState(state, entry.FrameTransfer);
            SceneRenderFeatures features = state.RequiresSceneDepth
                ? SceneRenderFeatures.SceneDepth
                : SceneRenderFeatures.None;
            if (state.RenderPhase == SceneRenderPhase.BeforeGameUi3D)
            {
                snapshot[beforeGameUi3DIndex++] = renderState;
                beforeGameUi3DFeatures |= features;
            }
            else
            {
                snapshot[afterGameUi3DIndex++] = renderState;
                afterGameUi3DFeatures |= features;
            }
        }

        Volatile.Write(
            ref _snapshot,
            new RenderSnapshot(
                snapshot,
                beforeGameUi3DCount,
                beforeGameUi3DFeatures,
                afterGameUi3DFeatures));
    }

    private sealed record RenderSnapshot(
        WorldSurfaceRenderState[] Surfaces,
        int BeforeGameUi3DCount,
        SceneRenderFeatures BeforeGameUi3DFeatures,
        SceneRenderFeatures AfterGameUi3DFeatures)
    {
        public static RenderSnapshot Empty { get; } = new([], 0, SceneRenderFeatures.None, SceneRenderFeatures.None);

        public int Count
            => Surfaces.Length;

        public ReadOnlySpan<WorldSurfaceRenderState> GetSurfaces(SceneRenderPhase phase)
            => phase switch
            {
                SceneRenderPhase.BeforeGameUi3D => Surfaces.AsSpan(0, BeforeGameUi3DCount),
                SceneRenderPhase.AfterGameUi3D => Surfaces.AsSpan(BeforeGameUi3DCount),
                _ => [],
            };

        public bool TryGetRequest(SceneRenderPhase phase, out SceneRenderFeatures features)
        {
            int count;
            (count, features) = phase switch
            {
                SceneRenderPhase.BeforeGameUi3D => (BeforeGameUi3DCount, BeforeGameUi3DFeatures),
                SceneRenderPhase.AfterGameUi3D => (Surfaces.Length - BeforeGameUi3DCount, AfterGameUi3DFeatures),
                _ => (0, SceneRenderFeatures.None),
            };
            return count > 0;
        }
    }

    internal sealed class SurfaceEntry : IDisposable
    {
        private IGpuFrameSource? _presentedSource;

        public SurfaceEntry(WorldSurfaceState state, GpuFrameTransferService frameTransfers)
        {
            State = state;
            UpdateFrameTransfer(state, frameTransfers);
        }

        public WorldSurfaceState State { get; set; }
        public GpuFrameTransferService.Lease? FrameTransfer { get; private set; }

        public void UpdateFrameTransfer(
            WorldSurfaceState state,
            GpuFrameTransferService frameTransfers)
        {
            IGpuFrameSource? source = state.IsVisible ? state.FrameSource : null;
            if (ReferenceEquals(_presentedSource, source))
            {
                return;
            }

            GpuFrameTransferService.Lease? next = source != null
                ? frameTransfers.Acquire(source)
                : null;
            try
            {
                FrameTransfer?.Dispose();
            }
            catch
            {
                next?.Dispose();
                throw;
            }

            FrameTransfer = next;
            _presentedSource = source;
        }

        public void Dispose()
        {
            GpuFrameTransferService.Lease? transfer = FrameTransfer;
            FrameTransfer = null;
            _presentedSource = null;
            transfer?.Dispose();
        }
    }

    internal sealed class Handle : IWorldSurfaceHandle
    {
        private WorldSurfaceService? _owner;
        private readonly SurfaceEntry _entry;

        public Handle(WorldSurfaceService owner, SurfaceEntry entry)
        {
            _owner = owner;
            _entry = entry;
        }

        public bool Update(WorldSurfaceState state)
            => Volatile.Read(ref _owner)?.Update(_entry, state) ?? false;

        public void Dispose()
            => Interlocked.Exchange(ref _owner, null)?.Remove(_entry);
    }
}
