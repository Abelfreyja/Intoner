using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Intoner.Objects.Utils;

internal enum ObjectSurfaceHitSource
{
    Unknown,
    Native,
    ObjectBounds,
    ObjectGeometry,
}

internal readonly record struct ObjectSurfaceHit(
    Vector3 Point,
    Vector3 Normal,
    ulong Material = 0,
    nint ColliderAddress = 0,
    float Distance = 0f,
    ObjectSurfaceHitSource Source = ObjectSurfaceHitSource.Unknown,
    Guid TargetObjectId = default)
{
    public bool HasCollider
        => ColliderAddress != 0;

    public bool HasObjectTarget
        => TargetObjectId != Guid.Empty
           && Source is ObjectSurfaceHitSource.ObjectBounds or ObjectSurfaceHitSource.ObjectGeometry;

    public bool HasMaterial(ulong materialMask)
        => (Material & materialMask) != 0;
}

internal static class ObjectSurfaceRaycastUtility
{
    public const float MinimumHitDistance = 0.001f;

    public static unsafe bool TryBuildScreenRay(
        Vector2 viewportPos,
        Vector2 viewportSize,
        Vector2 mousePosition,
        out Vector3 rayOrigin,
        out Vector3 rayDirection)
    {
        rayOrigin = default;
        rayDirection = default;

        var viewportMouse = mousePosition - viewportPos;
        if (viewportMouse.X < 0f
            || viewportMouse.Y < 0f
            || viewportMouse.X > viewportSize.X
            || viewportMouse.Y > viewportSize.Y)
        {
            return false;
        }

        var control = Control.Instance();
        if (control == null)
        {
            return false;
        }

        var activeCamera = control->CameraManager.GetActiveCamera();
        if (activeCamera == null)
        {
            return false;
        }

        var ray = activeCamera->SceneCamera.ScreenPointToRay(viewportMouse);
        var direction = new Vector3(ray.Direction.X, ray.Direction.Y, ray.Direction.Z);
        if (!ObjectMathUtility.TryNormalize(direction, out rayDirection))
        {
            return false;
        }

        rayOrigin = new Vector3(ray.Origin.X, ray.Origin.Y, ray.Origin.Z);
        return true;
    }

    public static unsafe bool TryRaycastSurface(
        Vector3 rayOrigin,
        Vector3 rayDirection,
        out ObjectSurfaceHit hit,
        float maxDistance = 1000000f)
    {
        hit = new ObjectSurfaceHit(Vector3.Zero, Vector3.Zero);

        if (!ObjectMathUtility.TryNormalize(rayDirection, out var normalizedDirection))
        {
            return false;
        }

        if (!BGCollisionModule.RaycastMaterialFilter(rayOrigin, normalizedDirection, out var raycastHit, maxDistance))
        {
            return false;
        }

        hit = new ObjectSurfaceHit(
            raycastHit.Point,
            ResolveSurfaceNormal(raycastHit, normalizedDirection),
            raycastHit.Material,
            (nint)raycastHit.Object,
            raycastHit.Distance,
            ObjectSurfaceHitSource.Native);
        return true;
    }

    public static unsafe bool TryFindContainingColliders(
        Vector3 position,
        ulong layerMask,
        out SceneWrapper.ColliderList colliderList)
    {
        colliderList = default;
        if (!TryResolveCollisionScene(out BGCollisionModule* collisionModule, out SceneWrapper* sceneWrapper))
        {
            return false;
        }

        SceneWrapper.ColliderList resolvedColliders = default;
        var hasColliders = false;
        var queryPosition = position;
        nint lockAddress = AcquireCollisionReadLock(collisionModule);
        try
        {
            hasColliders = sceneWrapper->FindContainingCollidersCheckLayer(&resolvedColliders, layerMask, &queryPosition)
                           && resolvedColliders.Count > 0;
        }
        finally
        {
            ReleaseSRWLockShared(lockAddress);
        }

        if (!hasColliders)
        {
            return false;
        }

        colliderList = resolvedColliders;
        return true;
    }

    public static unsafe bool HasCollisionScene()
        => TryResolveCollisionScene(out _, out _);

