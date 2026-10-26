using Intoner.Objects.Assets;
using Intoner.Objects.Models;
using Intoner.Objects.Utils;
using Intoner.Services.Loading;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Intoner.Objects.Catalog;

internal enum ObjectCatalogKind
{
    BgObject = 0,
    Furniture = 1,
    Vfx = 2,
}

internal readonly record struct ObjectCatalogFilterCount(string Label, int Count);

internal readonly record struct ObjectCatalogFurnitureResult(
    ObjectCatalogEntry Entry,
    ObjectCatalogFurnitureVariant Variant);

internal sealed record ObjectCatalogFurnitureVariant(
    uint HousingRowId,
    uint ItemRowId,
    string Name,
    uint IconId,
    byte DyeCount,
    HousingFurnitureMetadata HousingMetadata,
    string Category,
    bool DestroyOnRemoval)
{
    private string? _searchText;
    private string? _normalizedSearchText;

    public string SearchText
        => _searchText ??= BuildSearchText();

    public bool MatchesToken(string normalizedToken)
        => NormalizedSearchText.Contains(normalizedToken, StringComparison.Ordinal);

    private string NormalizedSearchText
        => _normalizedSearchText ??= SearchTermUtility.NormalizeSearchText(SearchText);

    private string BuildSearchText()
        => SearchTermUtility.BuildSearchText(
        [
            HousingRowId.ToString(CultureInfo.InvariantCulture),
            ItemRowId.ToString(CultureInfo.InvariantCulture),
            Name,
            IconId.ToString(CultureInfo.InvariantCulture),
            DyeCount.ToString(CultureInfo.InvariantCulture),
            HousingMetadata.SearchText,
            Category,
            DestroyOnRemoval ? "destroy on removal" : "persistent",
        ]);
}

internal sealed class ObjectCatalogFurnitureInfo
{
    private readonly ObjectCatalogFurnitureVariant[] _variants;
    private string? _searchText;

    public ObjectCatalogFurnitureInfo(
        ushort modelKey,
        ObjectCatalogFurnitureVariant primaryVariant,
        IEnumerable<ObjectCatalogFurnitureVariant>? variants = null)
    {
        ModelKey       = modelKey;
        PrimaryVariant = primaryVariant;

        if (variants is null)
        {
            _variants = [primaryVariant];
            return;
        }

        _variants = variants
            .Prepend(primaryVariant)
            .DistinctBy(static variant => variant.HousingRowId)
            .ToArray();
    }

    public ushort ModelKey { get; }

    public ObjectCatalogFurnitureVariant PrimaryVariant { get; }

    public IReadOnlyList<ObjectCatalogFurnitureVariant> Variants
        => _variants;

    public string SearchText
        => _searchText ??= BuildSearchText();

    public IEnumerable<string> EnumerateCategories()
    {
        foreach (ObjectCatalogFurnitureVariant variant in _variants)
        {
            if (!string.IsNullOrWhiteSpace(variant.Category))
            {
                yield return variant.Category;
            }
        }
    }

    public bool TryResolveVariantByItemRowId(
        uint itemRowId,
        [NotNullWhen(true)] out ObjectCatalogFurnitureVariant? variant)
    {
        if (itemRowId == 0)
        {
            variant = null;
            return false;
        }

        foreach (ObjectCatalogFurnitureVariant candidate in _variants)
        {
            if (candidate.ItemRowId == itemRowId)
            {
                variant = candidate;
                return true;
            }
        }

        variant = null;
        return false;
    }

    public bool TryResolveVariantByHousingRowId(
        uint housingRowId,
        [NotNullWhen(true)] out ObjectCatalogFurnitureVariant? variant)
    {
        if (housingRowId == 0)
        {
            variant = null;
            return false;
        }

        foreach (ObjectCatalogFurnitureVariant candidate in _variants)
        {
            if (candidate.HousingRowId == housingRowId)
            {
                variant = candidate;
                return true;
            }
        }

        variant = null;
        return false;
    }

