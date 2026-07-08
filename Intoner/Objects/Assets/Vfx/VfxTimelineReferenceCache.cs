namespace Intoner.Objects.Assets;

internal sealed class VfxTimelineReferenceCache(IObjectAssetGameData gameData)
{
    private readonly IObjectAssetGameData _gameData = gameData;
    private readonly Dictionary<string, IReadOnlyList<TmbVfxReference>> _references = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<TmbVfxReference> Get(string timelinePath)
    {
        string normalizedTimelinePath = GameAssetPathRules.NormalizeGamePath(timelinePath);
        if (_references.TryGetValue(normalizedTimelinePath, out IReadOnlyList<TmbVfxReference>? references))
        {
            return references;
        }

        references = VfxAssetAnalyzer.CollectTmbVfxReferences(_gameData, normalizedTimelinePath);
        _references.Add(normalizedTimelinePath, references);
        return references;
    }
}

