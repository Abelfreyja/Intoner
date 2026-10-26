using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using Intoner.Objects.Api;
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
    private readonly Dictionary<uint, string> _dataCenters = [];

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

    public bool TryResolveDataCenter(uint dataCenterId, [NotNullWhen(true)] out string? name)
        => _dataCenters.TryGetValue(dataCenterId, out name);

    public bool IsValidWorldSelection(ObjectWorldSelection? selection)
    {
        if (selection is null)
        {
            return true;
        }

        foreach (uint id in selection.DataCenterIds.AsSpan())
        {
            if (!_dataCenters.ContainsKey(id))
            {
                return false;
            }
        }

        foreach (ushort id in selection.WorldIds.AsSpan())
        {
            if (!_worlds.ContainsKey(id))
            {
                return false;
            }
        }

        return true;
    }

    public bool MatchesLocation(SceneCreationContext location, SceneLocationScope current)
        => location.MatchesLocation(current, TryResolveWorld(current.WorldId, out SceneWorldInfo? world) ? world.DataCenterId : 0);

    public string GetTerritoryName(uint territoryId)
        => ObjectTerritoryMetadataUtility.BuildForTerritoryId(territoryId, _gameData).TerritoryName;

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
        return SceneCreationContext.FromScope(scope, GetTerritoryName(scope.TerritoryId));
    }

    private unsafe SceneLocationScope BuildCurrentLocationScope()
    {
        if (_isTransitioning)
        {
            return default;
        }

        uint territoryId = _clientState.TerritoryType;
        uint divisionId = 0;
        uint wardId = 0;
        uint houseId = 0;
        uint roomId = 0;

        HousingManager* housingManager = HousingManager.Instance();
        if (housingManager != null)
        {
            bool inside = housingManager->IsInside();

            if (inside || housingManager->IsInWorkshop())
            {
                HouseId house = inside ? housingManager->GetCurrentIndoorHouseId() : housingManager->WorkshopTerritory->HouseId;

                if (house.Id == ulong.MaxValue)
                {
                    return default;
                }

                if (inside)
                {
                    territoryId = HousingManager.GetOriginalHouseTerritoryTypeId();
                    roomId = (uint)house.RoomNumber;
                }

                wardId = house.WardIndex + 1u;
                houseId = house.PlotIndex + 1u;
                divisionId = house.PlotIndex >= 30 ? 2u : 1u;

                if (house.IsApartment)
                {
                    houseId = 100;
                    divisionId = house.ApartmentDivision + 1u;
                }
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
            _dataCenters.TryAdd(info.DataCenterId, info.DataCenterName);
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
