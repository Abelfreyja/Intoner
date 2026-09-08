using Dalamud.Plugin.Services;
using Intoner.Scene;
using Intoner.Utils;
using System.Numerics;

namespace Intoner.Objects.Runtime;

/// <summary> resolves placement transforms and world collision hits for object placement </summary>
internal interface IObjectPlacementResolver
{
    /// <summary>
    /// Resolves a world collision hit from the given ray.
    /// </summary>
    /// <param name="rayOrigin">The ray origin in world space.</param>
    /// <param name="rayDirection">The normalized ray direction in world space.</param>
    /// <param name="hit">The resolved surface hit when available.</param>
    /// <returns>true when a collision surface was hit.</returns>
    bool TryResolveFromRay(Vector3 rayOrigin, Vector3 rayDirection, out SceneSurfaceHit hit);
}

internal sealed class ObjectPlacementResolver : IObjectPlacementResolver
{
    private readonly IFramework _framework;

    public ObjectPlacementResolver(IFramework framework)
        => _framework = framework;

    public bool TryResolveFromRay(Vector3 rayOrigin, Vector3 rayDirection, out SceneSurfaceHit hit)
    {
        var result = FrameworkThreadUtility.Run(_framework, () =>
        {
            return ObjectNativeSurfaceRaycaster.TryRaycastSurface(rayOrigin, rayDirection, out var resolvedHit)
                ? (Success: true, Hit: resolvedHit)
                : (Success: false, Hit: SceneSurfaceHit.Empty);
        });

        hit = result.Hit;
        return result.Success;
    }
}
