using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine.Layer;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;
using Intoner.Objects.Filesystem.Configuration;
using Intoner.Objects.Utils;
using System.Numerics;
using CollisionSceneWrapper = FFXIVClientStructs.FFXIV.Common.Component.BGCollision.SceneWrapper;

namespace Intoner.Objects.Runtime;

internal readonly record struct NativeHousingPlacementState(
    ObjectHousingArea? CurrentArea,
    HousingPlacementBlock Block,
    bool HasCollisionScene);

internal sealed class NativePlacementQuery(
    IFramework framework,
    IObjectTable objectTable,
    NativePlacementCollisionQuery collisionQuery)
{
    private const ulong MapRangeCollisionLayerMask = 8;

    public bool TryRaycast(Vector3 rayOrigin, Vector3 rayDirection, float maxDistance, out ObjectSurfaceHit hit)
    {
        (bool Success, ObjectSurfaceHit Hit) result = ObjectFrameworkUtility.RunOnFrameworkThread(framework, () =>
        {
            return collisionQuery.TryRaycast(rayOrigin, rayDirection, maxDistance, out ObjectSurfaceHit resolvedHit)
                ? (Success: true, Hit: resolvedHit)
                : (Success: false, Hit: new ObjectSurfaceHit(Vector3.Zero, Vector3.Zero));
        });

        hit = result.Hit;
        return result.Success;
    }

    public bool TryResolveFloorPlacementFromRay(
        Vector3 rayOrigin,
        Vector3 rayDirection,
        float radius,
        out ObjectSurfaceHit hit)
    {
        hit = new ObjectSurfaceHit(Vector3.Zero, Vector3.Zero);
        if (!ObjectMathUtility.TryNormalize(rayDirection, out Vector3 normalizedDirection))
        {
            return false;
        }

        (bool Success, ObjectSurfaceHit Hit) result = ObjectFrameworkUtility.RunOnFrameworkThread(framework, () =>
            TryResolveFloorPlacementFromRayOnFramework(rayOrigin, normalizedDirection, radius));

        hit = result.Hit;
        return result.Success;
    }

    private (bool Success, ObjectSurfaceHit Hit) TryResolveFloorPlacementFromRayOnFramework(
        Vector3 rayOrigin,
        Vector3 normalizedDirection,
        float radius)
    {
        if (!collisionQuery.TryRaycast(
                rayOrigin,
                normalizedDirection,
                PlacementValidationConstants.NativeRayMaxDistance,
                out ObjectSurfaceHit rayHit))
        {
            return (false, new ObjectSurfaceHit(Vector3.Zero, Vector3.Zero));
        }

        if (!float.IsFinite(radius) || radius <= ObjectMathUtility.ScalarEpsilon)
        {
            return (true, rayHit);
        }

        Vector3 sweepOrigin = ResolveFloorSweepOrigin(rayHit.Point, normalizedDirection, radius);
        if (!collisionQuery.TrySweepSphere(
                sweepOrigin,
                normalizedDirection,
                radius,
                PlacementValidationConstants.NativeRayMaxDistance,
                out ObjectSurfaceHit sweepHit,
                out _))
        {
            return (false, rayHit);
        }

        ulong material = rayHit.HasMaterial(PlacementSurfacePolicy.FloorMaterial)
            ? sweepHit.Material
            : rayHit.Material;
        return (true, sweepHit with
        {
            Point = sweepHit.Point - (Vector3.UnitY * radius),
            Material = material,
            Distance = rayHit.Distance,
        });
    }

    public bool TryRaycastMaterialMask(
        Vector3 rayOrigin,
        Vector3 rayDirection,
        float maxDistance,
        ulong materialMask,
        out ObjectSurfaceHit hit)
    {
        (bool Success, ObjectSurfaceHit Hit) result = ObjectFrameworkUtility.RunOnFrameworkThread(framework, () =>
        {
            return collisionQuery.TryRaycastMaterialMask(
                    rayOrigin,
                    rayDirection,
                    maxDistance,
                    materialMask,
                    out ObjectSurfaceHit resolvedHit)
                ? (Success: true, Hit: resolvedHit)
                : (Success: false, Hit: new ObjectSurfaceHit(Vector3.Zero, Vector3.Zero));
        });

        hit = result.Hit;
        return result.Success && hit.Material != 0;
    }

    public NativeHousingPlacementState ResolveCurrentHousingState()
        => ObjectFrameworkUtility.RunOnFrameworkThread(framework, ResolveCurrentHousingStateOnFramework);

    public PlacementValidationStatus CheckPlacementAreaContainment(HousingPlacementContext context, Vector3 position)
    {
        if (!context.CanCheckContainment || context.HousingBlockId is not { } blockId)
        {
            return PlacementValidationStatus.Unknown;
        }

        return ObjectFrameworkUtility.RunOnFrameworkThread(framework, () => CheckPlacementAreaContainmentOnFramework(blockId, position));
    }

    private unsafe PlacementValidationStatus CheckPlacementAreaContainmentOnFramework(byte blockId, Vector3 position)
    {
        if (!ObjectSurfaceRaycastUtility.HasCollisionScene())
        {
            return PlacementValidationStatus.Unknown;
        }

        return TryFindContainingHousingBlock(position, blockId)
            ? PlacementValidationStatus.Valid
            : PlacementValidationStatus.Invalid;
    }

    private unsafe NativeHousingPlacementState ResolveCurrentHousingStateOnFramework()
        => new(
            TryResolveCurrentHousingAreaOnFramework(out ObjectHousingArea area) ? area : null,
            ResolveCurrentHousingBlockOnFramework(),
            ObjectSurfaceRaycastUtility.HasCollisionScene());

    private static unsafe bool TryResolveCurrentHousingAreaOnFramework(out ObjectHousingArea area)
    {
        area = default;
        HousingManager* housingManager = HousingManager.Instance();
        if (housingManager != null)
        {
            if (housingManager->IsInside())
            {
                area = ObjectHousingArea.Indoor;
                return true;
            }

            if (housingManager->IsOutside())
            {
                area = ObjectHousingArea.Outdoor;
                return true;
            }
        }

        if (IsIndoorHousingLayout())
        {
            area = ObjectHousingArea.Indoor;
            return true;
        }

        return false;
    }

    private unsafe HousingPlacementBlock ResolveCurrentHousingBlockOnFramework()
    {
        if (TryResolvePlayerHousingBlock(out byte playerBlockId))
        {
            return new HousingPlacementBlock(playerBlockId, HousingPlacementBlockSource.PlayerMapRange);
        }

        if (TryResolveCurrentPlotBlock(out byte currentPlotBlockId))
        {
            return new HousingPlacementBlock(currentPlotBlockId, HousingPlacementBlockSource.CurrentPlot);
        }

        return HousingPlacementBlock.Unavailable;
    }

    private unsafe bool TryResolvePlayerHousingBlock(out byte blockId)
    {
        blockId = 0;

        IPlayerCharacter? player = objectTable.LocalPlayer;
        if (player != null)
        {
            if (TryFindContainingPlayerHousingBlock(player.Position, out blockId))
            {
                return true;
            }
        }

        return false;
    }

    private static unsafe bool TryResolveCurrentPlotBlock(out byte blockId)
    {
        blockId = 0;
        HousingManager* housingManager = HousingManager.Instance();
        sbyte currentPlot = housingManager != null
            ? housingManager->GetCurrentPlot()
            : (sbyte)-1;
        if (currentPlot >= 0)
        {
            blockId = unchecked((byte)currentPlot);
            return true;
        }

        return false;
    }

    private unsafe bool TryFindContainingPlayerHousingBlock(Vector3 position, out byte blockId)
    {
        blockId = 0;
        if (!TryResolveContainingColliders(position, out CollisionSceneWrapper.ColliderList colliderList))
        {
            return false;
        }

        foreach (var colliderPointer in colliderList.Colliders[..colliderList.Count])
        {
            if (!TryResolvePlacementMapRange(colliderPointer.Value, out MapRangeLayoutInstance* mapRange))
            {
                continue;
            }

            blockId = mapRange->HousingBlockId;
            return true;
        }

        return false;
    }

    private unsafe bool TryFindContainingHousingBlock(Vector3 position, byte blockId)
    {
        if (!TryResolveContainingColliders(position, out CollisionSceneWrapper.ColliderList colliderList))
        {
            return false;
        }

        foreach (var colliderPointer in colliderList.Colliders[..colliderList.Count])
        {
            if (!TryResolvePlacementMapRange(colliderPointer.Value, out MapRangeLayoutInstance* mapRange))
            {
                continue;
            }

            if (mapRange->HousingBlockId == blockId)
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryResolveContainingColliders(
        Vector3 position,
        out CollisionSceneWrapper.ColliderList colliderList)
        => ObjectSurfaceRaycastUtility.TryFindContainingColliders(position, MapRangeCollisionLayerMask, out colliderList);

    private static Vector3 ResolveFloorSweepOrigin(Vector3 rayHitPoint, Vector3 normalizedDirection, float radius)
    {
        Vector3 sweepOrigin = rayHitPoint - (normalizedDirection * radius) + (Vector3.UnitY * radius);
        if (IsIndoorHousingLayout())
        {
            return sweepOrigin;
        }

        float heightFromHit = radius + sweepOrigin.Y - rayHitPoint.Y;
        float outdoorAdjustment = heightFromHit - 6f;
        return outdoorAdjustment > 0f
            ? sweepOrigin + (normalizedDirection * outdoorAdjustment)
            : sweepOrigin;
    }

    private static unsafe bool IsIndoorHousingLayout()
    {
        LayoutWorld* layoutWorld = LayoutWorld.Instance();
        LayoutManager* activeLayout = layoutWorld != null
            ? layoutWorld->ActiveLayout
            : null;
        return activeLayout != null && activeLayout->HousingType == 1;
    }

    private static unsafe bool TryResolvePlacementMapRange(Collider* collider, out MapRangeLayoutInstance* mapRange)
    {
        mapRange = null;
        if (collider == null)
        {
            return false;
        }

        ILayoutInstance* instance = LayoutWorld.GetColliderLayoutInstance(InstanceType.MapRange, collider);
        if (instance == null)
        {
            return false;
        }

        mapRange = (MapRangeLayoutInstance*)instance;
        return IsPlacementMapRange(mapRange);
    }

    private static unsafe bool IsPlacementMapRange(MapRangeLayoutInstance* mapRange)
        => mapRange != null
            && (mapRange->MapRangeFlags2 & MapRangeFlag2.HousingEnabled) == MapRangeFlag2.HousingEnabled
            && ((byte)mapRange->MapRangeFlags1 & 0x0F) == 0
            && mapRange->HousingBlockId != byte.MaxValue;
}
