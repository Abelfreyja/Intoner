using Intoner.Objects.Resources;
using Intoner.Objects.Utils;
using System.Diagnostics.CodeAnalysis;

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
        string normalizedPath = ObjectResolvedPath.FromGamePath(path).Path;
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

    public bool TryGetPath(string path, [NotNullWhen(true)] out KnownAssetPath? asset)
    {
        asset = null;

        if (!_paths.TryGetValue(ObjectResolvedPath.FromGamePath(path).Path, out KnownAssetPathState? state))
        {
            return false;
        }

        asset = state.GetSnapshot();
        return true;
    }

    private sealed class KnownAssetPathState
    {
        private readonly HashSet<string> _searchTerms = new(StringComparer.OrdinalIgnoreCase);
        private KnownAssetPath? _snapshot;
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

            if (searchTerms is not null)
            {
                changed |= SearchTermUtility.AddTerms(_searchTerms, searchTerms);
            }

            if (changed)
            {
                _includePathSegments = true;
                _snapshot = null;
            }

            return changed;
        }

        public KnownAssetPath GetSnapshot()
            => _snapshot ??= new(Path, Kind, Sources, Contracts, VfxFamily, BuildSearchTerms());

        private IReadOnlyList<string> BuildSearchTerms()
        {
            HashSet<string> searchTerms = SearchTermUtility.CreateSet(Path);
            if (_includePathSegments)
            {
                _ = SearchTermUtility.AddPathSegments(searchTerms, Path);
            }

            _ = SearchTermUtility.AddTerms(searchTerms, _searchTerms);

            return SearchTermUtility.BuildStableTerms(searchTerms);
        }
    }
}