    public bool TryResolveVariant(
        uint housingRowId,
        uint itemRowId,
        [NotNullWhen(true)] out ObjectCatalogFurnitureVariant? variant)
    {
        if (housingRowId != 0)
        {
            if (!TryResolveVariantByHousingRowId(housingRowId, out variant))
            {
                return false;
            }

            if (itemRowId == 0 || variant.ItemRowId == itemRowId)
            {
                return true;
            }

            variant = null;
            return false;
        }

        return TryResolveVariantByItemRowId(itemRowId, out variant);
    }

    private string BuildSearchText()
        => SearchTermUtility.BuildSearchText(BuildSearchTerms());

    private IEnumerable<string> BuildSearchTerms()
    {
        yield return ModelKey.ToString(CultureInfo.InvariantCulture);
        foreach (ObjectCatalogFurnitureVariant variant in _variants)
        {
            yield return variant.SearchText;
        }
    }
}

internal enum HousingFurnitureArea
{
    Indoor,
    Outdoor,
}

internal enum HousingPlacementSurface
{
    Floor,
    Tabletop,
    Wall,
}

[StructLayout(LayoutKind.Auto)]
internal readonly record struct HousingPileFootprint(ushort Width, ushort Depth, ushort Height, byte AllowedOverlapTierMask)
{
    public bool HasArea
        => Width != 0 && Depth != 0;

    public bool AllowsOverlapWith(byte aquariumTier)
        => aquariumTier is >= 1 and <= 5
        && (AllowedOverlapTierMask & (1 << (aquariumTier - 1))) != 0;
}

internal sealed record HousingFurnitureMetadata(
    HousingFurnitureArea Area,
    byte HousingItemCategory,
    byte UsageType,
    byte PlaceLimitType,
    byte AquariumTier,
    uint PlacementRowId,
    HousingPileFootprint? PileFootprint)
{
    private const byte TabletopCategory = 0x0e;
    private const byte WallMountedCategory = 0x0f;

    private string? _searchText;

    public bool IsIndoor
        => Area == HousingFurnitureArea.Indoor;

    public bool IsOutdoor
        => Area == HousingFurnitureArea.Outdoor;

    public bool IsTabletop
        => HousingItemCategory == TabletopCategory;

    public bool IsWallMounted
        => HousingItemCategory == WallMountedCategory;

    public bool RequiresAquariumFootprintValidation
        => AquariumTier != 0;

    public bool HasAquariumFootprint
        => PileFootprint is { HasArea: true };

    public HousingPlacementSurface Surface
        => IsWallMounted
            ? HousingPlacementSurface.Wall
            : IsTabletop
                ? HousingPlacementSurface.Tabletop
                : HousingPlacementSurface.Floor;

    public string SearchText
        => _searchText ??= SearchTermUtility.BuildSearchText(BuildSearchTerms());

    private IEnumerable<string> BuildSearchTerms()
    {
        yield return Area.ToString();
        yield return Surface.ToString();

        if (RequiresAquariumFootprintValidation)
        {
            yield return "aquarium";
            yield return "aquarium tier " + AquariumTier.ToString(CultureInfo.InvariantCulture);
            yield return HasAquariumFootprint
                ? "aquarium showcase footprint"
                : "aquarium footprint";
        }
    }
}

internal sealed record ObjectCatalogBgObjectInfo(
    IReadOnlyList<uint> TerritoryIds,
    IReadOnlyList<string> TerritoryNames)
{
    public IReadOnlyList<BgColliderDefinition> ColliderDefinitions { get; init; } = [];
    public bool ColliderDefinitionsComplete { get; init; }
    public BgColliderDefinition? DefaultColliderDefinition
        => ColliderDefinitionsComplete && ColliderDefinitions.Count == 1 ? ColliderDefinitions[0] : null;

    private string? _searchText;

    public string SearchText
        => _searchText ??= BuildSearchText();

    private string BuildSearchText()
        => SearchTermUtility.BuildSearchText(BuildSearchTerms());

    private IEnumerable<string> BuildSearchTerms()
    {
        foreach (uint territoryId in TerritoryIds)
        {
            yield return territoryId.ToString(CultureInfo.InvariantCulture);
        }

        foreach (string territoryName in TerritoryNames)
        {
            yield return territoryName;
        }
    }
}

internal sealed record ObjectCatalogVfxInfo(VfxLoopFacts LoopFacts)
{
    public bool CanUseReplayLoop
        => !LoopFacts.IsPermanent;

