using Dalamud.Plugin.Services;
using Intoner.Objects.Assets;
using Intoner.Objects.Utils;
using Intoner.Services.Loading;
using Lumina.Excel.Sheets;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace Intoner.Objects.Catalog;

internal sealed class ObjectCatalogBuilder
{
    private readonly record struct CatalogCandidate(ObjectCatalogEntry Entry, int Priority);

    private static readonly SharedGroupAssetInfo EmptySharedGroups = new([], [], [], [], []);
    private static readonly IComparer<ObjectCatalogEntry> NameSourcePathComparer = Comparer<ObjectCatalogEntry>.Create(static (left, right) =>
    {
        var nameComparison = StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name);
        if (nameComparison != 0)
        {
            return nameComparison;
        }

        var sourceComparison = StringComparer.OrdinalIgnoreCase.Compare(left.Source, right.Source);
        if (sourceComparison != 0)
        {
            return sourceComparison;
        }

        return StringComparer.OrdinalIgnoreCase.Compare(left.DisplayPath, right.DisplayPath);
    });
    private static readonly IComparer<ObjectCatalogEntry> SourceNamePathComparer = Comparer<ObjectCatalogEntry>.Create(static (left, right) =>
    {
        var sourceComparison = StringComparer.OrdinalIgnoreCase.Compare(left.Source, right.Source);
        if (sourceComparison != 0)
        {
            return sourceComparison;
        }

        var nameComparison = StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name);
        if (nameComparison != 0)
        {
            return nameComparison;
        }

        return StringComparer.OrdinalIgnoreCase.Compare(left.DisplayPath, right.DisplayPath);
    });

    private readonly IDataManager _gameData;
    private readonly IObjectAssetIndex _assetIndex;
    private readonly ILogger<ObjectCatalogBuilder> _logger;

    public ObjectCatalogBuilder(
        ILogger<ObjectCatalogBuilder> logger,
        IDataManager gameData,
        IObjectAssetIndex assetIndex)
    {
        _logger = logger;
        _gameData = gameData;
        _assetIndex = assetIndex;
    }

    public ObjectCatalogData Build(LoadProgress progress)
    {
        LoadProgressPlan progressPlan = progress.CreatePlan();
        LoadProgress bgObjectProgress = progressPlan.Next(38d);
        LoadProgress furnitureProgress = progressPlan.Next(6d);
        LoadProgress vfxProgress = progressPlan.Next(7d);
        LoadProgress indexProgress = progressPlan.Next(4d);
        LoadProgress searchProgress = progressPlan.Next(45d);
        long startedAt = Stopwatch.GetTimestamp();
        long phaseStartedAt = startedAt;
        IReadOnlyList<ObjectCatalogEntry> bgObjectEntries = BuildBgObjectEntries(bgObjectProgress);
        TimeSpan bgObjectTime = Stopwatch.GetElapsedTime(phaseStartedAt);

        phaseStartedAt = Stopwatch.GetTimestamp();
        IReadOnlyList<ObjectCatalogEntry> furnitureEntries = BuildFurnitureEntries(furnitureProgress);
        TimeSpan furnitureTime = Stopwatch.GetElapsedTime(phaseStartedAt);

        phaseStartedAt = Stopwatch.GetTimestamp();
        IReadOnlyList<ObjectCatalogEntry> vfxEntries = BuildVfxEntries(vfxProgress);
        TimeSpan vfxTime = Stopwatch.GetElapsedTime(phaseStartedAt);

        phaseStartedAt = Stopwatch.GetTimestamp();
        ObjectCatalogData catalog = new(bgObjectEntries, furnitureEntries, vfxEntries);
        indexProgress.Report(1d);
        TimeSpan indexTime = Stopwatch.GetElapsedTime(phaseStartedAt);

        phaseStartedAt = Stopwatch.GetTimestamp();
        catalog.PrepareSearchIndexes(searchProgress);
        TimeSpan searchTime = Stopwatch.GetElapsedTime(phaseStartedAt);
        TimeSpan totalTime = Stopwatch.GetElapsedTime(startedAt);

        _logger.LogInformation(
            "built object catalog data in {ElapsedMilliseconds:F0} ms (bgobjects {BgObjectMilliseconds:F0} ms, furniture {FurnitureMilliseconds:F0} ms, vfx {VfxMilliseconds:F0} ms, indexes {IndexMilliseconds:F0} ms, search {SearchMilliseconds:F0} ms)",
            totalTime.TotalMilliseconds,
            bgObjectTime.TotalMilliseconds,
            furnitureTime.TotalMilliseconds,
            vfxTime.TotalMilliseconds,
            indexTime.TotalMilliseconds,
            searchTime.TotalMilliseconds);
        return catalog;
    }

    public IReadOnlyList<ObjectCatalogEntry> BuildBgObjectEntries(LoadProgress progress)
    {
        CancellationToken cancellationToken = progress.CancellationToken;
        IReadOnlyList<GameDataBgObjectAsset> gameDataAssets = _assetIndex.GetGameDataBgObjectAssets(cancellationToken);
        IReadOnlyList<ObservedBgAsset> observedAssets = _assetIndex.GetObservedBgObjectAssets(cancellationToken);
        Dictionary<string, CatalogCandidate> entries = new(gameDataAssets.Count + observedAssets.Count, StringComparer.OrdinalIgnoreCase);
        int completedAssetCount = 0;
        int totalAssetCount = gameDataAssets.Count + observedAssets.Count;
        progress.ReportItems(completedAssetCount, totalAssetCount);

        foreach (GameDataBgObjectAsset asset in gameDataAssets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddBgObjectEntry(
                entries,
                asset.Source,
                11,
                asset.RowId,
                ObjectCatalogLabelUtility.BuildPathLabel(asset.Source, asset.SourcePath),
                asset.ModelPath,
                BuildBgObjectInfo(asset),
                asset.SearchTerms);
            progress.ReportItems(++completedAssetCount, totalAssetCount);
        }

        foreach (ObservedBgAsset observedAsset in observedAssets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddBgObjectEntry(
                entries,
                observedAsset.Source,
                20,
                rowId: 0,
                ObjectCatalogLabelUtility.BuildPathLabel(observedAsset.Source, observedAsset.Path),
                observedAsset.Path,
                BuildBgObjectInfo(observedAsset.TerritoryIds, observedAsset.TerritoryNames),
                observedAsset.SearchTerms);
            progress.ReportItems(++completedAssetCount, totalAssetCount);
        }

        return MaterializeEntries(entries, NameSourcePathComparer);
    }

    public IReadOnlyList<ObjectCatalogEntry> BuildFurnitureEntries(LoadProgress progress)
    {
        CancellationToken cancellationToken = progress.CancellationToken;
        var housingFurniture = _gameData.GetExcelSheet<HousingFurniture>()!;
        var housingYardObjects = _gameData.GetExcelSheet<HousingYardObject>()!;
        Dictionary<uint, HousingPileFootprint> pileFootprints = BuildPileFootprints(_gameData.GetExcelSheet<HousingPileLimit>()!);
        Dictionary<string, CatalogCandidate> entries = new(StringComparer.OrdinalIgnoreCase);
        progress.Report(0d);

        foreach (HousingFurniture row in housingFurniture)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (row.RowId == 0 || row.ModelKey == 0)
            {
                continue;
            }

            AddFurnitureEntry(
                entries,
                "housing furniture",
                0,
                row,
                GameDataAssetPathUtility.BuildIndoorHousingSharedGroupPath(row.ModelKey),
                pileFootprints);
        }
        progress.Report(0.5d);

        foreach (HousingYardObject row in housingYardObjects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (row.RowId == 0 || row.ModelKey == 0)
            {
                continue;
            }

            AddFurnitureEntry(
                entries,
                "housing yard object",
                1,
                row,
                GameDataAssetPathUtility.BuildOutdoorHousingSharedGroupPath(row.ModelKey));
        }
        progress.Report(1d);

        return MaterializeEntries(entries, NameSourcePathComparer);
    }

    public IReadOnlyList<ObjectCatalogEntry> BuildVfxEntries(LoadProgress progress)
    {
        CancellationToken cancellationToken = progress.CancellationToken;
        IReadOnlyList<RuntimeVfxAsset> standaloneVfxAssets = _assetIndex.GetStandaloneVfxAssets(cancellationToken);
        Dictionary<string, ObjectCatalogEntry> entries = new(standaloneVfxAssets.Count, StringComparer.OrdinalIgnoreCase);
        progress.ReportItems(0, standaloneVfxAssets.Count);
        for (var index = 0; index < standaloneVfxAssets.Count; ++index)
        {
            RuntimeVfxAsset vfxAsset = standaloneVfxAssets[index];
            cancellationToken.ThrowIfCancellationRequested();
            entries[vfxAsset.Path] = new ObjectCatalogEntry(
                ObjectCatalogKind.Vfx,
                vfxAsset.Source,
                rowId: 0,
                ObjectCatalogLabelUtility.BuildPathLabel(vfxAsset.Source, vfxAsset.Path),
                vfxAsset.Path,
                vfxAsset.Path,
                vfxInfo: new ObjectCatalogVfxInfo(vfxAsset.LoopFacts),
                additionalSearchTerms: vfxAsset.SearchTerms,
                searchProfile: ObjectCatalogSearchProfile.Vfx);
            progress.ReportItems(index + 1, standaloneVfxAssets.Count);
        }

        return MaterializeEntries(entries.Values, SourceNamePathComparer);
    }

    private void AddFurnitureEntry(
        Dictionary<string, CatalogCandidate> entries,
        string sourceName,
        int sourcePriority,
        HousingFurniture row,
        string sharedGroupPath,
        IReadOnlyDictionary<uint, HousingPileFootprint> pileFootprints)
    {
        Item? item = row.Item.ValueNullable;
        string name = BuildFurnitureLabel(item, sourceName);
        AddFurnitureEntry(
            entries,
            sourceName,
            sourcePriority,
            row.RowId,
            name,
            sharedGroupPath,
            BuildFurnitureInfo(row, item, name, pileFootprints));
    }

    private void AddFurnitureEntry(
        Dictionary<string, CatalogCandidate> entries,
        string sourceName,
        int sourcePriority,
        HousingYardObject row,
        string sharedGroupPath)
    {
        Item? item = row.Item.ValueNullable;
        string name = BuildFurnitureLabel(item, sourceName);
        AddFurnitureEntry(
            entries,
            sourceName,
            sourcePriority,
            row.RowId,
            name,
            sharedGroupPath,
            BuildFurnitureInfo(row, item, name));
    }

    private void AddFurnitureEntry(
        Dictionary<string, CatalogCandidate> entries,
        string sourceName,
        int sourcePriority,
        uint rowId,
        string name,
        string sharedGroupPath,
        ObjectCatalogFurnitureInfo furnitureInfo)
    {
        if (!IsCatalogSharedGroupPathAvailable(sharedGroupPath))
        {
            return;
        }

        SharedGroupAssetInfo sharedGroupAssets = GetSharedGroupAssetsOrEmpty(sharedGroupPath);
        AddFurnitureCatalogEntry(
            entries,
            sourceName,
            sourcePriority,
            rowId,
            name,
            sharedGroupPath,
            furnitureInfo,
            sharedGroupAssets);
    }

    private static void AddBgObjectEntry(
        Dictionary<string, CatalogCandidate> entries,
        string sourceName,
        int sourcePriority,
        uint rowId,
        string name,
        string modelPath,
        ObjectCatalogBgObjectInfo? bgObjectInfo,
        IReadOnlyList<string> searchTerms)
    {
        if (entries.TryGetValue(modelPath, out CatalogCandidate existing) && existing.Priority <= sourcePriority)
        {
            return;
        }

        entries[modelPath] = new CatalogCandidate(
            new ObjectCatalogEntry(
                ObjectCatalogKind.BgObject,
                sourceName,
                rowId,
                name,
                modelPath,
                modelPath,
                bgObjectInfo: bgObjectInfo,
                additionalSearchTerms: searchTerms),
            sourcePriority);
    }

    private static ObjectCatalogBgObjectInfo? BuildBgObjectInfo(GameDataBgObjectAsset asset)
        => ObjectCatalogBgObjectInfo.Create(asset.TerritoryIds, asset.TerritoryNames);

    private static ObjectCatalogBgObjectInfo? BuildBgObjectInfo(
        IReadOnlyList<uint> territoryIds,
        IReadOnlyList<string> territoryNames)
        => ObjectCatalogBgObjectInfo.Create(territoryIds, territoryNames);

    private static void AddFurnitureCatalogEntry(
        Dictionary<string, CatalogCandidate> entries,
        string sourceName,
        int sourcePriority,
        uint rowId,
        string name,
        string sharedGroupPath,
        ObjectCatalogFurnitureInfo furnitureInfo,
        SharedGroupAssetInfo sharedGroupAssets)
    {
        if (entries.TryGetValue(sharedGroupPath, out CatalogCandidate existing))
        {
            if (existing.Entry.FurnitureInfo is { } existingFurnitureInfo)
            {
                if (existing.Priority <= sourcePriority)
                {
                    ObjectCatalogFurnitureInfo mergedFurnitureInfo = existingFurnitureInfo.AddVariants(furnitureInfo.Variants);
                    entries[sharedGroupPath] = new CatalogCandidate(
                        CreateFurnitureCatalogEntry(
                            existing.Entry.Source,
                            existing.Entry.RowId,
                            existing.Entry.Name,
                            sharedGroupPath,
                            mergedFurnitureInfo,
                            sharedGroupAssets),
                        existing.Priority);
                    return;
                }

                furnitureInfo = furnitureInfo.AddVariants(existingFurnitureInfo.Variants);
            }
            else if (existing.Priority <= sourcePriority)
            {
                return;
            }
        }

        entries[sharedGroupPath] = new CatalogCandidate(
            CreateFurnitureCatalogEntry(
                sourceName,
                rowId,
                name,
                sharedGroupPath,
                furnitureInfo,
                sharedGroupAssets),
            sourcePriority);
    }

    private static ObjectCatalogEntry CreateFurnitureCatalogEntry(
        string sourceName,
        uint rowId,
        string name,
        string sharedGroupPath,
        ObjectCatalogFurnitureInfo furnitureInfo,
        SharedGroupAssetInfo sharedGroupAssets)
        => new(
            ObjectCatalogKind.Furniture,
            sourceName,
            rowId,
            name,
            sharedGroupPath,
            sharedGroupPath,
            furnitureInfo: furnitureInfo,
            previewModels: sharedGroupAssets.PreviewModels,
            additionalSearchTerms: BuildFurnitureSearchTerms(sharedGroupAssets));

    private bool IsCatalogSharedGroupPathAvailable(string sharedGroupPath)
        => ObjectAssetPathRules.IsCatalogSharedGroupPath(sharedGroupPath)
        && _gameData.FileExists(sharedGroupPath);

    private SharedGroupAssetInfo GetSharedGroupAssetsOrEmpty(string sharedGroupPath)
    {
        if (_assetIndex.TryGetSharedGroupAssets(sharedGroupPath, out SharedGroupAssetInfo? resolvedSharedGroups))
        {
            return resolvedSharedGroups;
        }

        return EmptySharedGroups;
    }

    private static ObjectCatalogFurnitureInfo BuildFurnitureInfo(
        HousingFurniture row,
        Item? item,
        string name,
        IReadOnlyDictionary<uint, HousingPileFootprint> pileFootprints)
        => BuildFurnitureInfo(
            item,
            row.RowId,
            name,
            row.ModelKey,
            row.DestroyOnRemoval,
            new HousingFurnitureMetadata(
                HousingFurnitureArea.Indoor,
                row.HousingItemCategory,
                row.UsageType,
                row.PlaceLimitType,
                row.AquariumTier,
                row.Placement.RowId,
                TryResolvePileFootprint(row.AquariumTier, pileFootprints)));

    private static ObjectCatalogFurnitureInfo BuildFurnitureInfo(HousingYardObject row, Item? item, string name)
        => BuildFurnitureInfo(
            item,
            row.RowId,
            name,
            row.ModelKey,
            row.DestroyOnRemoval,
            new HousingFurnitureMetadata(
                HousingFurnitureArea.Outdoor,
                row.HousingItemCategory,
                row.UsageType,
                row.PlaceLimitType,
                0,
                row.Placement.RowId,
                null));

    private static ObjectCatalogFurnitureInfo BuildFurnitureInfo(
        Item? item,
        uint housingRowId,
        string name,
        ushort modelKey,
        bool destroyOnRemoval,
        HousingFurnitureMetadata housingMetadata)
    {
        uint itemRowId = 0;
        uint iconId = 0;
        byte dyeCount = 0;
        string? category = null;
        if (item is { } resolvedItem)
        {
            itemRowId = resolvedItem.RowId;
            iconId = resolvedItem.Icon;
            dyeCount = resolvedItem.DyeCount;
            category = GetItemCategoryLabel(resolvedItem);
        }

        ObjectCatalogFurnitureVariant variant = new(
            housingRowId,
            itemRowId,
            name,
            iconId,
            dyeCount,
            housingMetadata,
            category ?? string.Empty,
            destroyOnRemoval);

        return new ObjectCatalogFurnitureInfo(modelKey, variant);
    }

    private static Dictionary<uint, HousingPileFootprint> BuildPileFootprints(IEnumerable<HousingPileLimit> rows)
    {
        Dictionary<uint, HousingPileFootprint> pileFootprints = [];
        foreach (HousingPileLimit row in rows)
        {
            pileFootprints[row.RowId] = new HousingPileFootprint(
                row.Unknown0,
                row.Unknown1,
                row.Unknown2,
                BuildPileCompatibilityMask(row.Unknown3, row.Unknown4, row.Unknown5, row.Unknown6, row.Unknown7));
        }

        return pileFootprints;
    }

    private static byte BuildPileCompatibilityMask(bool tier1, bool tier2, bool tier3, bool tier4, bool tier5)
        => BuildPileCompatibilityMask(
            tier1 ? (byte)1 : (byte)0,
            tier2 ? (byte)1 : (byte)0,
            tier3 ? (byte)1 : (byte)0,
            tier4 ? (byte)1 : (byte)0,
            tier5 ? (byte)1 : (byte)0);

    private static byte BuildPileCompatibilityMask(byte tier1, byte tier2, byte tier3, byte tier4, byte tier5)
    {
        byte mask = 0;
        AddPileCompatibilityBit(ref mask, tier1, 0);
        AddPileCompatibilityBit(ref mask, tier2, 1);
        AddPileCompatibilityBit(ref mask, tier3, 2);
        AddPileCompatibilityBit(ref mask, tier4, 3);
        AddPileCompatibilityBit(ref mask, tier5, 4);
        return mask;
    }

    private static void AddPileCompatibilityBit(ref byte mask, byte value, int bit)
    {
        if (value != 0)
        {
            mask |= (byte)(1 << bit);
        }
    }

    private static HousingPileFootprint? TryResolvePileFootprint(
        byte aquariumTier,
        IReadOnlyDictionary<uint, HousingPileFootprint> pileFootprints)
        => aquariumTier != 0 && pileFootprints.TryGetValue(aquariumTier, out HousingPileFootprint footprint)
            ? footprint
            : null;

    private static string BuildFurnitureLabel(Item? item, string fallback)
    {
        if (item is { } resolvedItem)
        {
            var name = ObjectCatalogLabelUtility.NormalizeLabel(resolvedItem.Name.ExtractText());
            if (!string.IsNullOrWhiteSpace(name))
            {
                return name;
            }
        }

        return fallback;
    }

    private static IReadOnlyList<string> BuildFurnitureSearchTerms(SharedGroupAssetInfo sharedGroupAssets)
        => SearchTermUtility.BuildStableTerms(
            sharedGroupAssets.BgObjectModelPaths,
            sharedGroupAssets.NestedSharedGroupPaths,
            sharedGroupAssets.ReferencedVfxPaths);

    private static ObjectCatalogEntry[] MaterializeEntries(
        Dictionary<string, CatalogCandidate> candidates,
        IComparer<ObjectCatalogEntry> comparer)
    {
        List<ObjectCatalogEntry> entries = new(candidates.Count);
        foreach (CatalogCandidate candidate in candidates.Values)
        {
            entries.Add(candidate.Entry);
        }

        entries.Sort(comparer);
        return entries.ToArray();
    }

    private static ObjectCatalogEntry[] MaterializeEntries(
        IEnumerable<ObjectCatalogEntry> entries,
        IComparer<ObjectCatalogEntry> comparer)
    {
        List<ObjectCatalogEntry> materializedEntries =
        [
            .. entries,
        ];
        if (materializedEntries.Count == 0)
        {
            return [];
        }

        materializedEntries.Sort(comparer);
        return materializedEntries.ToArray();
    }

    private static string? GetItemCategoryLabel(Item item)
    {
        ItemSearchCategory? searchCategory = item.ItemSearchCategory.ValueNullable;
        string? label = GetItemSearchCategoryLabel(searchCategory)
            ?? GetItemUiCategoryLabel(item.ItemUICategory.ValueNullable);
        string category = ObjectCatalogLabelUtility.NormalizeFurnitureCategoryLabel(label);
        return string.IsNullOrWhiteSpace(category)
            ? null
            : category;
    }

    private static string? GetItemSearchCategoryLabel(ItemSearchCategory? category)
        => category is { } resolvedCategory
            ? GetNonEmptyLabel(resolvedCategory.Name.ExtractText())
            : null;

    private static string? GetItemUiCategoryLabel(ItemUICategory? category)
        => category is { } resolvedCategory
            ? GetNonEmptyLabel(resolvedCategory.Name.ExtractText())
            : null;

    private static string? GetNonEmptyLabel(string text)
    {
        var label = ObjectCatalogLabelUtility.NormalizeLabel(text);
        return string.IsNullOrWhiteSpace(label)
            ? null
            : label;
    }

}

