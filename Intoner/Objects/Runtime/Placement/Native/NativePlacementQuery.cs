using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using Intoner.Objects.Utils;
using Intoner.Scene;
using Intoner.Services.Configuration;
using Intoner.Utils;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;

namespace Intoner.Objects.Runtime;

[SuppressMessage("Maintainability", "MA0048:File name must match type name", Justification = "Keep the native placement state colocated with its query boundary.")]
internal readonly record struct NativeHousingPlacementState(
    ObjectHousingArea? CurrentArea,
    HousingPlacementBlock Block,
    byte? CurrentPlotIndex,
    bool HasCollisionScene);

internal sealed class NativePlacementQuery(
    IFramework framework,
    IObjectTable objectTable,
    NativePlacementCollisionQuery collisionQuery,
    NativePlacementAreaQuery areaQuery,
    IObjectSceneState sceneState)
{
    public bool TryRaycast(PlacementSurfaceRaycastRequest request, out SceneSurfaceHit hit)
    {
        (bool Success, SceneSurfaceHit Hit) result = FrameworkThreadUtility.Run(framework, () =>
        {
            SceneSurfaceHit resolvedHit;
            bool success = request.NativeMaterialMask == 0
                ? collisionQuery.TryRaycast(request.Origin, request.Direction, request.MaxDistance, out resolvedHit)
                : collisionQuery.TryRaycastMaterialMask(request.Origin, request.Direction, request.MaxDistance,
                    request.NativeMaterialMask, out resolvedHit) && resolvedHit.Material != 0;
            return success && !IsExcludedSurfaceOnFramework(request.ObjectId, resolvedHit)
                ? (Success: true, Hit: resolvedHit)
                : (Success: false, Hit: SceneSurfaceHit.Empty);
        });

        hit = result.Hit;
        return result.Success;
    }

    public bool TryResolveFloorPlacementFromRay(
        Vector3 rayOrigin,
        Vector3 rayDirection,
        float radius,
        out SceneSurfaceHit hit)
    {
        hit = SceneSurfaceHit.Empty;
        if (!NumericsUtility.TryNormalize(rayDirection, out Vector3 normalizedDirection))
        {
            return false;
        }

        (bool Success, SceneSurfaceHit Hit) result = FrameworkThreadUtility.Run(framework, () =>
            TryResolveFloorPlacementFromRayOnFramework(rayOrigin, normalizedDirection, radius));

        hit = result.Hit;
        return result.Success;
    }

    private (bool Success, SceneSurfaceHit Hit) TryResolveFloorPlacementFromRayOnFramework(
        Vector3 rayOrigin,
        Vector3 normalizedDirection,
        float radius)
    {
        if (!collisionQuery.TryRaycast(
                rayOrigin,
                normalizedDirection,
                PlacementValidationConstants.NativeRayMaxDistance,
                out SceneSurfaceHit rayHit))
        {
            return (false, SceneSurfaceHit.Empty);
        }

        if (!float.IsFinite(radius) || radius <= NumericsUtility.ScalarEpsilon)
        {
            return (true, rayHit);
        }

        Vector3 sweepOrigin = ResolveFloorSweepOrigin(rayHit.Point, normalizedDirection, radius);
        if (!collisionQuery.TrySweepSphere(
                sweepOrigin,
                normalizedDirection,
                radius,
                PlacementValidationConstants.NativeRayMaxDistance,
                out SceneSurfaceHit sweepHit,
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

    internal bool IsExcludedSurfaceOnFramework(Guid objectId, SceneSurfaceHit hit)
    {
        if (!framework.IsInFrameworkUpdateThread)
        {
            throw new InvalidOperationException("collider ownership must be checked on the framework thread");
        }

        return objectId != Guid.Empty
            && hit.Source == SceneSurfaceHitSource.Native
            && hit.ColliderAddress != 0
            && sceneState.TryGetEntry(objectId, out ObjectSceneEntry entry)
            && entry.Runtime.ContainsCollider(hit.ColliderAddress);
    }

    public NativeHousingPlacementState ResolveCurrentHousingState()
        => FrameworkThreadUtility.Run(framework, ResolveCurrentHousingStateOnFramework);

    public PlacementValidationStatus CheckPlacementAreaContainment(HousingPlacementContext context, Vector3 position)
    {
        if (!context.CanCheckContainment || context.HousingBlockId is not { } blockId)
        {
            return PlacementValidationStatus.Unknown;
        }

        return FrameworkThreadUtility.Run(framework, () =>
            context.CurrentArea == ObjectHousingArea.Indoor
                ? areaQuery.CheckCurrentBlock(position, blockId)
                : areaQuery.CheckCurrentPlot(position));
    }

    private NativeHousingPlacementState ResolveCurrentHousingStateOnFramework()
    {
        byte? currentPlotIndex = TryResolveCurrentPlotBlock(out byte blockId) ? blockId : null;
        return new NativeHousingPlacementState(
            TryResolveCurrentHousingAreaOnFramework(out ObjectHousingArea area) ? area : null,
            ResolveCurrentHousingBlockOnFramework(currentPlotIndex),
            currentPlotIndex,
            ObjectCollisionSceneQuery.HasScene());
    }

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

    private HousingPlacementBlock ResolveCurrentHousingBlockOnFramework(byte? currentPlotIndex)
    {
        if (TryResolvePlayerHousingBlock(out byte playerBlockId))
        {
            return new HousingPlacementBlock(playerBlockId, HousingPlacementBlockSource.PlayerMapRange);
        }

        if (currentPlotIndex is { } currentPlotBlockId)
        {
            return new HousingPlacementBlock(currentPlotBlockId, HousingPlacementBlockSource.CurrentPlot);
        }

        return HousingPlacementBlock.Unavailable;
    }

    private unsafe bool TryResolvePlayerHousingBlock(out byte blockId)
    {
        blockId = 0;

        IPlayerCharacter? player = objectTable.LocalPlayer;
        if (player == null)
        {
            return false;
        }

        return areaQuery.TryResolveBlock(player.Position, out blockId);
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

    private static Vector3 ResolveFloorSweepOrigin(Vector3 rayHitPoint, Vector3 normalizedDirection, float radius)
    {
        Vector3 sweepOrigin = rayHitPoint - (normalizedDirection * radius) + (Vector3.UnitY * radius);
        if (IsIndoorHousingLayout())
        {
            return sweepOrigin;
        }

        float heightFromHit = radius + sweepOrigin.Y - rayHitPoint.Y;
        float outdoorAdjustment = heightFromHit - PlacementValidationConstants.NativeOutdoorFloorSweepDistance;
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
}