    public static unsafe bool TryResolveCollisionModule(out BGCollisionModule* collisionModule)
        => TryResolveCollisionScene(out collisionModule, out _);

    private static unsafe bool TryResolveCollisionScene(out BGCollisionModule* collisionModule, out SceneWrapper* sceneWrapper)
    {
        collisionModule = null;
        sceneWrapper = null;
        Framework* framework = Framework.Instance();
        if (framework == null || framework->BGCollisionModule == null)
        {
            return false;
        }

        BGCollisionModule* resolvedCollisionModule = framework->BGCollisionModule;
        SceneManager* sceneManager = resolvedCollisionModule->SceneManager;
        if (sceneManager == null || sceneManager->FirstScene == null || sceneManager->FirstScene->Scene == null)
        {
            return false;
        }

        collisionModule = resolvedCollisionModule;
        sceneWrapper = sceneManager->FirstScene;
        return true;
    }

    private static unsafe nint AcquireCollisionReadLock(BGCollisionModule* collisionModule)
    {
        nint lockAddress = (nint)(&collisionModule->UpdateTaskLock);
        AcquireSRWLockShared(lockAddress);
        return lockAddress;
    }

    public static Vector3 ResolveSurfaceNormal(in RaycastHit raycastHit, Vector3 rayDirection)
    {
        var triangleNormal = ComputeTriangleNormal(raycastHit.V1, raycastHit.V2, raycastHit.V3);
        if (ObjectMathUtility.HasLength(triangleNormal))
        {
            return OrientSurfaceNormal(triangleNormal, rayDirection);
        }

        if (ObjectMathUtility.TryNormalize(raycastHit.Normal, out var normalizedNormal))
        {
            return OrientSurfaceNormal(normalizedNormal, rayDirection);
        }

        return Vector3.Zero;
    }

    /// <summary> intersects a ray against one plane </summary>
    /// <param name="rayOrigin">the ray origin in world space</param>
    /// <param name="rayDirection">the ray direction in world space</param>
    /// <param name="planePoint">any point on the plane</param>
    /// <param name="planeNormal">the plane normal</param>
    /// <param name="intersectionPoint">the resolved hit point when an intersection exists in front of the ray</param>
    /// <returns>true when the ray intersects the plane in front of the origin</returns>
    public static bool TryIntersectRayPlane(
        Vector3 rayOrigin,
        Vector3 rayDirection,
        Vector3 planePoint,
        Vector3 planeNormal,
        out Vector3 intersectionPoint)
    {
        intersectionPoint = default;
        if (!ObjectMathUtility.HasLength(rayDirection) || !ObjectMathUtility.HasLength(planeNormal))
        {
            return false;
        }

        var denominator = Vector3.Dot(rayDirection, planeNormal);
        if (ObjectMathUtility.IsNearlyZero(denominator))
        {
            return false;
        }

        var distance = Vector3.Dot(planePoint - rayOrigin, planeNormal) / denominator;
        if (distance < 0f)
        {
            return false;
        }

        intersectionPoint = rayOrigin + (rayDirection * distance);
        return true;
    }

    private static Vector3 ComputeTriangleNormal(Vector3 v1, Vector3 v2, Vector3 v3)
    {
        var edge1 = v2 - v1;
        var edge2 = v3 - v1;
        var normal = Vector3.Cross(edge1, edge2);
        return ObjectMathUtility.TryNormalize(normal, out var normalizedNormal)
            ? normalizedNormal
            : Vector3.Zero;
    }

    public static Vector3 OrientSurfaceNormal(Vector3 normal, Vector3 rayDirection)
    {
        if (!ObjectMathUtility.TryNormalize(normal, out var orientedNormal))
        {
            return Vector3.Zero;
        }

        return Vector3.Dot(orientedNormal, rayDirection) > 0f
            ? -orientedNormal
            : orientedNormal;
    }

    public static float ResolveMaxDistance(float maxDistance)
        => float.IsFinite(maxDistance) && maxDistance > MinimumHitDistance
            ? maxDistance
            : float.PositiveInfinity;

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern void AcquireSRWLockShared(nint srwLock);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern void ReleaseSRWLockShared(nint srwLock);
}

