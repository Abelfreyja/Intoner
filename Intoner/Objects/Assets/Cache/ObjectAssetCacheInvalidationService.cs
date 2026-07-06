using Intoner.Objects.Assets;

namespace Intoner.Objects.Assets.Cache;

/// <summary> checks whether a saved object asset cache snapshot can be reused </summary>
internal interface IObjectAssetCacheInvalidationService
{
    /// <summary> gets the current live game version used for object asset cache invalidation </summary>
    string CurrentGameVersion { get; }

    /// <summary> gets the current local sqpack index fingerprint used for object asset cache invalidation </summary>
    string CurrentSqpackIndexFingerprint { get; }

    /// <summary> gets reusable cache sections for the current game build </summary>
    /// <param name="manifest">the loaded cache manifest when one is available</param>
    /// <param name="requestedSections">the cache sections the caller wants to reuse</param>
    /// <returns>the reusable cache sections for the current game build</returns>
    ObjectAssetCacheSectionSet GetReusableSections(
        ObjectAssetCacheManifest? manifest,
        ObjectAssetCacheSectionSet requestedSections);
}

internal sealed class ObjectAssetCacheInvalidationService : IObjectAssetCacheInvalidationService
{
    private readonly IObjectAssetGameVersionService _gameVersionService;
    private readonly ISqpackIndexFingerprintService _sqpackIndexFingerprintService;

    public ObjectAssetCacheInvalidationService(
        IObjectAssetGameVersionService gameVersionService,
        ISqpackIndexFingerprintService sqpackIndexFingerprintService)
    {
        _gameVersionService = gameVersionService;
        _sqpackIndexFingerprintService = sqpackIndexFingerprintService;
    }

    public string CurrentGameVersion
        => _gameVersionService.GetCurrentGameVersion();

    public string CurrentSqpackIndexFingerprint
        => _sqpackIndexFingerprintService.GetCurrentSqpackIndexFingerprint();

    public ObjectAssetCacheSectionSet GetReusableSections(
        ObjectAssetCacheManifest? manifest,
        ObjectAssetCacheSectionSet requestedSections)
    {
        string currentGameVersion = CurrentGameVersion;
        string currentSqpackIndexFingerprint = CurrentSqpackIndexFingerprint;
        if (manifest is null
         || !manifest.MatchesBuildIdentity(currentGameVersion, currentSqpackIndexFingerprint))
        {
            return ObjectAssetCacheSectionSet.None;
        }

        return manifest.GetReusableSections(requestedSections);
    }
}
