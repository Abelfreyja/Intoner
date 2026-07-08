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

        bool changed = assetPathState.Merge(source, contract, vfxFamily, searchTerms);
        if (changed)
        {
            _ = ObjectSearchTermUtility.AddPathSegments(assetPathState.SearchTerms, normalizedPath);
        }

        return changed;
    }

    public void MergeFrom(PathKnowledgeBase other)
    {
        foreach (KnownAssetPath knownAssetPath in other.Enumerate())
        {
            _ = AddPath(
                knownAssetPath.Path,
                knownAssetPath.Sources,
                knownAssetPath.Contracts,
                knownAssetPath.SearchTerms,
                knownAssetPath.VfxFamily);
        }
    }

    public bool TryGetPath(string path, [NotNullWhen(true)] out KnownAssetPath? knownAssetPath)
    {
        string normalizedPath = GameAssetPathRules.NormalizeGamePath(path);
        if (_paths.TryGetValue(normalizedPath, out KnownAssetPathState? assetPathState))
        {
            knownAssetPath = assetPathState.ToKnownAssetPath();
            return true;
        }

        knownAssetPath = null;
        return false;
    }

    public IReadOnlyList<string> GetSearchTerms(string path)
        => TryGetPath(path, out KnownAssetPath? knownAssetPath)
            ? knownAssetPath.SearchTerms
            : [];

    public IEnumerable<KnownAssetPath> Enumerate()
        => _paths.Values
            .Select(static assetPathState => assetPathState.ToKnownAssetPath())
            .OrderBy(static knownAssetPath => knownAssetPath.Path, StringComparer.OrdinalIgnoreCase);

    private sealed class KnownAssetPathState
    {
        public KnownAssetPathState(string path, AssetPathKind kind)
        {
            Path = path;
            Kind = kind;
            SearchTerms = ObjectSearchTermUtility.CreateSet(path);
        }

        public string Path { get; }
        public AssetPathKind Kind { get; }
        public AssetPathSource Sources { get; private set; }
        public AssetPathContract Contracts { get; private set; }
        public KnownVfxFamily VfxFamily { get; private set; }
        public HashSet<string> SearchTerms { get; }

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
                changed |= ObjectSearchTermUtility.AddTerms(SearchTerms, searchTerms);
            }

            return changed;
        }

        public KnownAssetPath ToKnownAssetPath()
            => new(
                Path,
                Kind,
                Sources,
                Contracts,
                VfxFamily,
                ObjectSearchTermUtility.BuildStableTerms(SearchTerms));
    }
}

