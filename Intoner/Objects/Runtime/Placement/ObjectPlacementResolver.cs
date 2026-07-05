using Dalamud.Plugin.Services;
using Intoner.Objects.Models;
using Intoner.Objects.Utils;
using System.Numerics;

namespace Intoner.Objects.Runtime;

/// <summary>
/// Resolves placement transforms and world collision hits for object placement.
/// </summary>
internal interface IObjectPlacementResolver
{
    /// <summary>
    /// Resolves a default placement transform from the local player position and facing.
    /// </summary>
    /// <param name="transform">The resolved placement transform when available.</param>
    /// <returns>true when the player was available and a placement transform could be resolved.</returns>
    bool TryResolveFromPlayer(out ObjectTransform transform);

    /// <summary>
    /// Resolves a world collision hit from the given ray.
    /// </summary>
    /// <param name="rayOrigin">The ray origin in world space.</param>
    /// <param name="rayDirection">The normalized ray direction in world space.</param>
    /// <param name="hit">The resolved surface hit when available.</param>
    /// <returns>true when a collision surface was hit.</returns>
    bool TryResolveFromRay(Vector3 rayOrigin, Vector3 rayDirection, out ObjectSurfaceHit hit);
}

internal sealed class ObjectPlacementResolver : IObjectPlacementResolver
{
    private readonly IFramework _framework;
    private readonly IObjectTable _objectTable;

    private const float PlacementDistance = 2f;

    public ObjectPlacementResolver(IFramework framework, IObjectTable objectTable)
    {
        _framework = framework;
        _objectTable = objectTable;
    }

    public bool TryResolveFromPlayer(out ObjectTransform transform)
    {
        var result = ObjectFrameworkUtility.RunOnFrameworkThread(_framework, () =>
        {
            var player = _objectTable.LocalPlayer;
            if (player == null)
            {
                return (Success: false, Transform: new ObjectTransform());
            }

            var forward = new Vector3(MathF.Sin(player.Rotation), 0f, MathF.Cos(player.Rotation));
            var placementPosition = player.Position + (forward * PlacementDistance);
            var placementRotation = new Vector3(0f, player.Rotation * (180f / MathF.PI), 0f);
            return (Success: true, Transform: new ObjectTransform
            {
                Position = placementPosition,
                RotationDegrees = placementRotation,
            });
        });

        transform = result.Transform;
        return result.Success;
    }

    public bool TryResolveFromRay(Vector3 rayOrigin, Vector3 rayDirection, out ObjectSurfaceHit hit)
    {
        var result = ObjectFrameworkUtility.RunOnFrameworkThread(_framework, () =>
        {
            return ObjectNativeSurfaceRaycaster.TryRaycastSurface(rayOrigin, rayDirection, out var resolvedHit)
                ? (Success: true, Hit: resolvedHit)
                : (Success: false, Hit: ObjectSurfaceHit.Empty);
        });

        hit = result.Hit;
        return result.Success;
    }
}
