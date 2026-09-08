using Dalamud.Plugin.Services;
using Intoner.Objects.Utils;
using Intoner.Utils;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Intoner.Scene;

[StructLayout(LayoutKind.Auto)]
internal readonly record struct SceneSurfaceGeometryTarget(
    Guid ItemId,
    SceneGeometryRaycastTarget RaycastTarget);

internal sealed class SceneSurfaceTargetSnapshot
{
    public static SceneSurfaceTargetSnapshot Empty { get; } = new([]);

    public SceneSurfaceTargetSnapshot(IReadOnlyList<SceneSurfaceGeometryTarget> geometryTargets)
    {
        GeometryTargets = geometryTargets;
    }

    public IReadOnlyList<SceneSurfaceGeometryTarget> GeometryTargets { get; }

    public bool HasTargets
        => GeometryTargets.Count > 0;
}

/// <summary> captures and raycasts active local scene items used as surface drag targets </summary>
internal interface ISceneSurfaceService
{
    /// <summary> captures active persisted item geometry while excluding the items being dragged </summary>
    SceneSurfaceTargetSnapshot CaptureGeometryTargets(IReadOnlySet<Guid> excludedItemIds);

    /// <summary> raycasts a captured geometry target set </summary>
    bool TryRaycastGeometryTargets(
        SceneSurfaceTargetSnapshot targets,
        Vector3 rayOrigin,
        Vector3 rayDirection,
        float maxDistance,
        out SceneSurfaceHit hit);

    /// <summary> resolves the nearest native world surface hit </summary>
    bool TryResolveWorldHit(Vector3 rayOrigin, Vector3 rayDirection, out SceneSurfaceHit hit);
}

internal sealed class SceneSurfaceService : ISceneSurfaceService
{
    private readonly IFramework _framework;
    private readonly ISceneItemService _sceneItems;
    private readonly ISceneSelectionGeometryProvider _geometryProvider;
    private readonly IScenePlacementService _placementService;

    public SceneSurfaceService(
        IFramework framework,
        ISceneItemService sceneItems,
        ISceneSelectionGeometryProvider geometryProvider,
        IScenePlacementService placementService)
    {
        _framework = framework;
        _sceneItems = sceneItems;
        _geometryProvider = geometryProvider;
        _placementService = placementService;
    }

    public SceneSurfaceTargetSnapshot CaptureGeometryTargets(IReadOnlySet<Guid> excludedItemIds)
    {
        var draws = FrameworkThreadUtility.Run(_framework, () => CaptureDrawsUnsafe(excludedItemIds));
        return BuildTargetSnapshot(draws.ModelDraws, draws.PrimitiveDraws);
    }

    public bool TryRaycastGeometryTargets(
        SceneSurfaceTargetSnapshot targets,
        Vector3 rayOrigin,
        Vector3 rayDirection,
        float maxDistance,
        out SceneSurfaceHit hit)
    {
        hit = SceneSurfaceHit.Empty;
        if (!targets.HasTargets || !NumericsUtility.TryNormalize(rayDirection, out Vector3 normalizedDirection))
        {
            return false;
        }

        float closestDistance = SceneRaycastMath.ResolveMaxDistance(maxDistance);
        bool hasHit = false;
        foreach (SceneSurfaceGeometryTarget target in targets.GeometryTargets)
        {
            if (!SceneGeometryRaycaster.TryRaycastNormalized(
                    target.RaycastTarget,
                    rayOrigin,
                    normalizedDirection,
                    closestDistance,
                    out SceneSurfaceHit candidate))
            {
                continue;
            }

            closestDistance = candidate.Distance;
            hit = candidate with
            {
                Source = SceneSurfaceHitSource.ItemGeometry,
                TargetItemId = target.ItemId,
            };
            hasHit = true;
        }

        return hasHit;
    }

    public bool TryResolveWorldHit(Vector3 rayOrigin, Vector3 rayDirection, out SceneSurfaceHit hit)
        => _placementService.TryResolveFromRay(rayOrigin, rayDirection, out hit);

    private SceneSurfaceTargetSnapshot BuildTargetSnapshot(
        IReadOnlyList<SceneSelectionModelDraw> modelDraws,
        IReadOnlyList<SceneSelectionPrimitiveDraw> primitiveDraws)
    {
        if (modelDraws.Count == 0 && primitiveDraws.Count == 0)
        {
            return SceneSurfaceTargetSnapshot.Empty;
        }

        var targets = new List<SceneSurfaceGeometryTarget>(modelDraws.Count + primitiveDraws.Count);
        foreach (SceneSelectionModelDraw draw in modelDraws)
        {
            if (!_geometryProvider.TryGetRaycastGeometry(draw.ModelPath, out SceneSelectionGeometry geometry))
            {
                continue;
            }

            AddGeometryTarget(targets, draw.ItemId, geometry, draw.WorldTransform);
        }

        foreach (SceneSelectionPrimitiveDraw draw in primitiveDraws)
        {
            AddGeometryTarget(
                targets,
                draw.ItemId,
                SceneSelectionPrimitiveGeometry.Resolve(draw.PrimitiveKind),
                draw.WorldTransform);
        }

        return targets.Count > 0
            ? new SceneSurfaceTargetSnapshot(targets.ToArray())
            : SceneSurfaceTargetSnapshot.Empty;
    }

    private static void AddGeometryTarget(
        List<SceneSurfaceGeometryTarget> targets,
        Guid itemId,
        SceneSelectionGeometry geometry,
        Matrix4x4 worldTransform)
    {
        if (SceneGeometryRaycaster.TryCreateTarget(geometry, worldTransform, out SceneGeometryRaycastTarget target))
        {
            targets.Add(new SceneSurfaceGeometryTarget(itemId, target));
        }
    }

    private (SceneSelectionModelDraw[] ModelDraws, SceneSelectionPrimitiveDraw[] PrimitiveDraws) CaptureDrawsUnsafe(
        IReadOnlySet<Guid> excludedItemIds)
    {
        var collector = new SceneSelectionCollector();
        HashSet<Guid> targetItemIds = _sceneItems.GetPlacedItems()
            .Select(static snapshot => snapshot.Id)
            .ToHashSet();
        targetItemIds.ExceptWith(excludedItemIds);
        SceneSelectionService.AppendSelectableDraws(_sceneItems.GetRuntimeItems(), targetItemIds, collector);

        return collector.HasDraws
            ? (collector.ModelDraws.ToArray(), collector.PrimitiveDraws.ToArray())
            : (Array.Empty<SceneSelectionModelDraw>(), Array.Empty<SceneSelectionPrimitiveDraw>());
    }
}
