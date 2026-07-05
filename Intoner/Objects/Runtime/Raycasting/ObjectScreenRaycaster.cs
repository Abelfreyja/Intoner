using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Graphics;
using Intoner.Objects.Utils;
using System.Numerics;

namespace Intoner.Objects.Runtime;

internal static class ObjectScreenRaycaster
{
    public static unsafe bool TryBuildScreenRay(
        Vector2 viewportPos,
        Vector2 viewportSize,
        Vector2 mousePosition,
        out Vector3 rayOrigin,
        out Vector3 rayDirection)
    {
        rayOrigin = default;
        rayDirection = default;

        Vector2 viewportMouse = mousePosition - viewportPos;
        if (viewportMouse.X < 0f
            || viewportMouse.Y < 0f
            || viewportMouse.X > viewportSize.X
            || viewportMouse.Y > viewportSize.Y)
        {
            return false;
        }

        Control* control = Control.Instance();
        if (control == null)
        {
            return false;
        }

        Camera* activeCamera = control->CameraManager.GetActiveCamera();
        if (activeCamera == null)
        {
            return false;
        }

        Ray ray = activeCamera->SceneCamera.ScreenPointToRay(viewportMouse);
        Vector3 direction = new(ray.Direction.X, ray.Direction.Y, ray.Direction.Z);
        if (!ObjectMathUtility.TryNormalize(direction, out rayDirection))
        {
            return false;
        }

        rayOrigin = new Vector3(ray.Origin.X, ray.Origin.Y, ray.Origin.Z);
        return true;
    }
}
