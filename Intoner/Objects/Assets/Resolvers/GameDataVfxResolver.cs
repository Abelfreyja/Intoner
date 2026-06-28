using Intoner.Objects.Utils;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using Microsoft.Extensions.Logging;
using LuminaAction = Lumina.Excel.Sheets.Action;

namespace Intoner.Objects.Assets;

internal sealed class GameDataVfxResolver
{
    private const string ExportedSharedGroupSource = "exported shared group";
    private const string HousingExteriorSource = "housing exterior";
    private const string HousingInteriorSource = "housing interior";
    private const string TerritoryZoneSharedGroupSource = "territory zone shared group";
    private const string TerritoryLayoutSource = "territory layout";
    private const string StatusEffectSource = "status effect";
    private const string ActionSource = "action effect";
    private const string ActionTimelineSource = "action timeline";
    private const string EmoteTimelineSource = "emote timeline";
    private const string GimmickTimelineSource = "gimmick timeline";

    private readonly ILogger<GameDataVfxResolver> _logger;
    private readonly IObjectAssetGameData _gameData;

    public GameDataVfxResolver(ILogger<GameDataVfxResolver> logger, IObjectAssetGameData gameData)
    {
        _logger = logger;
        _gameData = gameData;
    }

    public IReadOnlyList<ResolvedVfxPath> Resolve(SqpackIndexSnapshot sqpackIndexSnapshot)
    {
        Dictionary<string, ResolvedVfxPathAccumulator> resolvedPaths = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, SharedGroupAssetInfo> sharedGroupCache = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, TerritoryLayoutAssetInfo> territoryLayoutCache = new(StringComparer.OrdinalIgnoreCase);
        VfxTimelineReferenceCache timelineReferenceCache = new(_gameData);
        ExcelSheet<TerritoryType>? territorySheet = _gameData.GetCurrentLanguageExcelSheet<TerritoryType>();
        ExcelSheet<PlaceName>? placeNameSheet = _gameData.GetCurrentLanguageExcelSheet<PlaceName>();

        CollectSharedGroupRows(
            resolvedPaths,
            sqpackIndexSnapshot,
            sharedGroupCache,
            _gameData.GetExcelSheet<ExportedSG>(),
            ExportedSharedGroupSource,
            static row => row.RowId,
            static row => ObjectPathRules.NormalizeGamePath(row.SgbPath.ToString()));
        CollectAssetPathRows(
            resolvedPaths,
            sqpackIndexSnapshot,
            sharedGroupCache,
            _gameData.GetExcelSheet<HousingExterior>(),
            HousingExteriorSource,
            static row => row.RowId,
            static row => ObjectPathRules.NormalizeGamePath(row.Model.ExtractText()));
        CollectHousingInteriorVfx(
            resolvedPaths,
            sqpackIndexSnapshot,
            sharedGroupCache,
            _gameData.GetExcelSheet<HousingInterior>());
        CollectTerritoryZoneSharedGroupVfx(
            resolvedPaths,
            sqpackIndexSnapshot,
            sharedGroupCache,
            territorySheet,
            placeNameSheet);
        CollectTerritoryLayoutVfx(
            resolvedPaths,
            territoryLayoutCache,
            territorySheet,
            placeNameSheet);
        CollectStatusVfx(resolvedPaths, sqpackIndexSnapshot);
        CollectActionVfx(resolvedPaths, sqpackIndexSnapshot, timelineReferenceCache);
        CollectEmoteTimelineVfx(resolvedPaths, sqpackIndexSnapshot, timelineReferenceCache);
        CollectGimmickTimelineVfx(resolvedPaths, sqpackIndexSnapshot, timelineReferenceCache);

        IReadOnlyList<ResolvedVfxPath> snapshot = ResolvedVfxPathAccumulator.BuildSnapshot(resolvedPaths.Values);
        _logger.LogInformation("resolved {VfxCount} static vfx paths from game data sources", snapshot.Count);
        return snapshot;
    }

    private void CollectAssetPathRows<T>(
        IDictionary<string, ResolvedVfxPathAccumulator> resolvedPaths,
        SqpackIndexSnapshot sqpackIndexSnapshot,
        IDictionary<string, SharedGroupAssetInfo> sharedGroupCache,
        IEnumerable<T>? rows,
        string source,
        Func<T, uint> rowIdSelector,
        Func<T, string> pathSelector)
    {
        if (rows is null)
        {
            return;
        }

        foreach (T row in rows)
        {
            TryCollectGameDataAssetPath(
                resolvedPaths,
                sqpackIndexSnapshot,
                sharedGroupCache,
                rowIdSelector(row),
                source,
                pathSelector(row),
                ObjectTerritoryMetadata.Empty);
        }
    }

