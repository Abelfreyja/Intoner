using Dalamud.Plugin.Services;
using Intoner.Objects.Runtime;
using Intoner.Utils;
using System.Numerics;

namespace Intoner.Scene;

/// <summary> resolves default transforms for newly placed scene items </summary>
internal interface IScenePlacementService
{
    /// <summary> resolves a default placement transform from the local player position and facing </summary>
    bool TryResolveFromPlayer(out SceneTransform transform);

    /// <summary> resolves the nearest native world surface hit from a ray </summary>
    bool TryResolveFromRay(Vector3 rayOrigin, Vector3 rayDirection, out SceneSurfaceHit hit);
}

internal sealed class ScenePlacementService(
    IFramework framework,
    IObjectTable objectTable,
    IObjectPlacementResolver objectPlacementResolver) : IScenePlacementService
{
    private const float PlacementDistance = 2f;

    public bool TryResolveFromPlayer(out SceneTransform transform)
    {
        (bool Success, SceneTransform Transform) result = FrameworkThreadUtility.Run(framework, () =>
        {
            var player = objectTable.LocalPlayer;
            if (player is null)
            {
                return (false, new SceneTransform());
            }

            Vector3 forward = new(MathF.Sin(player.Rotation), 0f, MathF.Cos(player.Rotation));
            return (true, new SceneTransform
            {
                Position = player.Position + (forward * PlacementDistance),
                RotationDegrees = new Vector3(0f, player.Rotation * (180f / MathF.PI), 0f),
            });
        });

        transform = result.Transform;
        return result.Success;
    }

    public bool TryResolveFromRay(Vector3 rayOrigin, Vector3 rayDirection, out SceneSurfaceHit hit)
        => objectPlacementResolver.TryResolveFromRay(rayOrigin, rayDirection, out hit);
}
