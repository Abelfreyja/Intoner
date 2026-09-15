using Dalamud.Plugin.Services;
using Intoner.Objects.Utils;
using Intoner.Services.Gpu;
using Intoner.Utils;
using Microsoft.Extensions.Logging;
using System.Numerics;
using System.Runtime.InteropServices;
using Device = SharpDX.Direct3D11.Device;

namespace Intoner.Scene;

/// <summary> queries active placed items using the offscreen GPU id pass </summary>
internal interface ISceneSelectionService
{
    /// <summary> resolves items covering pixels in a screen rectangle, with depth tested between selectable items </summary>
    /// <param name="viewportPos"> the top left viewport position in screen space </param>
    /// <param name="viewportSize"> the viewport size in pixels </param>
    /// <param name="start"> one rectangle corner, equal corners query one pixel </param>
    /// <param name="end"> the opposite corner, both corners are included and clipped to the viewport </param>
    /// <param name="snapshots"> distinct hits in scene item order, including locked items that can occlude other items </param>
    /// <returns> true for a completed query, including an empty result; false leaves selection unchanged </returns>
    bool TrySelectActiveItems(Vector2 viewportPos, Vector2 viewportSize, Vector2 start, Vector2 end,
        out IReadOnlyList<SceneItemSnapshot> snapshots);
}

/// <summary> describes a selection rectangle clipped to the viewport, with specific right and bottom pixel bounds </summary>
[StructLayout(LayoutKind.Auto)]
internal readonly record struct SceneSelectionQuery(int ViewportWidth, int ViewportHeight, int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;

    public static bool TryCreate(Vector2 viewportPos, Vector2 viewportSize, Vector2 start, Vector2 end, out SceneSelectionQuery query)
    {
        query = default;
        Vector2 viewportMax = viewportPos + viewportSize;
        if (!NumericsUtility.IsFinite(viewportPos) || !NumericsUtility.IsFinite(viewportSize)
            || !NumericsUtility.IsFinite(viewportMax) || !NumericsUtility.IsFinite(start) || !NumericsUtility.IsFinite(end)
            || viewportSize.X < 1f || viewportSize.Y < 1f || viewportSize.X >= int.MaxValue || viewportSize.Y >= int.MaxValue)
        {
            return false;
        }

        Vector2 min = Vector2.Min(start, end);
        Vector2 max = Vector2.Max(start, end);
        if (min.X >= viewportMax.X || min.Y >= viewportMax.Y || max.X < viewportPos.X || max.Y < viewportPos.Y)
        {
            return false;
        }

        min = Vector2.Max(min, viewportPos) - viewportPos;
        max = Vector2.Min(max, viewportMax) - viewportPos;
        int width = Math.Max(1, (int)MathF.Round(viewportSize.X));
        int height = Math.Max(1, (int)MathF.Round(viewportSize.Y));
        int left = Math.Clamp((int)MathF.Floor(min.X), 0, width - 1);
        int top = Math.Clamp((int)MathF.Floor(min.Y), 0, height - 1);
        query = new SceneSelectionQuery(width, height, left, top,
            Math.Clamp((int)MathF.Floor(max.X) + 1, left + 1, width),
            Math.Clamp((int)MathF.Floor(max.Y) + 1, top + 1, height));
        return true;
    }
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

    public bool TrySelectActiveItems(Vector2 viewportPos, Vector2 viewportSize, Vector2 start, Vector2 end,
        out IReadOnlyList<SceneItemSnapshot> snapshots)
    {
        snapshots = [];

        if (_disposed
            || !SceneSelectionQuery.TryCreate(viewportPos, viewportSize, start, end, out var query)
            || !TryCollectSelectionContext(out var context))
        {
            return false;
        }

        if (!context.Collector.HasDraws)
        {
            return true;
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
                return !_disposed && TryExecuteSelectionQuery(context, query, out snapshots);
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

    private bool TryCollectSelectionContext(out SelectionContext context)
    {
        context = FrameworkThreadUtility.Run(_framework, CollectSelectionContextUnsafe);
        return context.IsValid;
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

    private bool TryExecuteSelectionQuery(SelectionContext context, SceneSelectionQuery query,
        out IReadOnlyList<SceneItemSnapshot> snapshots)
    {
        snapshots = [];

        try
        {
            if (!TryEnsureRendererUnsafe(out var renderer) || renderer is null)
            {
                return false;
            }

            HashSet<uint> selectionIds = [];
            renderer.RenderSelectionIds(context.Collector, context.ViewProjection, context.UseReverseDepth, query, selectionIds);
            List<SceneItemSnapshot> hits = new(selectionIds.Count);
            foreach (uint selectionId in selectionIds.Order())
            {
                if (context.Collector.TryGetSnapshot(selectionId, out SceneItemSnapshot snapshot))
                {
                    hits.Add(snapshot);
                    renderer.TouchSelectionPaths(context.Collector, selectionId);
                }
            }

            snapshots = hits;
            return true;
        }
        catch (Exception ex)
        {
            _gpuProcessingService.NotifyOperationFailure(ex);
            ResetRendererUnsafe();
            _logger.LogWarning(ex, "scene item gpu selection failed");
            snapshots = [];
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