    private void CollectSharedGroupRows<T>(
        IDictionary<string, ResolvedVfxPathAccumulator> resolvedPaths,
        SqpackIndexSnapshot sqpackIndexSnapshot,
        IDictionary<string, SharedGroupAssetInfo> sharedGroupCache,
        IEnumerable<T>? rows,
        string source,
        Func<T, uint> rowIdSelector,
        Func<T, string> pathSelector)
    {
        if (rows is null)
        {
            return;
        }

        foreach (T row in rows)
        {
            TryCollectSharedGroupVfx(
                resolvedPaths,
                sqpackIndexSnapshot,
                sharedGroupCache,
                rowIdSelector(row),
                source,
                pathSelector(row),
                ObjectTerritoryMetadata.Empty);
        }
    }

    private void CollectHousingInteriorVfx(
        IDictionary<string, ResolvedVfxPathAccumulator> resolvedPaths,
        SqpackIndexSnapshot sqpackIndexSnapshot,
        IDictionary<string, SharedGroupAssetInfo> sharedGroupCache,
        IEnumerable<HousingInterior>? rows)
    {
        if (rows is null)
        {
            return;
        }

        foreach (HousingInterior row in rows)
        {
            if (!GameDataAssetPathUtility.TryBuildHousingInteriorSourcePath(row, out string sourcePath))
            {
                continue;
            }

            TryCollectGameDataAssetPath(
                resolvedPaths,
                sqpackIndexSnapshot,
                sharedGroupCache,
                row.RowId,
                HousingInteriorSource,
                sourcePath,
                ObjectTerritoryMetadata.Empty);
        }
    }

    private void CollectTerritoryZoneSharedGroupVfx(
        IDictionary<string, ResolvedVfxPathAccumulator> resolvedPaths,
        SqpackIndexSnapshot sqpackIndexSnapshot,
        IDictionary<string, SharedGroupAssetInfo> sharedGroupCache,
        IEnumerable<TerritoryType>? territories,
        ExcelSheet<PlaceName>? placeNames)
    {
        if (territories is null)
        {
            return;
        }

        foreach (TerritoryType territory in territories)
        {
            if (!territory.IsInUse)
            {
                continue;
            }

            var zoneSharedGroups = territory.ZoneSharedGroup.ValueNullable;
            if (zoneSharedGroups is null)
            {
                continue;
            }

            ObjectTerritoryMetadata territoryMetadata = ObjectTerritoryMetadataUtility.BuildFromTerritory(territory, placeNames);
            foreach (ZoneSharedGroup zoneSharedGroup in zoneSharedGroups)
            {
                string sharedGroupPath = ObjectPathRules.NormalizeGamePath(zoneSharedGroup.LGBSharedGroup.ToString());
                TryCollectSharedGroupVfx(
                    resolvedPaths,
                    sqpackIndexSnapshot,
                    sharedGroupCache,
                    territory.RowId,
                    TerritoryZoneSharedGroupSource,
                    sharedGroupPath,
                    territoryMetadata);
            }
        }
    }

    private void CollectTerritoryLayoutVfx(
        IDictionary<string, ResolvedVfxPathAccumulator> resolvedPaths,
        IDictionary<string, TerritoryLayoutAssetInfo> territoryLayoutCache,
        IEnumerable<TerritoryType>? territories,
        ExcelSheet<PlaceName>? placeNames)
    {
        if (territories is null)
        {
            return;
        }

        foreach (TerritoryType territory in territories)
        {
            if (!territory.IsInUse)
            {
                continue;
            }

            string territoryLayoutPath = GameDataAssetPathUtility.BuildTerritoryLayoutPath(territory.Bg.ExtractText());
            ObjectTerritoryMetadata territoryMetadata = ObjectTerritoryMetadataUtility.BuildFromTerritory(territory, placeNames);
            TryCollectTerritoryLayoutVfx(
                resolvedPaths,
                territoryLayoutCache,
                territory.RowId,
                territoryLayoutPath,
                territoryMetadata);
        }
    }

