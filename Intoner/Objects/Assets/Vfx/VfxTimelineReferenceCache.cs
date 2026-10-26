using Intoner.Objects.Resources;

namespace Intoner.Objects.Assets;

internal sealed class VfxTimelineReferenceCache
{
    private readonly Dictionary<string, TmbVfxReferenceCollection> _references = new(StringComparer.OrdinalIgnoreCase);

    public TmbVfxReferenceCollection Get(IObjectAssetGameData gameData, string timelinePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string normalizedTimelinePath = ObjectResolvedPath.FromGamePath(timelinePath).Path;
        if (_references.TryGetValue(normalizedTimelinePath, out TmbVfxReferenceCollection references))
        {
            return references;
        }

        if (!GameAssetPathRules.IsFileKind(normalizedTimelinePath, GameAssetFileKind.Tmb))
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new([], false);
        }

        references = TmbVfxReferenceReader.CollectReferences(gameData, normalizedTimelinePath, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        if (references.IsComplete)
        {
            _references.Add(normalizedTimelinePath, references);
        }

        return references;
    }
}