    public bool IsPermanentLoop
        => LoopFacts.IsPermanent;
}

internal enum ObjectCatalogSearchProfile
{
    Default,
    Vfx,
}

internal sealed class ObjectCatalogEntry
{
    private readonly IReadOnlyList<string>? _additionalSearchTerms;
    private readonly ObjectCatalogSearchProfile _searchProfile;
    private readonly IReadOnlyList<PreviewModelInfo>? _configuredPreviewModels;
    private IReadOnlyList<PreviewModelInfo>? _resolvedPreviewModels;
    private IReadOnlyList<string>? _resolvedPreviewModelPaths;
    private string? _normalizedSearchText;
    private string? _normalizedFurnitureSharedSearchText;

    public ObjectCatalogEntry(
        ObjectCatalogKind kind,
        string source,
        uint rowId,
        string name,
        string placementPath,
        string assetPath,
        ObjectCatalogFurnitureInfo? furnitureInfo = null,
        ObjectCatalogBgObjectInfo? bgObjectInfo = null,
        ObjectCatalogVfxInfo? vfxInfo = null,
        IReadOnlyList<PreviewModelInfo>? previewModels = null,
        IReadOnlyList<string>? additionalSearchTerms = null,
        ObjectCatalogSearchProfile searchProfile = ObjectCatalogSearchProfile.Default,
        bool previewModelDiscoveryComplete = true)
    {
        Kind              = kind;
        Source            = source;
        RowId             = rowId;
        Name              = name;
        PlacementPath     = placementPath;
        AssetPath         = assetPath;
        FurnitureInfo     = furnitureInfo;
        BgObjectInfo      = bgObjectInfo;
        VfxInfo           = vfxInfo;

        _configuredPreviewModels = previewModels;
        _additionalSearchTerms = additionalSearchTerms;
        _searchProfile = searchProfile;
        PreviewModelDiscoveryComplete = previewModelDiscoveryComplete;
    }

    public ObjectCatalogKind Kind { get; }
    public string Source { get; }
    public uint RowId { get; }
    public string Name { get; }
    public string PlacementPath { get; }
    public string AssetPath { get; }
    public ObjectCatalogFurnitureInfo? FurnitureInfo { get; }
    public ObjectCatalogBgObjectInfo? BgObjectInfo { get; }
    public ObjectCatalogVfxInfo? VfxInfo { get; }
    public bool PreviewModelDiscoveryComplete { get; }

    internal ObjectCatalogEntry WithCompletedFurniturePreview(SharedGroupAssetInfo assets)
        => new(
            Kind,
            Source,
            RowId,
            Name,
            PlacementPath,
            AssetPath,
            FurnitureInfo,
            BgObjectInfo,
            VfxInfo,
            previewModels: assets.PreviewModels,
            additionalSearchTerms: SearchTermUtility.MergeTerms(_additionalSearchTerms ?? [], BuildFurnitureSearchTerms(assets)),
            searchProfile: _searchProfile);

    internal static IReadOnlyList<string> BuildFurnitureSearchTerms(SharedGroupAssetInfo assets)
        => SearchTermUtility.BuildStableTerms(
            assets.BgObjectModelPaths,
            assets.NestedSharedGroupPaths,
            assets.ReferencedVfxPaths);

    public IReadOnlyList<PreviewModelInfo> PreviewModels
        => _resolvedPreviewModels ??= ResolvePreviewModels();