    private void CollectStatusVfx(
        IDictionary<string, ResolvedVfxPathAccumulator> resolvedPaths,
        SqpackIndexSnapshot sqpackIndexSnapshot)
    {
        ExcelSheet<Status>? sheet = _gameData.GetCurrentLanguageExcelSheet<Status>();
        if (sheet is null)
        {
            return;
        }

        foreach (Status row in sheet)
        {
            string rowName = row.Name.ExtractText();
            IReadOnlyList<string> rowSearchTerms = BuildSearchTerms(StatusEffectSource, row.RowId.ToString(), rowName);

            if (GameDataAssetPathUtility.TryBuildCommonEffectVfxPath(
                row.HitEffect.ValueNullable?.Location.ValueNullable?.Location.ExtractText() ?? string.Empty,
                out string hitPath))
            {
                _ = VfxResolvedPathUtility.TryMergeResolvedPath(
                    resolvedPaths,
                    _gameData,
                    hitPath,
                    sqpackIndexSnapshot,
                    KnownVfxFamily.None,
                    RuntimeVfxEvidence.Status,
                    AssetPathSource.GameData,
                    AssetPathContract.SheetConvention,
                    rowSearchTerms);
            }

            var loopVfxs = row.VFX.ValueNullable?.VFX;
            if (loopVfxs is null)
            {
                continue;
            }

            foreach (var loopVfx in loopVfxs)
            {
                if (!GameDataAssetPathUtility.TryBuildCommonEffectVfxPath(
                    loopVfx.ValueNullable?.Location.ExtractText() ?? string.Empty,
                    out string loopPath))
                {
                    continue;
                }

                _ = VfxResolvedPathUtility.TryMergeResolvedPath(
                    resolvedPaths,
                    _gameData,
                    loopPath,
                    sqpackIndexSnapshot,
                    KnownVfxFamily.None,
                    RuntimeVfxEvidence.Status,
                    AssetPathSource.GameData,
                    AssetPathContract.SheetConvention,
                    rowSearchTerms);
            }
        }
    }

    private void CollectActionVfx(
        IDictionary<string, ResolvedVfxPathAccumulator> resolvedPaths,
        SqpackIndexSnapshot sqpackIndexSnapshot,
        VfxTimelineReferenceCache timelineReferenceCache)
    {
        ExcelSheet<LuminaAction>? sheet = _gameData.GetCurrentLanguageExcelSheet<LuminaAction>();
        if (sheet is null)
        {
            return;
        }

        foreach (LuminaAction row in sheet)
        {
            string rowName = row.Name.ExtractText();
            IReadOnlyList<string> rowSearchTerms = BuildSearchTerms(ActionSource, row.RowId.ToString(), rowName);

            if (GameDataAssetPathUtility.TryBuildCommonEffectVfxPath(
                row.VFX.ValueNullable?.VFX.ValueNullable?.Location.ExtractText() ?? string.Empty,
                out string castVfxPath))
            {
                _ = VfxResolvedPathUtility.TryMergeResolvedPath(
                    resolvedPaths,
                    _gameData,
                    castVfxPath,
                    sqpackIndexSnapshot,
                    KnownVfxFamily.None,
                    RuntimeVfxEvidence.Action,
                    AssetPathSource.GameData,
                    AssetPathContract.SheetConvention,
                    rowSearchTerms);
            }

            if (GameDataAssetPathUtility.TryBuildCommonEffectVfxPath(
                row.AnimationStart.ValueNullable?.VFX.ValueNullable?.Location.ExtractText() ?? string.Empty,
                out string startVfxPath))
            {
                _ = VfxResolvedPathUtility.TryMergeResolvedPath(
                    resolvedPaths,
                    _gameData,
                    startVfxPath,
                    sqpackIndexSnapshot,
                    KnownVfxFamily.None,
                    RuntimeVfxEvidence.Action,
                    AssetPathSource.GameData,
                    AssetPathContract.SheetConvention,
                    rowSearchTerms);
            }

            CollectTimelineReferences(
                resolvedPaths,
                sqpackIndexSnapshot,
                RuntimeVfxEvidence.ActionTimeline,
                AssetPathSource.GameData,
                [.. rowSearchTerms, ActionTimelineSource],
                row.AnimationStart.ValueNullable?.Name.ValueNullable?.Key.ToString() ?? string.Empty,
                timelineReferenceCache);
            CollectTimelineReferences(
                resolvedPaths,
                sqpackIndexSnapshot,
                RuntimeVfxEvidence.ActionTimeline,
                AssetPathSource.GameData,
                [.. rowSearchTerms, ActionTimelineSource],
                row.AnimationEnd.ValueNullable?.Key.ToString() ?? string.Empty,
                timelineReferenceCache);
            CollectTimelineReferences(
                resolvedPaths,
                sqpackIndexSnapshot,
                RuntimeVfxEvidence.ActionTimeline,
                AssetPathSource.GameData,
                [.. rowSearchTerms, ActionTimelineSource],
                row.ActionTimelineHit.ValueNullable?.Key.ToString() ?? string.Empty,
                timelineReferenceCache);
            CollectTimelineReferences(
                resolvedPaths,
                sqpackIndexSnapshot,
                RuntimeVfxEvidence.ActionTimeline,
                AssetPathSource.GameData,
                [.. rowSearchTerms, ActionTimelineSource],
                row.AnimationEnd.ValueNullable?.WeaponTimeline.ValueNullable?.File.ToString() ?? string.Empty,
                timelineReferenceCache);
        }
    }

