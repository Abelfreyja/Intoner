using Dalamud.Plugin.Services;
using Intoner.Objects.Assets;
using Intoner.Objects.Models;
using Intoner.Objects.Utils;
using Intoner.Services.Loading;
using Lumina.Excel.Sheets;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace Intoner.Objects.Catalog;

internal sealed class ObjectCatalogBuilder
{
    private readonly record struct FurnitureCandidate(
        string Source,
        int Priority,
        ushort ModelKey,
        ObjectCatalogFurnitureVariant PrimaryVariant,
        List<ObjectCatalogFurnitureVariant> Variants,
        SharedGroupAssetInfo Assets);

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

    public ObjectCatalogData Build(ObjectAssetCatalogSnapshot assets, LoadProgress progress)
    {
        LoadProgressPlan progressPlan = progress.CreatePlan();
        LoadProgress bgObjectProgress = progressPlan.Next(38d);
        LoadProgress furnitureProgress = progressPlan.Next(6d);
        LoadProgress vfxProgress = progressPlan.Next(7d);
        LoadProgress indexProgress = progressPlan.Next(4d);
        LoadProgress searchProgress = progressPlan.Next(45d);
        long startedAt = Stopwatch.GetTimestamp();
        long phaseStartedAt = startedAt;
        IReadOnlyList<ObjectCatalogEntry> bgObjectEntries = BuildBgObjectEntries(assets.BgObjects, bgObjectProgress);
        TimeSpan bgObjectTime = Stopwatch.GetElapsedTime(phaseStartedAt);

        phaseStartedAt = Stopwatch.GetTimestamp();
        IReadOnlyList<ObjectCatalogEntry> furnitureEntries = BuildFurnitureEntries(furnitureProgress);
        TimeSpan furnitureTime = Stopwatch.GetElapsedTime(phaseStartedAt);

        phaseStartedAt = Stopwatch.GetTimestamp();
        IReadOnlyList<ObjectCatalogEntry> vfxEntries = BuildVfxEntries(assets.StandaloneVfx, vfxProgress);
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

    public static IReadOnlyList<ObjectCatalogEntry> BuildBgObjectEntries(IReadOnlyList<BgObjectAsset> assets, LoadProgress progress)
    {
        CancellationToken cancellationToken = progress.CancellationToken;
        List<ObjectCatalogEntry> entries = new(assets.Count);
        int completedAssetCount = 0;
        progress.ReportItems(completedAssetCount, assets.Count);
        foreach (BgObjectAsset asset in assets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            entries.Add(new ObjectCatalogEntry(
                ObjectCatalogKind.BgObject,
                asset.Source,
                asset.RowId,
                ObjectCatalogLabelUtility.BuildPathLabel(asset.Source, asset.SourcePath),
                asset.ModelPath,
                asset.ModelPath,
                bgObjectInfo: new ObjectCatalogBgObjectInfo(asset.TerritoryIds, asset.TerritoryNames)
                {
                    ColliderDefinitions = asset.ColliderDefinitions.Where(static definition => definition.Kind != BgCollisionKind.None).ToArray(),
                    ColliderDefinitionsComplete = asset.ColliderDefinitionsComplete,
                },
                additionalSearchTerms: asset.SearchTerms));
            progress.ReportItems(++completedAssetCount, assets.Count);
        }

        entries.Sort(NameSourcePathComparer);
        return entries.ToArray();
    }

    public IReadOnlyList<ObjectCatalogEntry> BuildFurnitureEntries(LoadProgress progress)
    {
        CancellationToken cancellationToken = progress.CancellationToken;
        var housingFurniture = _gameData.GetExcelSheet<HousingFurniture>()!;
        var housingYardObjects = _gameData.GetExcelSheet<HousingYardObject>()!;
        Dictionary<uint, HousingPileFootprint> pileFootprints = BuildPileFootprints(_gameData.GetExcelSheet<HousingPileLimit>()!);
        Dictionary<string, FurnitureCandidate> entries = new(StringComparer.OrdinalIgnoreCase);
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

        return MaterializeFurnitureEntries(entries);
    }

    public static IReadOnlyList<ObjectCatalogEntry> BuildVfxEntries(IReadOnlyList<RuntimeVfxAsset> standaloneVfxAssets, LoadProgress progress)
    {
        CancellationToken cancellationToken = progress.CancellationToken;
        List<ObjectCatalogEntry> entries = new(standaloneVfxAssets.Count);
        progress.ReportItems(0, standaloneVfxAssets.Count);
        for (var index = 0; index < standaloneVfxAssets.Count; ++index)
        {
            RuntimeVfxAsset vfxAsset = standaloneVfxAssets[index];
            cancellationToken.ThrowIfCancellationRequested();
            entries.Add(new ObjectCatalogEntry(
                ObjectCatalogKind.Vfx,
                vfxAsset.Source,
                rowId: 0,
                ObjectCatalogLabelUtility.BuildPathLabel(vfxAsset.Source, vfxAsset.Path),
                vfxAsset.Path,
                vfxAsset.Path,
                vfxInfo: new ObjectCatalogVfxInfo(vfxAsset.LoopFacts),
                additionalSearchTerms: vfxAsset.SearchTerms,
                searchProfile: ObjectCatalogSearchProfile.Vfx));
            progress.ReportItems(index + 1, standaloneVfxAssets.Count);
        }

        entries.Sort(SourceNamePathComparer);
        return entries.ToArray();
    }

    private void AddFurnitureEntry(
        Dictionary<string, FurnitureCandidate> entries,
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
            row.ModelKey,
            sharedGroupPath,
            BuildFurnitureVariant(row, item, name, pileFootprints));
    }

