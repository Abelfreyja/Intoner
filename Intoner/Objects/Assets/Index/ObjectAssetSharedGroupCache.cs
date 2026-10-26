using Intoner.Objects.Resources;

namespace Intoner.Objects.Assets;

internal sealed class ObjectAssetSharedGroupCache(IObjectAssetGameData gameData)
{
    private readonly IObjectAssetGameData _gameData = gameData;

    public SharedGroupAssetInfo? GetOrAnalyzeThreadSafe(Dictionary<string, SharedGroupAssetInfo> sharedGroups, Lock stateLock, string sharedGroupPath)
    {
        string path = ObjectResolvedPath.FromGamePath(sharedGroupPath).Path;

        if (!ObjectAssetPathRules.IsCatalogSharedGroupPath(path))
        {
            return null;
        }

        lock (stateLock)
        {
            if (sharedGroups.TryGetValue(path, out SharedGroupAssetInfo? cached))
            {
                return cached;
            }
        }

        SharedGroupAssetInfo analyzed = SharedGroupAssetResolver.AnalyzeSharedGroup(_gameData, path);
        lock (stateLock)
        {
            if (sharedGroups.TryGetValue(path, out SharedGroupAssetInfo? cached))
            {
                return cached;
            }

            if (analyzed.IsComplete)
            {
                sharedGroups[path] = analyzed;
            }

            return analyzed;
        }
    }

    public SharedGroupAssetInfo? GetOrAnalyze(Dictionary<string, SharedGroupAssetInfo> sharedGroups, string sharedGroupPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string path = ObjectResolvedPath.FromGamePath(sharedGroupPath).Path;

        if (!ObjectAssetPathRules.IsCatalogSharedGroupPath(path))
        {
            return null;
        }

        if (sharedGroups.TryGetValue(path, out SharedGroupAssetInfo? cached))
        {
            return cached;
        }

        SharedGroupAssetInfo analyzed = SharedGroupAssetResolver.AnalyzeSharedGroup(_gameData, path, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        if (analyzed.IsComplete)
        {
            sharedGroups[path] = analyzed;
        }

        return analyzed;
    }
}
