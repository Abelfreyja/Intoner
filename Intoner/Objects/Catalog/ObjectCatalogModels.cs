using Intoner.Objects.Assets;
using Intoner.Objects.Utils;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Numerics;

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
    string Placement,
    bool DestroyOnRemoval)
{
    private string? _searchText;
    private string? _normalizedSearchText;

    public string SearchText
        => _searchText ??= BuildSearchText();

    public bool MatchesToken(string normalizedToken)
        => NormalizedSearchText.Contains(normalizedToken, StringComparison.Ordinal);

    private string NormalizedSearchText
        => _normalizedSearchText ??= ObjectSearchTermUtility.NormalizeSearchText(SearchText);

    private string BuildSearchText()
        => ObjectSearchTermUtility.BuildSearchText(
        [
            HousingRowId.ToString(CultureInfo.InvariantCulture),
            ItemRowId.ToString(CultureInfo.InvariantCulture),
            Name,
            IconId.ToString(CultureInfo.InvariantCulture),
            DyeCount.ToString(CultureInfo.InvariantCulture),
            HousingMetadata.SearchText,
            Category,
            Placement,
            DestroyOnRemoval ? "destroy on removal" : "persistent",
        ]);
}

internal sealed class ObjectCatalogFurnitureInfo
{
    private readonly ObjectCatalogFurnitureVariant[] _variants;
    private string? _searchText;

    public ObjectCatalogFurnitureInfo(ushort modelKey, ObjectCatalogFurnitureVariant primaryVariant)
    {
        ModelKey       = modelKey;
        PrimaryVariant = primaryVariant;
        _variants      = [primaryVariant];
    }

    private ObjectCatalogFurnitureInfo(
        ushort modelKey,
        ObjectCatalogFurnitureVariant primaryVariant,
        ObjectCatalogFurnitureVariant[] variants)
    {
        ModelKey       = modelKey;
        PrimaryVariant = primaryVariant;
        _variants      = variants;
    }

    public ushort ModelKey { get; }

    public ObjectCatalogFurnitureVariant PrimaryVariant { get; }

    public IReadOnlyList<ObjectCatalogFurnitureVariant> Variants
        => _variants;

    public HousingFurnitureMetadata HousingMetadata
        => PrimaryVariant.HousingMetadata;

    public string SearchText
        => _searchText ??= BuildSearchText();

    public ObjectCatalogFurnitureInfo AddVariant(ObjectCatalogFurnitureVariant variant)
    {
        if (HasHousingRowId(variant.HousingRowId))
        {
            return this;
        }

        ObjectCatalogFurnitureVariant[] variants = new ObjectCatalogFurnitureVariant[_variants.Length + 1];
        Array.Copy(_variants, variants, _variants.Length);
        variants[^1] = variant;
        return new ObjectCatalogFurnitureInfo(ModelKey, PrimaryVariant, variants);
    }

    public ObjectCatalogFurnitureInfo AddVariants(IEnumerable<ObjectCatalogFurnitureVariant> variants)
    {
        ObjectCatalogFurnitureInfo info = this;
        foreach (ObjectCatalogFurnitureVariant variant in variants)
        {
            info = info.AddVariant(variant);
        }

        return info;
    }

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
        => TryResolveVariantByHousingRowId(housingRowId, out variant)
        || TryResolveVariantByItemRowId(itemRowId, out variant);

    private string BuildSearchText()
        => ObjectSearchTermUtility.BuildSearchText(BuildSearchTerms());

    private bool HasHousingRowId(uint housingRowId)
    {
        foreach (ObjectCatalogFurnitureVariant variant in _variants)
        {
            if (variant.HousingRowId == housingRowId)
            {
                return true;
            }
        }

        return false;
    }

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
        => _searchText ??= ObjectSearchTermUtility.BuildSearchText(BuildSearchTerms());

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
    private string? _searchText;

    public static ObjectCatalogBgObjectInfo? Create(
        IReadOnlyList<uint> territoryIds,
        IReadOnlyList<string> territoryNames)
        => territoryIds.Count == 0 && territoryNames.Count == 0
            ? null
            : new ObjectCatalogBgObjectInfo(territoryIds, territoryNames);

    public string SearchText
        => _searchText ??= BuildSearchText();

    private string BuildSearchText()
        => ObjectSearchTermUtility.BuildSearchText(BuildSearchTerms());

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

internal enum ObjectCatalogSearchProfile
{
    Default,
    Vfx,
}

internal sealed record ObjectCatalogEntry
{
    private readonly string _normalizedSearchText;
    private readonly string _normalizedFurnitureSharedSearchText;