    public IReadOnlyList<string> PreviewModelPaths
        => _resolvedPreviewModelPaths ??= PreviewModels
            .Select(static model => model.ModelPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public string DisplayPath
        => string.IsNullOrWhiteSpace(AssetPath)
            ? PlacementPath
            : AssetPath;

    internal void PrepareSearch()
    {
        if (Volatile.Read(ref _normalizedSearchText) is not null)
        {
            return;
        }

        IReadOnlyList<string> searchPreviewModelPaths = Kind == ObjectCatalogKind.BgObject
            && _configuredPreviewModels is null
                ? []
                : PreviewModelPaths;
        string normalizedBaseSearchText = BuildNormalizedSearchText(searchPreviewModelPaths);
        string normalizedFurnitureSharedSearchText = FurnitureInfo is not null
            ? BuildNormalizedFurnitureSharedSearchText(searchPreviewModelPaths)
            : normalizedBaseSearchText;
        string normalizedSearchText = FurnitureInfo is not null
            ? string.Concat(normalizedBaseSearchText, "\n", SearchTermUtility.NormalizeSearchText(FurnitureInfo.SearchText))
            : normalizedBaseSearchText;
        Volatile.Write(ref _normalizedFurnitureSharedSearchText, normalizedFurnitureSharedSearchText);
        Volatile.Write(ref _normalizedSearchText, normalizedSearchText);
    }

    public bool Matches(IReadOnlyList<string> searchTokens)
    {
        PrepareSearch();
        return SearchTermUtility.MatchesNormalizedSearchText(_normalizedSearchText!, searchTokens);
    }

    public bool TryResolveFurnitureFilterVariant(
        IReadOnlyList<string> searchTokens,
        string category,
        [NotNullWhen(true)] out ObjectCatalogFurnitureVariant? variant)
    {
        if (searchTokens.Count > 0)
        {
            PrepareSearch();
        }

        if (FurnitureInfo is not { } furnitureInfo)
        {
            variant = null;
            return false;
        }

        if (MatchesFurnitureVariant(furnitureInfo.PrimaryVariant, searchTokens, category))
        {
            variant = furnitureInfo.PrimaryVariant;
            return true;
        }

        foreach (ObjectCatalogFurnitureVariant candidate in furnitureInfo.Variants)
        {
            if (ReferenceEquals(candidate, furnitureInfo.PrimaryVariant)
             || !MatchesFurnitureVariant(candidate, searchTokens, category))
            {
                continue;
            }

            variant = candidate;
            return true;
        }

        variant = null;
        return false;
    }

    private bool MatchesFurnitureVariant(
        ObjectCatalogFurnitureVariant variant,
        IReadOnlyList<string> searchTokens,
        string category)
    {
        if (category.Length > 0 && !string.Equals(variant.Category, category, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        foreach (string token in searchTokens)
        {
            if (!_normalizedFurnitureSharedSearchText!.Contains(token, StringComparison.Ordinal)
             && !variant.MatchesToken(token))
            {
                return false;
            }
        }

        return true;
    }

    private string BuildNormalizedSearchText(IReadOnlyList<string> previewModelPaths)
    {
        HashSet<string> searchTerms = BuildSharedSearchTerms(
            _searchProfile != ObjectCatalogSearchProfile.Vfx ? Source : string.Empty,
            previewModelPaths);
        _ = SearchTermUtility.AddTerm(searchTerms, Name);

        if (_searchProfile != ObjectCatalogSearchProfile.Vfx)
        {
            _ = SearchTermUtility.AddTerm(searchTerms, RowId.ToString(CultureInfo.InvariantCulture));
        }

        if (BgObjectInfo is not null)
        {
            _ = SearchTermUtility.AddTerm(searchTerms, BgObjectInfo.SearchText);
        }

        return SearchTermUtility.BuildNormalizedSearchTextFromSet(searchTerms);
    }

    private string BuildNormalizedFurnitureSharedSearchText(IReadOnlyList<string> previewModelPaths)
        => SearchTermUtility.BuildNormalizedSearchTextFromSet(BuildSharedSearchTerms(Source, previewModelPaths));

    private HashSet<string> BuildSharedSearchTerms(string source, IReadOnlyList<string> previewModelPaths)
    {
        HashSet<string> searchTerms = SearchTermUtility.CreateSet(source, PlacementPath, AssetPath);
        _ = SearchTermUtility.AddTerms(searchTerms, previewModelPaths);

        if (_additionalSearchTerms is not null)
        {
            _ = SearchTermUtility.AddTerms(searchTerms, _additionalSearchTerms);
        }

        return searchTerms;
    }

    private IReadOnlyList<PreviewModelInfo> ResolvePreviewModels()
    {
        if (_configuredPreviewModels is { Count: > 0 })
        {
            return _configuredPreviewModels;
        }

        return Kind == ObjectCatalogKind.BgObject && !string.IsNullOrWhiteSpace(PlacementPath)
            ? [new PreviewModelInfo(PlacementPath, Matrix4x4.Identity)]
            : [];
    }
}

internal sealed class ObjectCatalogSection
{
    private readonly Lock _cacheLock = new();
    private readonly ObjectCatalogEntry[] _entries;
    private readonly Dictionary<string, ObjectCatalogEntry> _entriesByPlacementPath;
    private readonly ObjectCatalogFurnitureResult[] _defaultFurnitureResults;
    private readonly ObjectCatalogFilterCount[] _sourceFilters;
    private readonly ObjectCatalogFilterCount[] _categoryFilters;
    private string _cachedSourceFilterText = string.Empty;
    private string _cachedSourceGroup = string.Empty;
    private ObjectCatalogEntry[]? _cachedSourceEntries;
    private string _cachedFurnitureFilterText = string.Empty;
    private string _cachedFurnitureCategory = string.Empty;
    private ObjectCatalogFurnitureResult[]? _cachedFurnitureResults;

    public ObjectCatalogSection(ObjectCatalogKind kind, string displayName, IReadOnlyList<ObjectCatalogEntry> entries)
    {
        Kind = kind;
        DisplayName = displayName;
        _entries = entries as ObjectCatalogEntry[] ?? entries.ToArray();
        _entriesByPlacementPath = new Dictionary<string, ObjectCatalogEntry>(_entries.Length, StringComparer.OrdinalIgnoreCase);

        foreach (ObjectCatalogEntry entry in _entries)
        {
            _entriesByPlacementPath[GameAssetPathRules.NormalizeGamePath(entry.PlacementPath)] = entry;
        }

        _defaultFurnitureResults = kind == ObjectCatalogKind.Furniture
            ? BuildDefaultFurnitureResults(_entries)
            : [];
        _sourceFilters = BuildFilterCounts(_entries, static entry => entry.Source);
        _categoryFilters = kind == ObjectCatalogKind.Furniture
            ? BuildCategoryFilterCounts(_entries)
            : [];
    }

    public ObjectCatalogKind Kind { get; }
    public string DisplayName { get; }
    public IReadOnlyList<ObjectCatalogEntry> Entries => _entries;
    public IReadOnlyList<ObjectCatalogFilterCount> SourceFilters => _sourceFilters;
    public IReadOnlyList<ObjectCatalogFilterCount> CategoryFilters => _categoryFilters;
    public int Count => _entries.Length;

    public bool TryResolveEntry(string placementPath, [NotNullWhen(true)] out ObjectCatalogEntry? entry)
        => _entriesByPlacementPath.TryGetValue(GameAssetPathRules.NormalizeGamePath(placementPath), out entry);

    internal void PrepareSearchIndex(LoadProgress progress)
    {
        ParallelOptions options = new()
        {
            CancellationToken = progress.CancellationToken,
            MaxDegreeOfParallelism = Math.Min(2, Environment.ProcessorCount),
        };
        var completedCount = 0;
        Parallel.For(0, _entries.Length, options, index =>
        {
            _entries[index].PrepareSearch();
            int completed = Interlocked.Increment(ref completedCount);
            progress.ReportItems(completed, _entries.Length, 1024);
        });
    }

    public IReadOnlyList<ObjectCatalogEntry> FilterBySource(string filter, string source)
    {
        string normalizedFilter = TextUtility.TrimOrEmpty(filter);
        string normalizedSource = TextUtility.TrimOrEmpty(source);

        if (normalizedFilter.Length == 0 && normalizedSource.Length == 0)
        {
            return _entries;
        }

        lock (_cacheLock)
        {
            if (string.Equals(_cachedSourceFilterText, normalizedFilter, StringComparison.Ordinal)
             && string.Equals(_cachedSourceGroup, normalizedSource, StringComparison.Ordinal)
             && _cachedSourceEntries is not null)
            {
                return _cachedSourceEntries;
            }
        }

        ObjectCatalogEntry[] candidates = GetSearchCandidates(normalizedFilter, out string[] searchTokens);
        ObjectCatalogEntry[] filteredEntries = FilterEntries(candidates, searchTokens, normalizedSource);
        lock (_cacheLock)
        {
            _cachedSourceFilterText = normalizedFilter;
            _cachedSourceGroup = normalizedSource;
            _cachedSourceEntries = filteredEntries;
            return _cachedSourceEntries;
        }
    }

    public IReadOnlyList<ObjectCatalogFurnitureResult> FilterFurniture(string filter, string category)
    {
        string normalizedFilter = TextUtility.TrimOrEmpty(filter);
        string normalizedCategory = TextUtility.TrimOrEmpty(category);

        if (normalizedFilter.Length == 0 && normalizedCategory.Length == 0)
        {
            return _defaultFurnitureResults;
        }

        lock (_cacheLock)
        {
            if (string.Equals(_cachedFurnitureFilterText, normalizedFilter, StringComparison.Ordinal)
             && string.Equals(_cachedFurnitureCategory, normalizedCategory, StringComparison.Ordinal)
             && _cachedFurnitureResults is not null)
            {
                return _cachedFurnitureResults;
            }
        }

        ObjectCatalogEntry[] candidates = GetSearchCandidates(normalizedFilter, out string[] searchTokens);
        ObjectCatalogFurnitureResult[] filteredEntries = ReferenceEquals(candidates, _entries)
            && searchTokens.Length == 0 && normalizedCategory.Length == 0
                ? _defaultFurnitureResults
                : FilterFurnitureResults(candidates, searchTokens, normalizedCategory);
        lock (_cacheLock)
        {
            _cachedFurnitureFilterText = normalizedFilter;
            _cachedFurnitureCategory = normalizedCategory;
            _cachedFurnitureResults = filteredEntries;
            return _cachedFurnitureResults;
        }
    }

    private ObjectCatalogEntry[] GetSearchCandidates(string filter, out string[] searchTokens)
    {
        string path = filter.Length >= 2 && filter[0] == '"' && filter[^1] == '"' ? filter[1..^1] : filter;
        string normalizedPath = GameAssetPathRules.NormalizeGamePath(path);

        if (normalizedPath.Contains('/')
            && Path.HasExtension(normalizedPath)
            && !normalizedPath.Any(char.IsWhiteSpace))
        {
            searchTokens = [];
            return TryResolveEntry(normalizedPath, out ObjectCatalogEntry? entry) ? [entry] : [];
        }

        searchTokens = SearchTermUtility.BuildSearchTokens(filter);
        return _entries;
    }

    private static ObjectCatalogEntry[] FilterEntries(
        ObjectCatalogEntry[] entries,
        IReadOnlyList<string> searchTokens,
        string source)
    {
        if (searchTokens.Count == 0 && source.Length == 0)
        {
            return entries;
        }

        List<ObjectCatalogEntry> filteredEntries = [];
        foreach (ObjectCatalogEntry entry in entries)
        {
            if (source.Length > 0
             && !string.Equals(entry.Source, source, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (searchTokens.Count > 0 && !entry.Matches(searchTokens))
            {
                continue;
            }

            filteredEntries.Add(entry);
        }

        return filteredEntries.ToArray();
    }

    private static ObjectCatalogFurnitureResult[] BuildDefaultFurnitureResults(ObjectCatalogEntry[] entries)
    {
        List<ObjectCatalogFurnitureResult> results = [];
        foreach (ObjectCatalogEntry entry in entries)
        {
            if (entry.FurnitureInfo is not { } furnitureInfo)
            {
                continue;
            }

            results.Add(new ObjectCatalogFurnitureResult(entry, furnitureInfo.PrimaryVariant));
        }

        return results.ToArray();
    }

    private static ObjectCatalogFurnitureResult[] FilterFurnitureResults(
        ObjectCatalogEntry[] entries,
        IReadOnlyList<string> searchTokens,
        string category)
    {
        List<ObjectCatalogFurnitureResult> results = [];
        foreach (ObjectCatalogEntry entry in entries)
        {
            if (entry.TryResolveFurnitureFilterVariant(searchTokens, category, out ObjectCatalogFurnitureVariant? variant))
            {
                results.Add(new ObjectCatalogFurnitureResult(entry, variant));
            }
        }

        return results.ToArray();
    }

    private static ObjectCatalogFilterCount[] BuildFilterCounts(
        ObjectCatalogEntry[] entries,
        Func<ObjectCatalogEntry, string?> selector)
    {
        Dictionary<string, int> counts = new(StringComparer.OrdinalIgnoreCase);
        foreach (ObjectCatalogEntry entry in entries)
        {
            string? label = selector(entry);
            if (string.IsNullOrWhiteSpace(label))
            {
                continue;
            }

            IncrementFilterCount(counts, label);
        }

        return MaterializeFilterCounts(counts);
    }

    private static ObjectCatalogFilterCount[] BuildCategoryFilterCounts(ObjectCatalogEntry[] entries)
    {
        Dictionary<string, int> counts = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> entryCategories = new(StringComparer.OrdinalIgnoreCase);
        foreach (ObjectCatalogEntry entry in entries)
        {
            entryCategories.Clear();
            if (entry.FurnitureInfo is null)
            {
                continue;
            }

            foreach (string category in entry.FurnitureInfo.EnumerateCategories())
            {
                if (!entryCategories.Add(category))
                {
                    continue;
                }

                IncrementFilterCount(counts, category);
            }
        }

        return MaterializeFilterCounts(counts);
    }

    private static void IncrementFilterCount(Dictionary<string, int> counts, string label)
        => counts[label] = counts.TryGetValue(label, out int count)
            ? count + 1
            : 1;

    private static ObjectCatalogFilterCount[] MaterializeFilterCounts(Dictionary<string, int> counts)
    {
        if (counts.Count == 0)
        {
            return [];
        }

        ObjectCatalogFilterCount[] filterCounts = new ObjectCatalogFilterCount[counts.Count];
        var index = 0;
        foreach ((string label, int count) in counts)
        {
            filterCounts[index++] = new ObjectCatalogFilterCount(label, count);
        }

        Array.Sort(filterCounts, static (left, right) =>
        {
            var countComparison = right.Count.CompareTo(left.Count);
            return countComparison != 0
                ? countComparison
                : StringComparer.OrdinalIgnoreCase.Compare(left.Label, right.Label);
        });

        return filterCounts;
    }
}

internal sealed class ObjectCatalogData
{
    private sealed class CatalogState
    {
        public CatalogState(
            ObjectCatalogSection bgObjects,
            ObjectCatalogSection furniture,
            ObjectCatalogSection vfx)
        {
            BgObjects = bgObjects;
            Furniture = furniture;
            Vfx = vfx;
            EntryCount = bgObjects.Count + furniture.Count + vfx.Count;
        }

        public ObjectCatalogSection BgObjects { get; }
        public ObjectCatalogSection Furniture { get; }
        public ObjectCatalogSection Vfx { get; }
        public int EntryCount { get; }
    }

    private CatalogState _state;

    public ObjectCatalogData(
        IReadOnlyList<ObjectCatalogEntry> bgObjectEntries,
        IReadOnlyList<ObjectCatalogEntry> furnitureEntries,
        IReadOnlyList<ObjectCatalogEntry> vfxEntries)
    {
        _state = BuildState(bgObjectEntries, furnitureEntries, vfxEntries);
    }

    public ObjectCatalogSection BgObjects => Volatile.Read(ref _state).BgObjects;
    public ObjectCatalogSection Furniture => Volatile.Read(ref _state).Furniture;
    public ObjectCatalogSection Vfx => Volatile.Read(ref _state).Vfx;
    public int EntryCount => Volatile.Read(ref _state).EntryCount;

    public void PrepareSearchIndexes(LoadProgress progress)
    {
        CatalogState state = Volatile.Read(ref _state);
        int totalCount = state.EntryCount;
        if (totalCount == 0)
        {
            progress.Report(1d);
            return;
        }

        int completedCount = 0;
        progress.Report(0d);
        PrepareSectionSearchIndex(state.BgObjects, ref completedCount, totalCount, progress);
        PrepareSectionSearchIndex(state.Furniture, ref completedCount, totalCount, progress);
        PrepareSectionSearchIndex(state.Vfx, ref completedCount, totalCount, progress);
    }

    public void ReplaceSections(
        IReadOnlyList<ObjectCatalogEntry>? bgObjectEntries = null,
        IReadOnlyList<ObjectCatalogEntry>? furnitureEntries = null,
        IReadOnlyList<ObjectCatalogEntry>? vfxEntries = null,
        CancellationToken cancellationToken = default)
    {
        CatalogState currentState = Volatile.Read(ref _state);
        ObjectCatalogSection nextBgObjects = bgObjectEntries is not null
            ? new ObjectCatalogSection(ObjectCatalogKind.BgObject, currentState.BgObjects.DisplayName, bgObjectEntries)
            : currentState.BgObjects;
        ObjectCatalogSection nextFurniture = furnitureEntries is not null
            ? new ObjectCatalogSection(ObjectCatalogKind.Furniture, currentState.Furniture.DisplayName, furnitureEntries)
            : currentState.Furniture;
        ObjectCatalogSection nextVfx = vfxEntries is not null
            ? new ObjectCatalogSection(ObjectCatalogKind.Vfx, currentState.Vfx.DisplayName, vfxEntries)
            : currentState.Vfx;

        if (bgObjectEntries is not null)
        {
            nextBgObjects.PrepareSearchIndex(LoadProgress.Unreported(cancellationToken));
        }

        if (furnitureEntries is not null)
        {
            nextFurniture.PrepareSearchIndex(LoadProgress.Unreported(cancellationToken));
        }

        if (vfxEntries is not null)
        {
            nextVfx.PrepareSearchIndex(LoadProgress.Unreported(cancellationToken));
        }

        CatalogState nextState = new(nextBgObjects, nextFurniture, nextVfx);
        cancellationToken.ThrowIfCancellationRequested();
        Volatile.Write(ref _state, nextState);
    }

    public IReadOnlyList<PreviewModelInfo> ResolvePreviewModels(ObjectCatalogKind kind, string placementPath)
        => TryResolveEntry(kind, placementPath, out ObjectCatalogEntry? entry)
            ? entry.PreviewModels
            : [];

    public bool TryResolveEntry(
        ObjectCatalogKind kind,
        string placementPath,
        [NotNullWhen(true)] out ObjectCatalogEntry? entry)
    {
        CatalogState state = Volatile.Read(ref _state);
        ObjectCatalogSection? section = kind switch
        {
            ObjectCatalogKind.BgObject => state.BgObjects,
            ObjectCatalogKind.Furniture => state.Furniture,
            ObjectCatalogKind.Vfx => state.Vfx,
            _ => null,
        };

        if (section is not null)
        {
            return section.TryResolveEntry(placementPath, out entry);
        }

        entry = null;
        return false;
    }

    public bool TryResolveFurnitureVariant(
        string sharedGroupPath,
        uint housingRowId,
        uint itemRowId,
        [NotNullWhen(true)] out ObjectCatalogEntry? entry,
        [NotNullWhen(true)] out ObjectCatalogFurnitureVariant? variant)
    {
        if (!TryResolveEntry(ObjectCatalogKind.Furniture, sharedGroupPath, out entry)
            || entry.FurnitureInfo is not { } furnitureInfo
            || !furnitureInfo.TryResolveVariant(housingRowId, itemRowId, out variant))
        {
            entry = null;
            variant = null;
            return false;
        }

        return true;
    }

    private static CatalogState BuildState(
        IReadOnlyList<ObjectCatalogEntry> bgObjectEntries,
        IReadOnlyList<ObjectCatalogEntry> furnitureEntries,
        IReadOnlyList<ObjectCatalogEntry> vfxEntries)
        => new(
            new ObjectCatalogSection(ObjectCatalogKind.BgObject, "BgObjects", bgObjectEntries),
            new ObjectCatalogSection(ObjectCatalogKind.Furniture, "Furniture", furnitureEntries),
            new ObjectCatalogSection(ObjectCatalogKind.Vfx, "VFX", vfxEntries));

    private static void PrepareSectionSearchIndex(
        ObjectCatalogSection section,
        ref int completedCount,
        int totalCount,
        LoadProgress progress)
    {
        double start = completedCount / (double)totalCount;
        completedCount += section.Count;
        double end = completedCount / (double)totalCount;
        section.PrepareSearchIndex(progress.Slice(start, end));
    }
}
