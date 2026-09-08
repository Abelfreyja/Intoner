using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using Intoner.Objects.Utils;
using Intoner.Utils;
using System.Diagnostics.CodeAnalysis;
using World = Lumina.Excel.Sheets.World;

namespace Intoner.Scene;

internal sealed class SceneLocationService : ISceneLocationService, IDisposable
{
    private readonly IDataManager _gameData;
    private readonly IClientState _clientState;
    private readonly ICondition _condition;
    private readonly IFramework _framework;
    private readonly IPlayerState _playerState;
    private readonly Dictionary<ushort, SceneWorldInfo> _worlds = [];

    private bool _locationInvalidationScheduled;
    private bool _isTransitioning;
    private bool _disposed;

    public SceneLocationService(
        IDataManager gameData,
        IClientState clientState,
        ICondition condition,
        IFramework framework,
        IPlayerState playerState)
    {
        _gameData = gameData;
        _clientState = clientState;
        _condition = condition;
        _framework = framework;
        _playerState = playerState;
        _isTransitioning = IsBetweenAreas();
        PublicWorlds = BuildWorldCatalog();

        _condition.ConditionChange += HandleConditionChange;
    }

    public event Action? TransitionStarted;
    public event Action? LocationInvalidated;

    public bool IsTransitioning
        => _isTransitioning;

    public IReadOnlyList<SceneWorldInfo> PublicWorlds { get; }

    public bool TryResolveWorld(ushort worldId, [NotNullWhen(true)] out SceneWorldInfo? world)
        => _worlds.TryGetValue(worldId, out world);

    public SceneCreationContext GetCurrentCreationContext()
        => FrameworkThreadUtility.Run(_framework, BuildCurrentCreationContext);

    public SceneLocationScope GetCurrentLocationScope()
        => FrameworkThreadUtility.Run(_framework, BuildCurrentLocationScope);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _condition.ConditionChange -= HandleConditionChange;
    }

    private SceneCreationContext BuildCurrentCreationContext()
    {
        SceneLocationScope scope = BuildCurrentLocationScope();
        return new SceneCreationContext
        {
            WorldId = scope.WorldId,
            WorldName = TryResolveWorld(scope.WorldId, out SceneWorldInfo? world) ? world.Name : string.Empty,
            TerritoryId = scope.TerritoryId,
            TerritoryName = ObjectTerritoryMetadataUtility.BuildForTerritoryId(scope.TerritoryId, _gameData).TerritoryName,
            DivisionId = scope.DivisionId,
            WardId = scope.WardId,
            HouseId = scope.HouseId,
            RoomId = scope.RoomId,
        };
    }

    private unsafe SceneLocationScope BuildCurrentLocationScope()
    {
        uint territoryId = _clientState.TerritoryType;
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

        return new SceneLocationScope(
            (ushort)_playerState.CurrentWorld.RowId,
            territoryId,
            divisionId,
            wardId,
            houseId,
            roomId);
    }

    private IReadOnlyList<SceneWorldInfo> BuildWorldCatalog()
    {
        List<SceneWorldInfo> publicWorlds = [];
        foreach (World world in _gameData.GetExcelSheet<World>(_gameData.Language))
        {
            if (world.RowId == 0 || world.RowId > ushort.MaxValue || !world.DataCenter.IsValid)
            {
                continue;
            }

            string name = world.Name.ToString();
            string dataCenterName = world.DataCenter.Value.Name.ToString();
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(dataCenterName))
            {
                continue;
            }

            SceneWorldInfo info = new((ushort)world.RowId, name, world.DataCenter.RowId, dataCenterName);
            _worlds.Add(info.Id, info);
            if (world.IsPublic)
            {
                publicWorlds.Add(info);
            }
        }

        return publicWorlds.OrderBy(static world => world.DataCenterName, StringComparer.Ordinal)
            .ThenBy(static world => world.Name, StringComparer.Ordinal)
            .ToArray();
    }

    private void HandleConditionChange(ConditionFlag flag, bool _)
    {
        if (flag is not (ConditionFlag.BetweenAreas or ConditionFlag.BetweenAreas51))
        {
            return;
        }

        bool isTransitioning = IsBetweenAreas();
        if (_isTransitioning == isTransitioning)
        {
            return;
        }

        _isTransitioning = isTransitioning;
        if (isTransitioning)
        {
            TransitionStarted?.Invoke();
            return;
        }

        ScheduleLocationInvalidation();
    }

    private void ScheduleLocationInvalidation()
    {
        if (_disposed
            || _locationInvalidationScheduled
            || _isTransitioning)
        {
            return;
        }

        _locationInvalidationScheduled = true;
        _ = _framework.RunOnTick(PublishLocationInvalidation, delayTicks: 1);
    }

    private void PublishLocationInvalidation()
    {
        _locationInvalidationScheduled = false;
        if (_disposed || _isTransitioning)
        {
            return;
        }

        LocationInvalidated?.Invoke();
    }

    private bool IsBetweenAreas()
        => _condition[ConditionFlag.BetweenAreas] || _condition[ConditionFlag.BetweenAreas51];
}
