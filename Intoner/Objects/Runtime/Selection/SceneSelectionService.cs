using Dalamud.Plugin.Services;
using Intoner.Services.Gpu;
using Intoner.Utils;
using Microsoft.Extensions.Logging;
using System.Numerics;
using System.Runtime.InteropServices;
using Device = SharpDX.Direct3D11.Device;

namespace Intoner.Scene;

/// <summary>
/// Performs one click selection query against active scene items using an offscreen GPU id pass.
/// </summary>
internal interface ISceneSelectionService
{
    /// <summary>
    /// Tries to resolve the nearest visible active item under the given viewport cursor for selection.
    /// </summary>
    /// <param name="viewportPos">The top left viewport position in screen space.</param>
    /// <param name="viewportSize">The viewport size in pixels.</param>
    /// <param name="mousePos">The current cursor position in screen space.</param>
    /// <param name="snapshot">The selected active item snapshot when one was resolved.</param>
    /// <returns>true when one active item was resolved for selection.</returns>
    bool TrySelectActiveItem(Vector2 viewportPos, Vector2 viewportSize, Vector2 mousePos, out SceneItemSnapshot snapshot);
}

internal sealed class SceneSelectionService : ISceneSelectionService, IDisposable
{
    private readonly ILogger<SceneSelectionService> _logger;
    private readonly IFramework _framework;
    private readonly ISceneItemService _sceneItemService;
    private readonly GpuProcessingService _gpuProcessingService;
    private readonly ISceneSelectionGeometryProvider _geometryProvider;
    private readonly Lock _rendererLock = new();

    private SceneSelectionRenderer? _renderer;
    private bool _disposed;

    public SceneSelectionService(
        ILogger<SceneSelectionService> logger,
        IFramework framework,
        ISceneItemService sceneItemService,
        GpuProcessingService gpuProcessingService,
        ISceneSelectionGeometryProvider geometryProvider)
    {
        _logger = logger;
        _framework = framework;
        _sceneItemService = sceneItemService;
        _gpuProcessingService = gpuProcessingService;
        _geometryProvider = geometryProvider;
    }

    public bool TrySelectActiveItem(Vector2 viewportPos, Vector2 viewportSize, Vector2 mousePos, out SceneItemSnapshot snapshot)
    {
        snapshot = default!;

        if (_disposed
            || !TryCreateSelectionQuery(viewportPos, viewportSize, mousePos, out var query)
            || !TryCollectSelectionContext(out var context))
        {
            return false;
        }

        if (!_gpuProcessingService.TryEnterOperationScope(CancellationToken.None, GpuJobOptions.None, out var operationScope)
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

    private static bool TryCreateSelectionQuery(Vector2 viewportPos, Vector2 viewportSize, Vector2 mousePos, out SelectionQuery query)
    {
        query = default;
        if (viewportSize.X < 1f
            || viewportSize.Y < 1f
            || mousePos.X < viewportPos.X
            || mousePos.X >= viewportPos.X + viewportSize.X
            || mousePos.Y < viewportPos.Y
            || mousePos.Y >= viewportPos.Y + viewportSize.Y)
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
        context = FrameworkThreadUtility.Run(_framework, CollectSelectionContextUnsafe);
        return context.IsValid && context.Collector.HasDraws;
    }

    private SelectionContext CollectSelectionContextUnsafe()
    {
        if (!SceneViewportProjection.TryGetActiveCameraProjection(out var viewProjection, out var viewMatrix, out var nearPlane))
        {
            return SelectionContext.Invalid;
        }

        var reverseDepth = SceneViewportProjection.TryResolveReverseDepth(viewProjection, viewMatrix, nearPlane, out var resolvedReverseDepth)
            && resolvedReverseDepth;

        var collector = new SceneSelectionCollector();
        var selectableIds = _sceneItemService.GetPlacedItems()
            .Select(static snapshot => snapshot.Id)
            .ToHashSet();
        AppendSelectableDraws(
            _sceneItemService.GetRuntimeItems(),
            selectableIds,
            collector);

        return new SelectionContext(viewProjection, reverseDepth, collector);
    }

    internal static void AppendSelectableDraws(
        IReadOnlyList<ISceneItemRuntime> runtimes,
        IReadOnlySet<Guid> selectableIds,
        SceneSelectionCollector collector)
    {
        foreach (ISceneItemRuntime item in runtimes)
        {
            if (selectableIds.Contains(item.Snapshot.Id)
                && item is ISceneSelectableRuntime selectable)
            {
                selectable.AppendSelectionDraws(collector);
            }
        }
    }

    private bool TryExecuteSelectionQuery(SelectionContext context, SelectionQuery query, out SceneItemSnapshot snapshot)
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
            _logger.LogWarning(ex, "scene item gpu selection failed");
            snapshot = default!;
            return false;
        }
    }

    private bool TryEnsureRendererUnsafe(out SceneSelectionRenderer? renderer)
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
            renderer = new SceneSelectionRenderer(device, _geometryProvider);
            _renderer = renderer;
            device = null;
            return true;
        }
        catch (Exception ex)
        {
            device?.Dispose();
            _gpuProcessingService.NotifyOperationFailure(ex);
            _logger.LogWarning(ex, "scene selection renderer initialization failed");
            renderer = null;
            return false;
        }
        finally
        {
            D3D11ComReference.Release(ref d3d11Device);
        }
    }

    private void ResetRendererUnsafe()
    {
        _renderer?.Dispose();
        _renderer = null;
    }

    [StructLayout(LayoutKind.Auto)]
    private readonly record struct SelectionQuery(
        int ViewportWidth,
        int ViewportHeight,
        int PixelX,
        int PixelY);

    [StructLayout(LayoutKind.Auto)]
    private readonly record struct SelectionContext(
        Matrix4x4 ViewProjection,
        bool UseReverseDepth,
        SceneSelectionCollector Collector)
    {
        public static SelectionContext Invalid { get; } = new(default, false, new SceneSelectionCollector());

        public bool IsValid
            => ViewProjection != default;
    }
}

