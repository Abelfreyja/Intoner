using Intoner.Objects.Utils;

namespace Intoner.Objects.Assets;

internal sealed class AssetReferenceSources
{
    private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _sources;

    private AssetReferenceSources(IReadOnlyDictionary<string, IReadOnlyList<string>> sources)
        => _sources = sources;

    public static AssetReferenceSources Empty { get; } = new(new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase));

    public IReadOnlyList<string> this[string assetPath]
        => _sources.TryGetValue(assetPath, out IReadOnlyList<string>? paths) ? paths : [];

    internal sealed class Builder
    {
        private readonly Dictionary<string, HashSet<string>> _sources = new(StringComparer.OrdinalIgnoreCase);

        public void Add(string assetPath, string sourcePath)
            => _ = SearchTermUtility.AddTerm(GetSources(assetPath), sourcePath);

        public void Add(string assetPath, IEnumerable<string> sourcePaths)
            => _ = SearchTermUtility.AddTerms(GetSources(assetPath), sourcePaths);

        public AssetReferenceSources Build()
        {
            if (_sources.Count == 0)
            {
                return Empty;
            }

            Dictionary<string, IReadOnlyList<string>> snapshot = new(_sources.Count, StringComparer.OrdinalIgnoreCase);

            foreach ((string path, HashSet<string> sourcePaths) in _sources)
            {
                snapshot.Add(path, SearchTermUtility.BuildStableTerms(sourcePaths));
            }

            return new AssetReferenceSources(snapshot);
        }

        private HashSet<string> GetSources(string assetPath)
        {
            if (!_sources.TryGetValue(assetPath, out HashSet<string>? sourcePaths))
            {
                sourcePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                _sources.Add(assetPath, sourcePaths);
            }

            return sourcePaths;
        }
    }
}
