using Dalamud.Plugin.Services;
using Intoner.Objects.Utils;
using System.Numerics;

namespace Intoner.Objects.Runtime;

internal readonly record struct ObjectSurfaceTargetGeometry(
    Guid ObjectId,
    ObjectSelectionGeometry Geometry,
    Matrix4x4 WorldTransform);

internal sealed class ObjectSurfaceTargetSnapshot
{
    public static ObjectSurfaceTargetSnapshot Empty { get; } = new([]);

    public ObjectSurfaceTargetSnapshot(IReadOnlyList<ObjectSurfaceTargetGeometry> geometryTargets)
    {
        GeometryTargets = geometryTargets;
    }

    public IReadOnlyList<ObjectSurfaceTargetGeometry> GeometryTargets { get; }

    public bool HasTargets
        => GeometryTargets.Count > 0;
}

/// <summary>
/// Captures and raycasts active object geometry that can be used as surface drag targets.
/// </summary>
internal interface IObjectSurfaceTargetService
{
    /// <summary>
    /// Captures current active object target draws, excluding objects that are being dragged.
    /// </summary>
    /// <param name="excludedObjectIds">The object ids to exclude from the captured target set.</param>
    /// <returns>The captured surface target snapshot.</returns>
    ObjectSurfaceTargetSnapshot CaptureTargets(IReadOnlyCollection<Guid> excludedObjectIds);

    /// <summary>
    /// Raycasts the given captured target set against model and primitive geometry.
    /// </summary>
    /// <param name="targets">The captured target set.</param>
    /// <param name="rayOrigin">The ray origin in world space.</param>
    /// <param name="rayDirection">The ray direction in world space.</param>
    /// <param name="maxDistance">The maximum accepted hit distance.</param>
    /// <param name="hit">The resolved surface hit when found.</param>
    /// <returns>true when the ray hit captured target geometry.</returns>
    bool TryRaycastGeometryTargets(
        ObjectSurfaceTargetSnapshot targets,
        Vector3 rayOrigin,
        Vector3 rayDirection,
        float maxDistance,
        out ObjectSurfaceHit hit);
}

internal sealed class ObjectSurfaceTargetService : IObjectSurfaceTargetService
{
    private readonly IFramework _framework;
    private readonly IObjectSceneState _sceneState;
    private readonly ObjectSelectionGeometryCache _geometryCache;

    public ObjectSurfaceTargetService(
        IFramework framework,
        IObjectSceneState sceneState,
        ObjectSelectionGeometryCache geometryCache)
    {
        _framework = framework;
        _sceneState = sceneState;
        _geometryCache = geometryCache;
    }

    public ObjectSurfaceTargetSnapshot CaptureTargets(IReadOnlyCollection<Guid> excludedObjectIds)
    {
        HashSet<Guid> excluded = excludedObjectIds.Count > 0
            ? excludedObjectIds.ToHashSet()
            : [];
        var draws = ObjectFrameworkUtility.RunOnFrameworkThread(_framework, () => CaptureDrawsUnsafe(excluded));
        return BuildTargetSnapshot(draws.ModelDraws, draws.PrimitiveDraws);
    }

    public bool TryRaycastGeometryTargets(
        ObjectSurfaceTargetSnapshot targets,
        Vector3 rayOrigin,
        Vector3 rayDirection,
        float maxDistance,
        out ObjectSurfaceHit hit)
    {
        hit = ObjectSurfaceHit.Empty;
        if (!targets.HasTargets || !ObjectMathUtility.TryNormalize(rayDirection, out var normalizedDirection))
        {
            return false;
        }

        var closestDistance = ObjectRaycastMath.ResolveMaxDistance(maxDistance);
        var hasHit = false;
        foreach (var target in targets.GeometryTargets)
        {
            if (!ObjectGeometryRaycaster.TryRaycastNormalized(
                    target.Geometry,
                    target.WorldTransform,
                    rayOrigin,
                    normalizedDirection,
                    closestDistance,
                    out var candidateHit))
            {
                continue;
            }

            closestDistance = candidateHit.Distance;
            hit = candidateHit with
            {
                Source = ObjectSurfaceHitSource.ObjectGeometry,
                TargetObjectId = target.ObjectId,
            };
            hasHit = true;
        }

        return hasHit;
    }

    private ObjectSurfaceTargetSnapshot BuildTargetSnapshot(
        IReadOnlyList<ObjectSelectionModelDraw> modelDraws,
        IReadOnlyList<ObjectSelectionPrimitiveDraw> primitiveDraws)
    {
        if (modelDraws.Count == 0 && primitiveDraws.Count == 0)
        {
            return ObjectSurfaceTargetSnapshot.Empty;
        }

        var targets = new List<ObjectSurfaceTargetGeometry>(modelDraws.Count + primitiveDraws.Count);
        foreach (var draw in modelDraws)
        {
            if (!_geometryCache.TryGetGeometry(draw.ModelPath, out var geometry))
            {
                continue;
            }

            targets.Add(new ObjectSurfaceTargetGeometry(draw.ObjectId, geometry, draw.WorldTransform));
        }

        foreach (var draw in primitiveDraws)
        {
            targets.Add(new ObjectSurfaceTargetGeometry(
                draw.ObjectId,
                ObjectSelectionPrimitiveGeometry.Resolve(draw.PrimitiveKind),
                draw.WorldTransform));
        }

        return targets.Count > 0
            ? new ObjectSurfaceTargetSnapshot(targets.ToArray())
            : ObjectSurfaceTargetSnapshot.Empty;
    }

    private (ObjectSelectionModelDraw[] ModelDraws, ObjectSelectionPrimitiveDraw[] PrimitiveDraws) CaptureDrawsUnsafe(IReadOnlySet<Guid> excludedObjectIds)
    {
        var collector = new ObjectSelectionCollector();
        foreach (var entry in _sceneState.GetEntriesSnapshot())
        {
            if (excludedObjectIds.Contains(entry.Snapshot.Id))
            {
                continue;
            }

            entry.SceneObject.AppendSelectionDraws(collector);
        }

        return collector.HasDraws
            ? (collector.ModelDraws.ToArray(), collector.PrimitiveDraws.ToArray())
            : (Array.Empty<ObjectSelectionModelDraw>(), Array.Empty<ObjectSelectionPrimitiveDraw>());
    }
}