    private void AddFurnitureEntry(
        Dictionary<string, FurnitureCandidate> entries,
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
            row.ModelKey,
            sharedGroupPath,
            BuildFurnitureVariant(row, item, name));
    }

    private void AddFurnitureEntry(
        Dictionary<string, FurnitureCandidate> entries,
        string sourceName,
        int sourcePriority,
        ushort modelKey,
        string sharedGroupPath,
        ObjectCatalogFurnitureVariant variant)
    {
        if (!_assetIndex.TryGetSharedGroupAssets(sharedGroupPath, out SharedGroupAssetInfo? sharedGroupAssets))
        {
            return;
        }

        if (entries.TryGetValue(sharedGroupPath, out FurnitureCandidate existing))
        {
            if (existing.Priority <= sourcePriority)
            {
                existing.Variants.Add(variant);
                entries[sharedGroupPath] = existing with { Assets = sharedGroupAssets };
                return;
            }

            existing.Variants.Insert(0, variant);
            entries[sharedGroupPath] = new(sourceName, sourcePriority, modelKey, variant, existing.Variants, sharedGroupAssets);
            return;
        }

        entries[sharedGroupPath] = new(sourceName, sourcePriority, modelKey, variant, [variant], sharedGroupAssets);
    }

    private static ObjectCatalogFurnitureVariant BuildFurnitureVariant(
        HousingFurniture row,
        Item? item,
        string name,
        IReadOnlyDictionary<uint, HousingPileFootprint> pileFootprints)
        => BuildFurnitureVariant(
            item,
            row.RowId,
            name,
            row.DestroyOnRemoval,
            new HousingFurnitureMetadata(
                HousingFurnitureArea.Indoor,
                row.HousingItemCategory,
                row.UsageType,
                row.PlaceLimitType,
                row.AquariumTier,
                row.Placement.RowId,
                TryResolvePileFootprint(row.AquariumTier, pileFootprints)));

    private static ObjectCatalogFurnitureVariant BuildFurnitureVariant(HousingYardObject row, Item? item, string name)
        => BuildFurnitureVariant(
            item,
            row.RowId,
            name,
            row.DestroyOnRemoval,
            new HousingFurnitureMetadata(
                HousingFurnitureArea.Outdoor,
                row.HousingItemCategory,
                row.UsageType,
                row.PlaceLimitType,
                0,
                row.Placement.RowId,
                null));

    private static ObjectCatalogFurnitureVariant BuildFurnitureVariant(
        Item? item,
        uint housingRowId,
        string name,
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

        return new ObjectCatalogFurnitureVariant(
            housingRowId,
            itemRowId,
            name,
            iconId,
            dyeCount,
            housingMetadata,
            category ?? string.Empty,
            destroyOnRemoval);
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

    private static ObjectCatalogEntry[] MaterializeFurnitureEntries(Dictionary<string, FurnitureCandidate> candidates)
    {
        List<ObjectCatalogEntry> entries = new(candidates.Count);
        foreach ((string path, FurnitureCandidate candidate) in candidates)
        {
            entries.Add(new ObjectCatalogEntry(
                ObjectCatalogKind.Furniture,
                candidate.Source,
                candidate.PrimaryVariant.HousingRowId,
                candidate.PrimaryVariant.Name,
                path,
                path,
                furnitureInfo: new ObjectCatalogFurnitureInfo(candidate.ModelKey, candidate.PrimaryVariant, candidate.Variants),
                previewModels: candidate.Assets.PreviewModels,
                additionalSearchTerms: ObjectCatalogEntry.BuildFurnitureSearchTerms(candidate.Assets),
                previewModelDiscoveryComplete: candidate.Assets.IsComplete));
        }

        entries.Sort(NameSourcePathComparer);
        return entries.ToArray();
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
