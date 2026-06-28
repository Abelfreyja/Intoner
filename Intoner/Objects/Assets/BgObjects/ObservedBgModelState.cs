using Intoner.Objects.Assets.Cache;
using Intoner.Objects.Utils;

namespace Intoner.Objects.Assets;

internal sealed class ObservedBgModelState(string path)
{
    private static readonly string[] PreferredPrimarySources =
    [
        ObjectAssetIndex.ObservedSharedGroupSource,
        ObjectAssetIndex.SqpackSharedGroupSource,
        ObjectAssetIndex.SqpackCollisionSource,
    ];

    private IReadOnlyList<string>? _searchTerms;
    private readonly ObjectTerritoryMetadataSet _territoryMetadata = new();

    public string Path { get; } = path;
    public HashSet<string> Sources { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool IsRuntimeObserved
        => Sources.Contains(ObjectAssetIndex.ObservedResourceSource)
        || Sources.Contains(ObjectAssetIndex.ObservedSharedGroupSource);

    public bool AddSource(string source)
    {
        if (!Sources.Add(source))
        {
            return false;
        }

        _searchTerms = null;
        return true;
    }

    public bool AddTerritoryMetadata(in ObjectTerritoryMetadata territoryMetadata)
    {
        bool changed = _territoryMetadata.Add(territoryMetadata);
        if (changed)
        {
            _searchTerms = null;
        }

        return changed;
    }

    public bool AddTerritoryMetadata(IReadOnlyList<uint> territoryIds, IReadOnlyList<string> territoryNames)
    {
        bool changed = _territoryMetadata.Add(territoryIds, territoryNames);
        if (changed)
        {
            _searchTerms = null;
        }

        return changed;
    }

    public static ObservedBgModelState FromCache(ObjectAssetCacheBgModel bgModel)
    {
        ObservedBgModelState asset = new(bgModel.Path);
        foreach (string source in bgModel.Sources)
        {
            if (!string.IsNullOrWhiteSpace(source))
            {
                _ = asset.AddSource(source);
            }
        }

        _ = asset.AddTerritoryMetadata(bgModel.TerritoryIds, bgModel.TerritoryNames);
        if (asset.Sources.Count == 0)
        {
            _ = asset.AddSource(ObjectAssetIndex.ObservedResourceSource);
        }

        return asset;
    }

    public ObjectAssetCacheBgModel ToCacheModel()
        => new(
            Path,
            Sources.OrderBy(static source => source, StringComparer.OrdinalIgnoreCase).ToArray(),
            BuildStableTerritoryIds(),
            BuildStableTerritoryNames());

    public CacheSaveCapture CaptureForSave()
        => new(
            ToCacheModel(),
            IsRuntimeObserved);

    public ObservedBgAsset ToAsset(PathKnowledgeBase knowledgeBase)
        => new(
            Path,
            GetPrimarySource(),
            BuildStableTerritoryIds(),
            BuildStableTerritoryNames(),
            ObjectSearchTermUtility.MergeTerms(BuildSearchTerms(), knowledgeBase.GetSearchTerms(Path)));

    private string GetPrimarySource()
    {
        foreach (string source in PreferredPrimarySources)
        {
            if (Sources.Contains(source))
            {
                return source;
            }
        }

        return ObjectAssetIndex.ObservedResourceSource;
    }

    private IReadOnlyList<string> BuildSearchTerms()
    {
        if (_searchTerms is not null)
        {
            return _searchTerms;
        }

        HashSet<string> searchTerms = ObjectSearchTermUtility.CreateSet(Path);
        _ = ObjectSearchTermUtility.AddTerms(searchTerms, Sources);
        _ = ObjectSearchTermUtility.AddPathSegments(searchTerms, Path);
        _territoryMetadata.AddSearchTerms(searchTerms);

        _searchTerms = ObjectSearchTermUtility.BuildStableTerms(searchTerms);
        return _searchTerms;
    }

    private IReadOnlyList<uint> BuildStableTerritoryIds()
        => _territoryMetadata.BuildStableIds();

    private IReadOnlyList<string> BuildStableTerritoryNames()
        => _territoryMetadata.BuildStableNames();

    public readonly record struct CacheSaveCapture(
        ObjectAssetCacheBgModel CacheModel,
        bool IsRuntimeObserved);
}