    public ObjectCatalogEntry(
        ObjectCatalogKind kind,
        string source,
        uint rowId,
        string name,
        string placementPath,
        string assetPath,
        ObjectCatalogFurnitureInfo? furnitureInfo = null,
        ObjectCatalogBgObjectInfo? bgObjectInfo = null,
        IReadOnlyList<PreviewModelInfo>? previewModels = null,
        IReadOnlyList<string>? previewModelPaths = null,
        IReadOnlyList<string>? additionalSearchTerms = null,
        ObjectCatalogSearchProfile searchProfile = ObjectCatalogSearchProfile.Default)
    {
        Kind              = kind;
        Source            = source;
        RowId             = rowId;
        Name              = name;
        PlacementPath     = placementPath;
        AssetPath         = assetPath;
        FurnitureInfo     = furnitureInfo;
        BgObjectInfo      = bgObjectInfo;
        PreviewModels     = ResolvePreviewModels(kind, placementPath, previewModels, previewModelPaths);
        PreviewModelPaths = ResolvePreviewModelPaths(PreviewModels, previewModelPaths);
        string baseSearchText = BuildSearchText(
            source,
            rowId,
            name,
            placementPath,
            assetPath,
            bgObjectInfo,
            PreviewModelPaths,
            additionalSearchTerms,
            searchProfile);
        string furnitureSharedSearchText = furnitureInfo is not null
            ? BuildFurnitureSharedSearchText(
                source,
                placementPath,
                assetPath,
                PreviewModelPaths,
                additionalSearchTerms)
            : baseSearchText;
        string searchText = furnitureInfo is not null
            ? ObjectSearchTermUtility.BuildSearchText([baseSearchText, furnitureInfo.SearchText])
            : baseSearchText;
        _normalizedSearchText = ObjectSearchTermUtility.NormalizeSearchText(searchText);
        _normalizedFurnitureSharedSearchText = ObjectSearchTermUtility.NormalizeSearchText(furnitureSharedSearchText);
    }

    public ObjectCatalogKind Kind { get; }
    public string Source { get; }
    public uint RowId { get; }
    public string Name { get; }
    public string PlacementPath { get; }
    public string AssetPath { get; }
    public ObjectCatalogFurnitureInfo? FurnitureInfo { get; }
    public ObjectCatalogBgObjectInfo? BgObjectInfo { get; }
    public IReadOnlyList<PreviewModelInfo> PreviewModels { get; }
    public IReadOnlyList<string> PreviewModelPaths { get; }

    public string DisplayPath
        => string.IsNullOrWhiteSpace(AssetPath)
            ? PlacementPath
            : AssetPath;

    public bool Matches(IReadOnlyList<string> searchTokens)
        => ObjectSearchTermUtility.MatchesNormalizedSearchText(_normalizedSearchText, searchTokens);

    public bool TryResolveFurnitureFilterVariant(
        IReadOnlyList<string> searchTokens,
        string category,
        [NotNullWhen(true)] out ObjectCatalogFurnitureVariant? variant)
    {
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
            if (!_normalizedFurnitureSharedSearchText.Contains(token, StringComparison.Ordinal)
             && !variant.MatchesToken(token))
            {
                return false;
            }
        }

        return true;
    }

    private static string BuildSearchText(
        string source,
        uint rowId,
        string name,
        string placementPath,
        string assetPath,
        ObjectCatalogBgObjectInfo? bgObjectInfo,
        IReadOnlyList<string> previewModelPaths,
        IReadOnlyList<string>? additionalSearchTerms,
        ObjectCatalogSearchProfile searchProfile)
    {
        return ObjectSearchTermUtility.BuildSearchText(EnumerateSearchTerms());

        IEnumerable<string?> EnumerateSearchTerms()
        {
            yield return name;

            if (searchProfile != ObjectCatalogSearchProfile.Vfx)
            {
                yield return source;
                yield return rowId.ToString(CultureInfo.InvariantCulture);
            }

            yield return placementPath;
            yield return assetPath;

            foreach (string previewModelPath in previewModelPaths)
            {
                yield return previewModelPath;
            }

            if (additionalSearchTerms is not null)
            {
                foreach (string additionalTerm in additionalSearchTerms)
                {
                    yield return additionalTerm;
                }
            }

            if (bgObjectInfo is not null)
            {
                yield return bgObjectInfo.SearchText;
            }
        }
    }

    private static string BuildFurnitureSharedSearchText(
        string source,
        string placementPath,
        string assetPath,
        IReadOnlyList<string> previewModelPaths,
        IReadOnlyList<string>? additionalSearchTerms)
    {
        return ObjectSearchTermUtility.BuildSearchText(EnumerateSearchTerms());

        IEnumerable<string?> EnumerateSearchTerms()
        {
            yield return source;
            yield return placementPath;
            yield return assetPath;

            foreach (string previewModelPath in previewModelPaths)
            {
                yield return previewModelPath;
            }

            if (additionalSearchTerms is not null)
            {
                foreach (string additionalTerm in additionalSearchTerms)
                {
                    yield return additionalTerm;
                }
            }
        }
    }

