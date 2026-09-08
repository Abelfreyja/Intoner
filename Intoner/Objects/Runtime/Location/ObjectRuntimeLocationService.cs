using Dalamud.Plugin.Services;
using Intoner.Objects.Utils;
using Intoner.Scene;
using Intoner.Utils;

namespace Intoner.Objects.Runtime;

/// <summary>
/// Resolves the current Intoner objects runtime location and optional housing context on the framework thread.
/// </summary>
internal interface IObjectRuntimeLocationService
{
    /// <summary>
    /// Gets the full current location context for Intoner object runtime consumers.
    /// </summary>
    /// <returns>The current runtime location context.</returns>
    ObjectRuntimeLocationContext GetCurrentContext();

    /// <summary>
    /// Resolves the current housing placement context for the selected housing policy target.
    /// </summary>
    /// <param name="targetState">The selected housing mode state.</param>
    /// <returns>The current housing placement context.</returns>
    HousingPlacementContext ResolveHousingPlacementContext(ObjectHousingModeState targetState);
}

internal sealed class ObjectRuntimeLocationService : IObjectRuntimeLocationService
{
    private readonly IDataManager _gameData;
    private readonly IFramework _framework;
    private readonly ISceneLocationService _sceneLocationService;
    private readonly ObjectHousingRuntimeContextResolver _housingContextResolver;

    public ObjectRuntimeLocationService(
        IDataManager gameData,
        IFramework framework,
        ISceneLocationService sceneLocationService,
        ObjectHousingRuntimeContextResolver housingContextResolver)
    {
        _gameData = gameData;
        _framework = framework;
        _sceneLocationService = sceneLocationService;
        _housingContextResolver = housingContextResolver;
    }

    public ObjectRuntimeLocationContext GetCurrentContext()
        => FrameworkThreadUtility.Run(_framework, BuildCurrentContext);

    public HousingPlacementContext ResolveHousingPlacementContext(ObjectHousingModeState targetState)
        => FrameworkThreadUtility.Run(
            _framework,
            () => _housingContextResolver.Resolve(_sceneLocationService.GetCurrentLocationScope().TerritoryId).ToPlacementContext(targetState));

    private ObjectRuntimeLocationContext BuildCurrentContext()
    {
        SceneCreationContext creationContext = _sceneLocationService.GetCurrentCreationContext();
        SceneLocationScope scope = creationContext.Scope;
        ObjectTerritoryMetadata territory = BuildTerritoryMetadata(scope.TerritoryId);
        return new ObjectRuntimeLocationContext(
            scope,
            creationContext,
            territory,
            _housingContextResolver.Resolve(scope.TerritoryId));
    }

    private ObjectTerritoryMetadata BuildTerritoryMetadata(uint territoryId)
        => ObjectTerritoryMetadataUtility.BuildForTerritoryId(territoryId, _gameData);
}
