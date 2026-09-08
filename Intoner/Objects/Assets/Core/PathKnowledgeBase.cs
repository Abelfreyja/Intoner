using Intoner.Objects.Utils;

namespace Intoner.Objects.Assets;

internal sealed class PathKnowledgeBase
{
    private readonly Dictionary<string, KnownAssetPathState> _paths = new(StringComparer.OrdinalIgnoreCase);

    public int Count
        => _paths.Count;

    public bool AddPath(
        string path,
        AssetPathSource source,
        AssetPathContract contract = AssetPathContract.None,
        IEnumerable<string>? searchTerms = null,
        KnownVfxFamily vfxFamily = KnownVfxFamily.None)
    {
        string normalizedPath = GameAssetPathRules.NormalizeGamePath(path);
        AssetPathKind kind = ObjectAssetPathRules.ClassifyKnownAssetPath(normalizedPath);
        if (kind == AssetPathKind.Unknown)
        {
            return false;
        }

        if (!_paths.TryGetValue(normalizedPath, out KnownAssetPathState? assetPathState))
        {
            assetPathState = new KnownAssetPathState(normalizedPath, kind);
            _paths.Add(normalizedPath, assetPathState);
        }

        return assetPathState.Merge(source, contract, vfxFamily, searchTerms);
    }

    public IReadOnlyList<string> GetSearchTerms(string path)
        => TryGetState(path, out KnownAssetPathState? state) && state is not null
            ? state.GetSearchTerms()
            : [];

    public AssetPathContract GetContracts(string path)
        => TryGetState(path, out KnownAssetPathState? state) && state is not null
            ? state.Contracts
            : AssetPathContract.None;

    public KnownVfxFamily GetVfxFamily(string path)
        => TryGetState(path, out KnownAssetPathState? state) && state is not null
            ? state.VfxFamily
            : KnownVfxFamily.None;

    public IEnumerable<KnownAssetPath> Enumerate()
        => _paths.Values
            .Select(static assetPathState => assetPathState.ToKnownAssetPath())
            .OrderBy(static knownAssetPath => knownAssetPath.Path, StringComparer.OrdinalIgnoreCase);

    private bool TryGetState(string path, out KnownAssetPathState? state)
        => _paths.TryGetValue(GameAssetPathRules.NormalizeGamePath(path), out state);

    private sealed class KnownAssetPathState
    {
        private IReadOnlyList<string>? _searchTerms;
        private HashSet<string>? _mergedSearchTerms;
        private IReadOnlyList<string>? _stableSearchTerms;
        private bool _includePathSegments;

        public KnownAssetPathState(string path, AssetPathKind kind)
        {
            Path = path;
            Kind = kind;
        }

        public string Path { get; }
        public AssetPathKind Kind { get; }
        public AssetPathSource Sources { get; private set; }
        public AssetPathContract Contracts { get; private set; }
        public KnownVfxFamily VfxFamily { get; private set; }

        public bool Merge(
            AssetPathSource source,
            AssetPathContract contract,
            KnownVfxFamily vfxFamily,
            IEnumerable<string>? searchTerms)
        {
            AssetPathSource previousSources = Sources;
            AssetPathContract previousContracts = Contracts;
            KnownVfxFamily previousFamily = VfxFamily;

            Sources |= source;
            Contracts |= contract;
            VfxFamily |= vfxFamily;

            bool changed = previousSources != Sources
                || previousContracts != Contracts
                || previousFamily != VfxFamily;
            if (searchTerms is not null && MergeSearchTerms(searchTerms))
            {
                changed = true;
                _stableSearchTerms = null;
            }

            if (changed && !_includePathSegments)
            {
                _includePathSegments = true;
                _stableSearchTerms = null;
            }

            return changed;
        }

        public IReadOnlyList<string> GetSearchTerms()
            => _stableSearchTerms ??= BuildSearchTerms();

        public KnownAssetPath ToKnownAssetPath()
            => new(
                Path,
                Kind,
                Sources,
                Contracts,
                VfxFamily,
                GetSearchTerms());

        private bool MergeSearchTerms(IEnumerable<string> searchTerms)
        {
            if (_mergedSearchTerms is not null)
            {
                return SearchTermUtility.AddTerms(_mergedSearchTerms, searchTerms);
            }

            if (_searchTerms is null)
            {
                IReadOnlyList<string> capturedTerms = searchTerms as IReadOnlyList<string>
                    ?? SearchTermUtility.BuildStableTerms(searchTerms);
                if (!capturedTerms.Any(static term => !string.IsNullOrWhiteSpace(term)))
                {
                    return false;
                }

                _searchTerms = capturedTerms;
                return true;
            }

            _mergedSearchTerms = SearchTermUtility.CreateSet(_searchTerms);
            return SearchTermUtility.AddTerms(_mergedSearchTerms, searchTerms);
        }

        private IReadOnlyList<string> BuildSearchTerms()
        {
            HashSet<string> searchTerms = SearchTermUtility.CreateSet(Path);
            if (_includePathSegments)
            {
                _ = SearchTermUtility.AddPathSegments(searchTerms, Path);
            }

            if (_mergedSearchTerms is not null)
            {
                _ = SearchTermUtility.AddTerms(searchTerms, _mergedSearchTerms);
            }
            else if (_searchTerms is not null)
            {
                _ = SearchTermUtility.AddTerms(searchTerms, _searchTerms);
            }

            return SearchTermUtility.BuildStableTerms(searchTerms);
        }
    }
}

