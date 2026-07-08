using System.Diagnostics.CodeAnalysis;

namespace Intoner.Objects.Assets;

internal sealed class ObjectAssetSharedGroupCache(IObjectAssetGameData gameData)
{
    private readonly IObjectAssetGameData _gameData = gameData;

    public bool TryGetOrAnalyzeThreadSafe(
        CatalogAssetState state,
        Lock stateLock,
        string sharedGroupPath,
        [NotNullWhen(true)] out SharedGroupAssetInfo? sharedGroupAssets)
    {
        if (!TryNormalizeExistingPath(sharedGroupPath, out string? normalizedPath))
        {
            sharedGroupAssets = null;
            return false;
        }

        lock (stateLock)
        {
            if (state.SharedGroups.TryGetValue(normalizedPath, out sharedGroupAssets))
            {
                return true;
            }
        }

        SharedGroupAssetInfo analyzedAssets = Analyze(normalizedPath);
        lock (stateLock)
        {
            if (!state.SharedGroups.TryGetValue(normalizedPath, out SharedGroupAssetInfo? cachedAssets))
            {
                state.SharedGroups[normalizedPath] = analyzedAssets;
                sharedGroupAssets = analyzedAssets;
            }
            else
            {
                sharedGroupAssets = cachedAssets;
            }
        }

        return true;
    }

    public bool TryGetOrAnalyzeFromState(
        CatalogAssetState state,
        string sharedGroupPath,
        [NotNullWhen(true)] out SharedGroupAssetInfo? sharedGroupAssets)
    {
        if (!TryNormalizeExistingPath(sharedGroupPath, out string? normalizedPath))
        {
            sharedGroupAssets = null;
            return false;
        }

        if (!state.SharedGroups.TryGetValue(normalizedPath, out sharedGroupAssets))
        {
            sharedGroupAssets = Analyze(normalizedPath);
            state.SharedGroups[normalizedPath] = sharedGroupAssets;
        }

        return true;
    }

    private bool TryNormalizeExistingPath(string sharedGroupPath, [NotNullWhen(true)] out string? normalizedPath)
    {
        normalizedPath = GameAssetPathRules.NormalizeGamePath(sharedGroupPath);
        return ObjectAssetPathRules.IsCatalogSharedGroupPath(normalizedPath)
            && _gameData.FileExists(normalizedPath);
    }

    private SharedGroupAssetInfo Analyze(string normalizedPath)
        => SharedGroupAssetResolver.AnalyzeSharedGroup(_gameData, normalizedPath);
}
