using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Intoner.Objects.Filesystem.Configuration;
using Intoner.Objects.Models;
using Intoner.Objects.Utils;
using Lumina.Excel.Sheets;

namespace Intoner.Objects.Runtime;

/// <summary>
/// Resolves the current Intoner objects runtime location and optional housing context on the framework thread.
/// </summary>
internal interface IObjectRuntimeLocationService
{
    /// <summary>
    /// Gets the full current location context for Intoner objcts runtime consumers.
    /// </summary>
    /// <returns>The current runtime location context.</returns>
    ObjectRuntimeLocationContext GetCurrentContext();

    /// <summary>
    /// Gets the current creation context for new local objects.
    /// </summary>
    /// <returns>The current object creation context.</returns>
    ObjectCreationContext GetCurrentCreationContext();

    /// <summary>
    /// Gets the current location scope used for object activation.
    /// </summary>
    /// <returns>The current location scope.</returns>
    ObjectLocationScope GetCurrentLocationScope();

    /// <summary>
    /// Resolves the current housing placement context for the selected housing policy target.
    /// </summary>
    /// <param name="targetState">The selected housing mode state.</param>
    /// <returns>The current housing placement context.</returns>
    HousingPlacementContext ResolveHousingPlacementContext(ObjectHousingModeState targetState);
}

internal sealed class ObjectRuntimeLocationService(
    IDataManager gameData,
    IClientState clientState,
    IFramework framework,
    IPlayerState playerState,
    ObjectHousingRuntimeContextResolver housingContextResolver) : IObjectRuntimeLocationService
{
    public ObjectRuntimeLocationContext GetCurrentContext()
        => ObjectFrameworkUtility.RunOnFrameworkThread(framework, BuildCurrentContext);

    public ObjectCreationContext GetCurrentCreationContext()
        => ObjectFrameworkUtility.RunOnFrameworkThread(framework, BuildCurrentCreationContext);

    public ObjectLocationScope GetCurrentLocationScope()
        => ObjectFrameworkUtility.RunOnFrameworkThread(framework, BuildCurrentLocationScope);

    public HousingPlacementContext ResolveHousingPlacementContext(ObjectHousingModeState targetState)
        => ObjectFrameworkUtility.RunOnFrameworkThread(
            framework,
            () => housingContextResolver.Resolve(BuildCurrentLocationScope().TerritoryId).ToPlacementContext(targetState));

    private ObjectRuntimeLocationContext BuildCurrentContext()
    {
        ObjectLocationScope scope = BuildCurrentLocationScope();
        ObjectTerritoryMetadata territory = BuildTerritoryMetadata(scope.TerritoryId);
        return new ObjectRuntimeLocationContext(
            scope,
            BuildCreationContext(scope, territory),
            territory,
            housingContextResolver.Resolve(scope.TerritoryId));
    }

    private ObjectCreationContext BuildCurrentCreationContext()
    {
        ObjectLocationScope scope = BuildCurrentLocationScope();
        return BuildCreationContext(scope, BuildTerritoryMetadata(scope.TerritoryId));
    }

    private unsafe ObjectLocationScope BuildCurrentLocationScope()
    {
        uint territoryId = clientState.TerritoryType;
        uint divisionId = 0;
        uint wardId = 0;
        uint houseId = 0;
        uint roomId = 0;

        HousingManager* housingManager = HousingManager.Instance();
        if (housingManager != null)
        {
            if (housingManager->IsInside())
            {
                territoryId = HousingManager.GetOriginalHouseTerritoryTypeId();
                HouseId house = housingManager->GetCurrentIndoorHouseId();
                wardId = house.WardIndex + 1u;
                houseId = house.IsApartment ? 100u : house.PlotIndex + 1u;
                roomId = (uint)house.RoomNumber;
                divisionId = house.IsApartment ? house.ApartmentDivision + 1u : housingManager->GetCurrentDivision();
            }
            else if (housingManager->IsInWorkshop())
            {
                HouseId house = housingManager->WorkshopTerritory->HouseId;
                wardId = house.WardIndex + 1u;
                houseId = house.PlotIndex + 1u;
                divisionId = housingManager->GetCurrentDivision();
            }
            else if (housingManager->IsOutside())
            {
                HouseId house = housingManager->OutdoorTerritory->HouseId;
                wardId = house.WardIndex + 1u;
                divisionId = housingManager->GetCurrentDivision();
            }
        }

        return new ObjectLocationScope(
            (ushort)playerState.CurrentWorld.RowId,
            territoryId,
            divisionId,
            wardId,
            houseId,
            roomId);
    }

    private ObjectCreationContext BuildCreationContext(
        ObjectLocationScope scope,
        ObjectTerritoryMetadata territory)
        => new()
        {
            WorldId = scope.WorldId,
            WorldName = ResolveWorldName(scope.WorldId),
            TerritoryId = scope.TerritoryId,
            TerritoryName = territory.TerritoryName,
            DivisionId = scope.DivisionId,
            WardId = scope.WardId,
            HouseId = scope.HouseId,
            RoomId = scope.RoomId,
        };

    private string ResolveWorldName(ushort worldId)
    {
        if (worldId == 0)
        {
            return string.Empty;
        }

        return gameData.GetExcelSheet<World>(gameData.Language)?.TryGetRow(worldId, out World world) == true
            ? world.Name.ToString()
            : string.Empty;
    }

    private ObjectTerritoryMetadata BuildTerritoryMetadata(uint territoryId)
        => ObjectTerritoryMetadataUtility.BuildForTerritoryId(territoryId, gameData);
}
