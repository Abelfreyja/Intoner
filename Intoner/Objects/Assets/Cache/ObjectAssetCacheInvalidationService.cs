namespace Intoner.Objects.Assets.Cache;

/// <summary> gets the current build identity for object asset cache loading </summary>
internal interface IObjectAssetCacheInvalidationService
{
    /// <summary> gets the current build identity and requested cache sections once </summary>
    /// <param name="sections">the cache sections the caller wants to reuse</param>
    /// <returns>the load request with the set build identity</returns>
    ObjectAssetCacheLoadRequest CreateLoadRequest(ObjectAssetCacheSectionSet sections);
}

internal sealed class ObjectAssetCacheInvalidationService : IObjectAssetCacheInvalidationService
{
    private readonly IObjectAssetGameVersionService _gameVersionService;
    private readonly ISqpackIndexFingerprintService _sqpackIndexFingerprintService;
    private readonly IObjectAssetGameData _gameData;

    public ObjectAssetCacheInvalidationService(
        IObjectAssetGameVersionService gameVersionService,
        ISqpackIndexFingerprintService sqpackIndexFingerprintService,
        IObjectAssetGameData gameData)
    {
        _gameVersionService = gameVersionService;
        _sqpackIndexFingerprintService = sqpackIndexFingerprintService;
        _gameData = gameData;
    }

    public ObjectAssetCacheLoadRequest CreateLoadRequest(ObjectAssetCacheSectionSet sections)
        => new(
            _gameVersionService.GetCurrentGameVersion(),
            _sqpackIndexFingerprintService.GetCurrentSqpackIndexFingerprint(),
            _gameData.Language,
            sections);
}