    private static IReadOnlyList<PreviewModelInfo> ResolvePreviewModels(
        ObjectCatalogKind kind,
        string placementPath,
        IReadOnlyList<PreviewModelInfo>? previewModels,
        IReadOnlyList<string>? previewModelPaths)
    {
        if (previewModels is { Count: > 0 })
        {
            return previewModels;
        }

        if (previewModelPaths is { Count: > 0 })
        {
            return previewModelPaths
                .Select(static modelPath => new PreviewModelInfo(modelPath, Matrix4x4.Identity))
                .ToArray();
        }

        return kind == ObjectCatalogKind.BgObject && !string.IsNullOrWhiteSpace(placementPath)
            ? [new PreviewModelInfo(placementPath, Matrix4x4.Identity)]
            : [];
    }

    private static IReadOnlyList<string> ResolvePreviewModelPaths(
        IReadOnlyList<PreviewModelInfo> previewModels,
        IReadOnlyList<string>? previewModelPaths)
    {
        if (previewModelPaths is { Count: > 0 })
        {
            return previewModelPaths;
        }

        return previewModels
            .Select(static model => model.ModelPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}

internal sealed class ObjectCatalogSection
{
    private readonly Lock _cacheLock = new();
    private readonly ObjectCatalogEntry[] _entries;
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
        _defaultFurnitureResults = BuildDefaultFurnitureResults(_entries);
        _sourceFilters = BuildFilterCounts(_entries, static entry => entry.Source);
        _categoryFilters = BuildCategoryFilterCounts(_entries);
    }

    public ObjectCatalogKind Kind { get; }
    public string DisplayName { get; }
    public IReadOnlyList<ObjectCatalogEntry> Entries => _entries;
    public IReadOnlyList<ObjectCatalogFilterCount> SourceFilters => _sourceFilters;
    public IReadOnlyList<ObjectCatalogFilterCount> CategoryFilters => _categoryFilters;
    public int Count => _entries.Length;

    public IReadOnlyList<ObjectCatalogEntry> FilterBySource(string filter, string source)
    {
        string normalizedFilter = ObjectStringUtility.TrimOrEmpty(filter);
        string normalizedSource = ObjectStringUtility.TrimOrEmpty(source);
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

        ObjectCatalogEntry[] filteredEntries = FilterEntries(_entries, normalizedFilter, normalizedSource);
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
        string normalizedFilter = ObjectStringUtility.TrimOrEmpty(filter);
        string normalizedCategory = ObjectStringUtility.TrimOrEmpty(category);
        string[] searchTokens = ObjectSearchTermUtility.BuildSearchTokens(normalizedFilter);
        if (searchTokens.Length == 0 && normalizedCategory.Length == 0)
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

        ObjectCatalogFurnitureResult[] filteredEntries = FilterFurnitureResults(_entries, searchTokens, normalizedCategory);
        lock (_cacheLock)
        {
            _cachedFurnitureFilterText = normalizedFilter;
            _cachedFurnitureCategory = normalizedCategory;
            _cachedFurnitureResults = filteredEntries;
            return _cachedFurnitureResults;
        }
    }

    private static ObjectCatalogEntry[] FilterEntries(
        ObjectCatalogEntry[] entries,
        string filter,
        string? source)
    {
        string[] searchTokens = ObjectSearchTermUtility.BuildSearchTokens(filter);
        List<ObjectCatalogEntry> filteredEntries = [];
        foreach (ObjectCatalogEntry entry in entries)
        {
            if (source is not null
             && source.Length > 0
             && !string.Equals(entry.Source, source, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (searchTokens.Length > 0 && !entry.Matches(searchTokens))
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
            ObjectCatalogSection vfx,
            IReadOnlyDictionary<ObjectCatalogEntryKey, ObjectCatalogEntry> entriesByPlacementPath)
        {
            BgObjects = bgObjects;
            Furniture = furniture;
            Vfx = vfx;
            EntriesByPlacementPath = entriesByPlacementPath;
            EntryCount = bgObjects.Count + furniture.Count + vfx.Count;
        }

        public ObjectCatalogSection BgObjects { get; }
        public ObjectCatalogSection Furniture { get; }
        public ObjectCatalogSection Vfx { get; }
        public int EntryCount { get; }
        public IReadOnlyDictionary<ObjectCatalogEntryKey, ObjectCatalogEntry> EntriesByPlacementPath { get; }
    }

    private static readonly IEqualityComparer<ObjectCatalogEntryKey> EntryKeyComparer = ObjectCatalogEntryKeyComparer.Instance;

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

    public void ReplaceSections(
        IReadOnlyList<ObjectCatalogEntry>? bgObjectEntries = null,
        IReadOnlyList<ObjectCatalogEntry>? furnitureEntries = null,
        IReadOnlyList<ObjectCatalogEntry>? vfxEntries = null)
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

        CatalogState nextState = BuildState(nextBgObjects, nextFurniture, nextVfx);
        Volatile.Write(ref _state, nextState);
    }

    public IReadOnlyList<string> ResolvePreviewModelPaths(ObjectCatalogKind kind, string placementPath)
        => TryResolveEntry(kind, placementPath, out ObjectCatalogEntry? entry)
            ? entry.PreviewModelPaths
            : [];

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
        return state.EntriesByPlacementPath.TryGetValue(new ObjectCatalogEntryKey(kind, placementPath), out entry);
    }

    public bool TryResolveFurnitureMetadata(
        string sharedGroupPath,
        uint housingRowId,
        uint itemRowId,
        [NotNullWhen(true)] out HousingFurnitureMetadata? metadata)
    {
        if (!TryResolveEntry(ObjectCatalogKind.Furniture, sharedGroupPath, out ObjectCatalogEntry? entry)
            || entry.FurnitureInfo is not { } furnitureInfo)
        {
            metadata = null;
            return false;
        }

        if (furnitureInfo.TryResolveVariant(housingRowId, itemRowId, out ObjectCatalogFurnitureVariant? housingVariant))
        {
            metadata = housingVariant.HousingMetadata;
            return true;
        }

        metadata = furnitureInfo.HousingMetadata;
        return true;
    }

    private static CatalogState BuildState(
        IReadOnlyList<ObjectCatalogEntry> bgObjectEntries,
        IReadOnlyList<ObjectCatalogEntry> furnitureEntries,
        IReadOnlyList<ObjectCatalogEntry> vfxEntries)
        => BuildState(
            new ObjectCatalogSection(ObjectCatalogKind.BgObject, "BgObjects", bgObjectEntries),
            new ObjectCatalogSection(ObjectCatalogKind.Furniture, "Furniture", furnitureEntries),
            new ObjectCatalogSection(ObjectCatalogKind.Vfx, "VFX", vfxEntries));

    private static CatalogState BuildState(
        ObjectCatalogSection bgObjects,
        ObjectCatalogSection furniture,
        ObjectCatalogSection vfx)
    {
        Dictionary<ObjectCatalogEntryKey, ObjectCatalogEntry> entriesByPlacementPath = BuildEntryIndex(bgObjects, furniture, vfx);
        return new CatalogState(bgObjects, furniture, vfx, entriesByPlacementPath);
    }

    private static Dictionary<ObjectCatalogEntryKey, ObjectCatalogEntry> BuildEntryIndex(
        ObjectCatalogSection bgObjects,
        ObjectCatalogSection furniture,
        ObjectCatalogSection vfx)
    {
        var entriesByPlacementPath = new Dictionary<ObjectCatalogEntryKey, ObjectCatalogEntry>(
            bgObjects.Count + furniture.Count + vfx.Count,
            EntryKeyComparer);

        AddEntries(entriesByPlacementPath, bgObjects.Entries);
        AddEntries(entriesByPlacementPath, furniture.Entries);
        AddEntries(entriesByPlacementPath, vfx.Entries);
        return entriesByPlacementPath;
    }

    private static void AddEntries(
        Dictionary<ObjectCatalogEntryKey, ObjectCatalogEntry> entriesByPlacementPath,
        IReadOnlyList<ObjectCatalogEntry> entries)
    {
        foreach (ObjectCatalogEntry entry in entries)
        {
            entriesByPlacementPath[new ObjectCatalogEntryKey(entry.Kind, entry.PlacementPath)] = entry;
        }
    }

    private readonly record struct ObjectCatalogEntryKey(ObjectCatalogKind Kind, string PlacementPath);

    private sealed class ObjectCatalogEntryKeyComparer : IEqualityComparer<ObjectCatalogEntryKey>
    {
        public static ObjectCatalogEntryKeyComparer Instance { get; } = new();

        public bool Equals(ObjectCatalogEntryKey x, ObjectCatalogEntryKey y)
            => x.Kind == y.Kind
            && StringComparer.OrdinalIgnoreCase.Equals(x.PlacementPath, y.PlacementPath);

        public int GetHashCode(ObjectCatalogEntryKey obj)
            => HashCode.Combine((int)obj.Kind, StringComparer.OrdinalIgnoreCase.GetHashCode(obj.PlacementPath));
    }
}

