using Dalamud.Plugin.Services;
using Intoner.Objects.Models;
using Intoner.Objects.Utils;
using Intoner.Services.Gpu;
using Microsoft.Extensions.Logging;
using System.Numerics;
using Device = SharpDX.Direct3D11.Device;

namespace Intoner.Objects.Runtime;

/// <summary>
/// Performs one mesh aware click selection query against the active object scene using an offscreen GPU id pass.
/// </summary>
internal interface IObjectSelectionService
{
    /// <summary>
    /// Tries to resolve the nearest visible active object under the given viewport cursor for selection.
    /// </summary>
    /// <param name="viewportPos">The top left viewport position in screen space.</param>
    /// <param name="viewportSize">The viewport size in pixels.</param>
    /// <param name="mousePos">The current cursor position in screen space.</param>
    /// <param name="snapshot">The selected active object snapshot when one was resolved.</param>
    /// <returns>true when one active object was resolved for selection.</returns>
    bool TrySelectActiveObject(Vector2 viewportPos, Vector2 viewportSize, Vector2 mousePos, out ObjectSnapshot snapshot);
}

internal sealed class ObjectSelectionService : IObjectSelectionService, IDisposable
{
    private readonly ILogger<ObjectSelectionService> _logger;
    private readonly IFramework _framework;
    private readonly IObjectSceneState _sceneState;
    private readonly GpuProcessingService _gpuProcessingService;
    private readonly ObjectSelectionGeometryCache _geometryCache;
    private readonly Lock _rendererLock = new();

    private ObjectSelectionRenderer? _renderer;
    private bool _disposed;

    public ObjectSelectionService(
        ILogger<ObjectSelectionService> logger,
        IFramework framework,
        IObjectSceneState sceneState,
        GpuProcessingService gpuProcessingService,
        ObjectSelectionGeometryCache geometryCache)
    {
        _logger = logger;
        _framework = framework;
        _sceneState = sceneState;
        _gpuProcessingService = gpuProcessingService;
        _geometryCache = geometryCache;
    }

    public bool TrySelectActiveObject(Vector2 viewportPos, Vector2 viewportSize, Vector2 mousePos, out ObjectSnapshot snapshot)
    {
        snapshot = default!;

        if (_disposed
            || !TryCreateSelectionQuery(viewportPos, viewportSize, mousePos, out var query)
            || !TryCollectSelectionContext(out var context))
        {
            return false;
        }

        if (!_gpuProcessingService.TryEnterOperationScope(CancellationToken.None, GpuJobFlags.None, out var operationScope)
            || operationScope is null)
        {
            return false;
        }

        using (operationScope)
        {
            lock (_rendererLock)
            {
                return TryExecuteSelectionQuery(context, query, out snapshot);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        lock (_rendererLock)
        {
            ResetRendererUnsafe();
        }
    }

    private bool TryCreateSelectionQuery(Vector2 viewportPos, Vector2 viewportSize, Vector2 mousePos, out SelectionQuery query)
    {
        query = default;
        if (viewportSize.X < 1f
            || viewportSize.Y < 1f
            || mousePos.X < viewportPos.X
            || mousePos.X > viewportPos.X + viewportSize.X
            || mousePos.Y < viewportPos.Y
            || mousePos.Y > viewportPos.Y + viewportSize.Y)
        {
            return false;
        }

        var viewportWidth = Math.Max(1, (int)MathF.Round(viewportSize.X));
        var viewportHeight = Math.Max(1, (int)MathF.Round(viewportSize.Y));
        query = new SelectionQuery(
            viewportWidth,
            viewportHeight,
            Math.Clamp((int)MathF.Floor(mousePos.X - viewportPos.X), 0, viewportWidth - 1),
            Math.Clamp((int)MathF.Floor(mousePos.Y - viewportPos.Y), 0, viewportHeight - 1));
        return true;
    }

    private bool TryCollectSelectionContext(out SelectionContext context)
    {
        context = ObjectFrameworkUtility.RunOnFrameworkThread(_framework, CollectSelectionContextUnsafe);
        return context.IsValid && context.Collector.HasDraws;
    }

    private SelectionContext CollectSelectionContextUnsafe()
    {
        if (!ObjectViewportProjectionUtility.TryGetActiveCameraProjection(out var viewProjection, out var viewMatrix, out var nearPlane))
        {
            return SelectionContext.Invalid;
        }

        var reverseDepth = ObjectViewportProjectionUtility.TryResolveReverseDepth(viewProjection, viewMatrix, nearPlane, out var resolvedReverseDepth)
            && resolvedReverseDepth;

        var collector = new ObjectSelectionCollector();
        foreach (var entry in _sceneState.GetEntriesSnapshot())
        {
            entry.SceneObject.AppendSelectionDraws(collector);
        }

        return new SelectionContext(viewProjection, reverseDepth, collector);
    }

    private bool TryExecuteSelectionQuery(SelectionContext context, SelectionQuery query, out ObjectSnapshot snapshot)
    {
        snapshot = default!;

        try
        {
            if (!TryEnsureRendererUnsafe(out var renderer) || renderer is null)
            {
                return false;
            }

            if (!renderer.TryRenderSelectionId(
                    context.Collector,
                    context.ViewProjection,
                    context.UseReverseDepth,
                    query.ViewportWidth,
                    query.ViewportHeight,
                    query.PixelX,
                    query.PixelY,
                    out var selectionId)
                || selectionId == 0
                || !context.Collector.TryGetSnapshot(selectionId, out snapshot))
            {
                snapshot = default!;
                return false;
            }

            renderer.TouchSelectionPaths(context.Collector, selectionId);
            return true;
        }
        catch (Exception ex)
        {
            _gpuProcessingService.NotifyOperationFailure(ex);
            ResetRendererUnsafe();
            _logger.LogWarning(ex, "object gpu selection failed");
            snapshot = default!;
            return false;
        }
    }

    private bool TryEnsureRendererUnsafe(out ObjectSelectionRenderer? renderer)
    {
        renderer = _renderer;
        if (renderer is not null)
        {
            return true;
        }

        nint d3d11Device = nint.Zero;
        Device? device = null;
        try
        {
            if (!_gpuProcessingService.TryCreateOperationDeviceClone(out d3d11Device) || d3d11Device == nint.Zero)
            {
                return false;
            }

            device = new Device(d3d11Device);
            d3d11Device = nint.Zero;
            renderer = new ObjectSelectionRenderer(device, _geometryCache);
            _renderer = renderer;
            device = null;
            return true;
        }
        catch (Exception ex)
        {
            device?.Dispose();
            _gpuProcessingService.NotifyOperationFailure(ex);
            _logger.LogWarning(ex, "object selection renderer initialization failed");
            renderer = null;
            return false;
        }
        finally
        {
            GpuProcessingService.ReleaseComObject(ref d3d11Device);
        }
    }

    private void ResetRendererUnsafe()
    {
        _renderer?.Dispose();
        _renderer = null;
    }

    private readonly record struct SelectionQuery(
        int ViewportWidth,
        int ViewportHeight,
        int PixelX,
        int PixelY);

    private readonly record struct SelectionContext(
        Matrix4x4 ViewProjection,
        bool UseReverseDepth,
        ObjectSelectionCollector Collector)
    {
        public static SelectionContext Invalid { get; } = new(default, false, new ObjectSelectionCollector());

        public bool IsValid
            => ViewProjection != default;
    }
}