    private void CollectEmoteTimelineVfx(
        IDictionary<string, ResolvedVfxPathAccumulator> resolvedPaths,
        SqpackIndexSnapshot sqpackIndexSnapshot,
        VfxTimelineReferenceCache timelineReferenceCache)
    {
        ExcelSheet<Emote>? sheet = _gameData.GetCurrentLanguageExcelSheet<Emote>();
        if (sheet is null)
        {
            return;
        }

        foreach (Emote row in sheet)
        {
            string rowName = row.Name.ExtractText();
            IReadOnlyList<string> rowSearchTerms = BuildSearchTerms(EmoteTimelineSource, row.RowId.ToString(), rowName);

            foreach (var timeline in row.ActionTimeline)
            {
                CollectTimelineReferences(
                    resolvedPaths,
                    sqpackIndexSnapshot,
                    RuntimeVfxEvidence.EmoteTimeline,
                    AssetPathSource.GameData,
                    rowSearchTerms,
                    timeline.ValueNullable?.Key.ToString() ?? string.Empty,
                    timelineReferenceCache);
            }
        }
    }

    private void CollectGimmickTimelineVfx(
        IDictionary<string, ResolvedVfxPathAccumulator> resolvedPaths,
        SqpackIndexSnapshot sqpackIndexSnapshot,
        VfxTimelineReferenceCache timelineReferenceCache)
    {
        ExcelSheet<ActionTimeline>? sheet = _gameData.GetCurrentLanguageExcelSheet<ActionTimeline>();
        if (sheet is null)
        {
            return;
        }

        foreach (ActionTimeline row in sheet)
        {
            string key = row.Key.ToString();
            if (string.IsNullOrWhiteSpace(key)
             || !key.Contains("gimmick", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            CollectTimelineReferences(
                resolvedPaths,
                sqpackIndexSnapshot,
                RuntimeVfxEvidence.GimmickTimeline,
                AssetPathSource.GameData,
                BuildSearchTerms(GimmickTimelineSource, row.RowId.ToString(), key),
                key,
                timelineReferenceCache);
        }
    }

    private void TryCollectGameDataAssetPath(
        IDictionary<string, ResolvedVfxPathAccumulator> resolvedPaths,
        SqpackIndexSnapshot sqpackIndexSnapshot,
        IDictionary<string, SharedGroupAssetInfo> sharedGroupCache,
        uint rowId,
        string source,
        string sourcePath,
        in ObjectTerritoryMetadata territoryMetadata)
    {
        if (ObjectPathRules.IsCatalogSharedGroupPath(sourcePath))
        {
            TryCollectSharedGroupVfx(
                resolvedPaths,
                sqpackIndexSnapshot,
                sharedGroupCache,
                rowId,
                source,
                sourcePath,
                territoryMetadata);
        }
    }

    private void TryCollectSharedGroupVfx(
        IDictionary<string, ResolvedVfxPathAccumulator> resolvedPaths,
        SqpackIndexSnapshot sqpackIndexSnapshot,
        IDictionary<string, SharedGroupAssetInfo> sharedGroupCache,
        uint rowId,
        string source,
        string sharedGroupPath,
        in ObjectTerritoryMetadata territoryMetadata)
    {
        if (!ObjectPathRules.IsCatalogSharedGroupPath(sharedGroupPath)
         || !_gameData.FileExists(sharedGroupPath))
        {
            return;
        }

        if (!sharedGroupCache.TryGetValue(sharedGroupPath, out SharedGroupAssetInfo? sharedGroupAssets))
        {
            sharedGroupAssets = SharedGroupAssetResolver.AnalyzeSharedGroup(_gameData, sharedGroupPath);
            sharedGroupCache.Add(sharedGroupPath, sharedGroupAssets);
        }

        IReadOnlyList<string> searchTerms = BuildSearchTerms(
            source,
            rowId.ToString(),
            sharedGroupPath,
            territoryMetadata.SearchTerms,
            sharedGroupAssets.NestedSharedGroupPaths);
        foreach (string vfxPath in sharedGroupAssets.StandaloneVfxPaths)
        {
            _ = VfxResolvedPathUtility.TryMergeResolvedPath(
                resolvedPaths,
                _gameData,
                vfxPath,
                sqpackIndexSnapshot,
                KnownVfxFamily.None,
                RuntimeVfxEvidence.LayoutAutoplay,
                AssetPathSource.SharedGroup,
                AssetPathContract.ParsedFileReference,
                searchTerms);
        }
    }

    private void TryCollectTerritoryLayoutVfx(
        IDictionary<string, ResolvedVfxPathAccumulator> resolvedPaths,
        IDictionary<string, TerritoryLayoutAssetInfo> territoryLayoutCache,
        uint rowId,
        string territoryLayoutPath,
        in ObjectTerritoryMetadata territoryMetadata)
    {
        if (string.IsNullOrWhiteSpace(territoryLayoutPath)
         || !_gameData.FileExists(territoryLayoutPath))
        {
            return;
        }

        if (!territoryLayoutCache.TryGetValue(territoryLayoutPath, out TerritoryLayoutAssetInfo? territoryLayoutAssets))
        {
            territoryLayoutAssets = TerritoryLayoutAssetResolver.AnalyzeTerritoryLayout(_gameData, territoryLayoutPath);
            territoryLayoutCache.Add(territoryLayoutPath, territoryLayoutAssets);
        }

        IReadOnlyList<string> searchTerms = BuildSearchTerms(
            TerritoryLayoutSource,
            rowId.ToString(),
            territoryLayoutPath,
            territoryMetadata.SearchTerms,
            territoryLayoutAssets.ReferencedLayoutPaths,
            territoryLayoutAssets.ReferencedSharedGroupPaths);
        foreach (ResolvedVfxPath resolvedVfxPath in territoryLayoutAssets.ResolvedVfxPaths)
        {
            _ = VfxResolvedPathUtility.TryMergeResolvedPath(
                resolvedPaths,
                _gameData,
                resolvedVfxPath,
                extraSearchTerms: searchTerms);
        }
    }

    private void CollectTimelineReferences(
        IDictionary<string, ResolvedVfxPathAccumulator> resolvedPaths,
        SqpackIndexSnapshot sqpackIndexSnapshot,
        RuntimeVfxEvidence sourceEvidence,
        AssetPathSource recoverySource,
        IReadOnlyList<string> baseSearchTerms,
        string timelineKey,
        VfxTimelineReferenceCache timelineReferenceCache)
    {
        if (!GameDataAssetPathUtility.TryBuildActionTimelinePath(timelineKey, out string timelinePath))
        {
            return;
        }

        string normalizedTimelinePath = ObjectPathRules.NormalizeGamePath(timelinePath);
        if (!ObjectPathRules.IsCatalogTimelinePath(normalizedTimelinePath)
         || !_gameData.FileExists(normalizedTimelinePath))
        {
            return;
        }

        IReadOnlyList<string> searchTerms = ObjectSearchTermUtility.MergeTerms(baseSearchTerms, [normalizedTimelinePath]);
        foreach (TmbVfxReference reference in timelineReferenceCache.Get(normalizedTimelinePath))
        {
            _ = VfxResolvedPathUtility.TryMergeResolvedPath(
                resolvedPaths,
                _gameData,
                reference.Path,
                sqpackIndexSnapshot,
                KnownVfxFamily.None,
                sourceEvidence | reference.Evidence,
                recoverySource,
                AssetPathContract.ParsedFileReference,
                ObjectSearchTermUtility.MergeTerms(searchTerms, reference.SearchTerms));
        }
    }

    private static IReadOnlyList<string> BuildSearchTerms(
        string source,
        string rowId,
        string sourcePath,
        params IReadOnlyList<string>?[] relatedTerms)
        => ObjectSearchTermUtility.BuildStableTerms([source, rowId, sourcePath], relatedTerms);
}

