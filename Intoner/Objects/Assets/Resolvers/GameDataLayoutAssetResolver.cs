using Intoner.Objects.Utils;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using Microsoft.Extensions.Logging;

namespace Intoner.Objects.Assets;

internal sealed class GameDataLayoutAssetResolver
{
    private readonly ILogger<GameDataLayoutAssetResolver> _logger;
    private readonly IObjectAssetGameData _gameData;
    private readonly Lock _loadLock = new();

    private Snapshot? _snapshot;

    public GameDataLayoutAssetResolver(ILogger<GameDataLayoutAssetResolver> logger, IObjectAssetGameData gameData)
    {
        _logger   = logger;
        _gameData = gameData;
    }

    public Snapshot Resolve(CancellationToken cancellationToken = default)
    {
        lock (_loadLock)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_snapshot is not null)
            {
                return _snapshot;
            }

            ExcelSheet<TerritoryType>? territorySheet = _gameData.GetCurrentLanguageExcelSheet<TerritoryType>();
            ExcelSheet<PlaceName>? placeNameSheet = _gameData.GetCurrentLanguageExcelSheet<PlaceName>();

            _snapshot = new Collector(_gameData).Collect(
                _gameData.GetExcelSheet<ExportedSG>(),
                _gameData.GetExcelSheet<HousingExterior>(),
                _gameData.GetExcelSheet<HousingInterior>(),
                territorySheet,
                placeNameSheet,
                cancellationToken);

            _logger.LogInformation(
                "resolved {BgObjectCount} bgobject models and {VfxCount} vfx paths from shared game data layout sources",
                _snapshot.BgObjects.Count,
                _snapshot.ResolvedVfxPaths.Count);

            return _snapshot;
        }
    }

    internal sealed record Snapshot(
        IReadOnlyList<GameDataBgObjectAsset> BgObjects,
        IReadOnlyList<ResolvedVfxPath> ResolvedVfxPaths);

    private sealed class Collector(IObjectAssetGameData gameData)
    {
        private const string ExportedSharedGroupSource = "exported shared group";
        private const string HousingExteriorSource = "housing exterior";
        private const string HousingInteriorSource = "housing interior";
        private const string TerritoryZoneSharedGroupSource = "territory zone shared group";
        private const string TerritoryLayoutSource = "territory layout";

        private readonly IObjectAssetGameData _gameData = gameData;
        private readonly Dictionary<string, BgObjectDiscoveryState> _bgObjects = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, ResolvedVfxPathAccumulator> _resolvedVfxPaths = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, SharedGroupAssetInfo> _sharedGroupCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, TerritoryLayoutAssetResolver.AssetInfo> _territoryLayoutCache = new(StringComparer.OrdinalIgnoreCase);

        public Snapshot Collect(
            IEnumerable<ExportedSG>? exportedSharedGroups,
            IEnumerable<HousingExterior>? housingExteriors,
            IEnumerable<HousingInterior>? housingInteriors,
            IEnumerable<TerritoryType>? territories,
            ExcelSheet<PlaceName>? placeNames,
            CancellationToken cancellationToken)
        {
            CollectSharedGroupRows(
                exportedSharedGroups,
                ExportedSharedGroupSource,
                static row => row.RowId,
                static row => ObjectPathRules.NormalizeGamePath(row.SgbPath.ToString()),
                cancellationToken);
            CollectAssetPathRows(
                housingExteriors,
                HousingExteriorSource,
                static row => row.RowId,
                static row => ObjectPathRules.NormalizeGamePath(row.Model.ExtractText()),
                cancellationToken);
            CollectHousingInteriorAssets(housingInteriors, cancellationToken);
            CollectTerritoryZoneSharedGroupAssets(territories, placeNames, cancellationToken);
            CollectTerritoryLayoutAssets(territories, placeNames, cancellationToken);

            return new Snapshot(
                _bgObjects.Values
                    .Select(static state => state.ToAsset())
                    .OrderBy(static asset => asset.ModelPath, StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                ResolvedVfxPathAccumulator.BuildSnapshot(_resolvedVfxPaths.Values));
        }

        private void CollectAssetPathRows<T>(
            IEnumerable<T>? rows,
            string source,
            Func<T, uint> rowIdSelector,
            Func<T, string> pathSelector,
            CancellationToken cancellationToken)
        {
            if (rows is null)
            {
                return;
            }

            foreach (T row in rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                TryCollectGameDataAssetPath(
                    rowIdSelector(row),
                    source,
                    pathSelector(row),
                    ObjectTerritoryMetadata.Empty);
            }
        }

        private void CollectSharedGroupRows<T>(
            IEnumerable<T>? rows,
            string source,
            Func<T, uint> rowIdSelector,
            Func<T, string> pathSelector,
            CancellationToken cancellationToken)
        {
            if (rows is null)
            {
                return;
            }

            foreach (T row in rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                TryCollectSharedGroupAssets(
                    rowIdSelector(row),
                    source,
                    pathSelector(row),
                    ObjectTerritoryMetadata.Empty);
            }
        }

        private void CollectHousingInteriorAssets(
            IEnumerable<HousingInterior>? rows,
            CancellationToken cancellationToken)
        {
            if (rows is null)
            {
                return;
            }

            foreach (HousingInterior row in rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!GameDataAssetPathUtility.TryBuildHousingInteriorSourcePath(row, out string sourcePath))
                {
                    continue;
                }

                TryCollectGameDataAssetPath(
                    row.RowId,
                    HousingInteriorSource,
                    sourcePath,
                    ObjectTerritoryMetadata.Empty);
            }
        }

        private void CollectTerritoryZoneSharedGroupAssets(
            IEnumerable<TerritoryType>? territories,
            ExcelSheet<PlaceName>? placeNames,
            CancellationToken cancellationToken)
        {
            if (territories is null)
            {
                return;
            }

            foreach (TerritoryType territory in territories)
            {
                cancellationToken.ThrowIfCancellationRequested();
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
                    TryCollectSharedGroupAssets(
                        territory.RowId,
                        TerritoryZoneSharedGroupSource,
                        ObjectPathRules.NormalizeGamePath(zoneSharedGroup.LGBSharedGroup.ToString()),
                        territoryMetadata);
                }
            }
        }

        private void CollectTerritoryLayoutAssets(
            IEnumerable<TerritoryType>? territories,
            ExcelSheet<PlaceName>? placeNames,
            CancellationToken cancellationToken)
        {
            if (territories is null)
            {
                return;
            }

            foreach (TerritoryType territory in territories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!territory.IsInUse)
                {
                    continue;
                }

                TryCollectTerritoryLayoutAssets(
                    territory.RowId,
                    GameDataAssetPathUtility.BuildTerritoryLayoutPath(territory.Bg.ExtractText()),
                    ObjectTerritoryMetadataUtility.BuildFromTerritory(territory, placeNames));
            }
        }

        private void TryCollectGameDataAssetPath(
            uint rowId,
            string source,
            string sourcePath,
            in ObjectTerritoryMetadata territoryMetadata)
        {
            if (ObjectPathRules.IsCatalogSharedGroupPath(sourcePath))
            {
                TryCollectSharedGroupAssets(rowId, source, sourcePath, territoryMetadata);
                return;
            }

            if (!ObjectPathRules.IsCatalogModelPath(sourcePath)
             || !_gameData.FileExists(sourcePath))
            {
                return;
            }

            MergeDirectModelAsset(
                rowId,
                source,
                sourcePath,
                sourcePath,
                ObjectSearchTermUtility.BuildStableTerms([source, sourcePath], territoryMetadata.SearchTerms),
                territoryMetadata);
        }

        private void TryCollectSharedGroupAssets(
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

            if (!_sharedGroupCache.TryGetValue(sharedGroupPath, out SharedGroupAssetInfo? sharedGroupAssets))
            {
                sharedGroupAssets = SharedGroupAssetResolver.AnalyzeSharedGroup(_gameData, sharedGroupPath);
                _sharedGroupCache.Add(sharedGroupPath, sharedGroupAssets);
            }

            IReadOnlyList<string> bgSearchTerms = ObjectSearchTermUtility.BuildStableTerms(
                [source, sharedGroupPath],
                territoryMetadata.SearchTerms,
                sharedGroupAssets.NestedSharedGroupPaths);
            foreach (string modelPath in sharedGroupAssets.BgObjectModelPaths)
            {
                MergeDirectModelAsset(
                    rowId,
                    source,
                    sharedGroupPath,
                    modelPath,
                    bgSearchTerms,
                    territoryMetadata);
            }

            IReadOnlyList<string> vfxSearchTerms = BuildSearchTerms(
                source,
                rowId.ToString(),
                sharedGroupPath,
                territoryMetadata.SearchTerms,
                sharedGroupAssets.NestedSharedGroupPaths);
            foreach (string vfxPath in sharedGroupAssets.StandaloneVfxPaths)
            {
                _ = VfxResolvedPathUtility.TryMergeResolvedPath(
                    _resolvedVfxPaths,
                    _gameData,
                    vfxPath,
                    sqpackIndexSnapshot: null,
                    KnownVfxFamily.None,
                    RuntimeVfxEvidence.LayoutAutoplay,
                    AssetPathSource.SharedGroup,
                    AssetPathContract.ParsedFileReference,
                    vfxSearchTerms);
            }
        }

        private void TryCollectTerritoryLayoutAssets(
            uint rowId,
            string territoryLayoutPath,
            in ObjectTerritoryMetadata territoryMetadata)
        {
            if (string.IsNullOrWhiteSpace(territoryLayoutPath)
             || !_gameData.FileExists(territoryLayoutPath))
            {
                return;
            }

            if (!_territoryLayoutCache.TryGetValue(territoryLayoutPath, out TerritoryLayoutAssetResolver.AssetInfo? territoryLayoutAssets))
            {
                territoryLayoutAssets = TerritoryLayoutAssetResolver.AnalyzeTerritoryLayout(_gameData, territoryLayoutPath);
                _territoryLayoutCache.Add(territoryLayoutPath, territoryLayoutAssets);
            }

            IReadOnlyList<string> bgSearchTerms = ObjectSearchTermUtility.BuildStableTerms(
                [TerritoryLayoutSource, territoryLayoutPath],
                territoryMetadata.SearchTerms,
                territoryLayoutAssets.ReferencedLayoutPaths,
                territoryLayoutAssets.ReferencedSharedGroupPaths);
            foreach (string modelPath in territoryLayoutAssets.BgObjectModelPaths)
            {
                MergeDirectModelAsset(
                    rowId,
                    TerritoryLayoutSource,
                    territoryLayoutPath,
                    modelPath,
                    bgSearchTerms,
                    territoryMetadata);
            }

            IReadOnlyList<string> vfxSearchTerms = BuildSearchTerms(
                TerritoryLayoutSource,
                rowId.ToString(),
                territoryLayoutPath,
                territoryMetadata.SearchTerms,
                territoryLayoutAssets.ReferencedLayoutPaths,
                territoryLayoutAssets.ReferencedSharedGroupPaths);
            foreach (ResolvedVfxPath resolvedVfxPath in territoryLayoutAssets.ResolvedVfxPaths)
            {
                _ = VfxResolvedPathUtility.TryMergeResolvedPath(
                    _resolvedVfxPaths,
                    _gameData,
                    resolvedVfxPath,
                    extraSearchTerms: vfxSearchTerms);
            }
        }

        private void MergeDirectModelAsset(
            uint rowId,
            string source,
            string sourcePath,
            string modelPath,
            IReadOnlyList<string> searchTerms,
            in ObjectTerritoryMetadata territoryMetadata)
        {
            BgObjectDiscoveryState state = GetOrAddState(modelPath, source, rowId, sourcePath);
            state.AddSearchTerms(searchTerms);
            state.AddTerritoryMetadata(territoryMetadata);
        }

        private BgObjectDiscoveryState GetOrAddState(
            string modelPath,
            string source,
            uint rowId,
            string sourcePath)
        {
            if (_bgObjects.TryGetValue(modelPath, out BgObjectDiscoveryState? state))
            {
                return state;
            }

            state = new BgObjectDiscoveryState(modelPath, source, rowId, sourcePath);
            _bgObjects.Add(modelPath, state);
            return state;
        }

        private static IReadOnlyList<string> BuildSearchTerms(
            string source,
            string rowId,
            string sourcePath,
            params IReadOnlyList<string>?[] relatedTerms)
            => ObjectSearchTermUtility.BuildStableTerms([source, rowId, sourcePath], relatedTerms);
    }

    private sealed class BgObjectDiscoveryState
    {
        private readonly HashSet<string> _searchTerms;
        private readonly ObjectTerritoryMetadataSet _territoryMetadata = new();

        public BgObjectDiscoveryState(string modelPath, string source, uint rowId, string sourcePath)
        {
            ModelPath = modelPath;
            Source = source;
            RowId = rowId;
            SourcePath = sourcePath;
            _searchTerms = ObjectSearchTermUtility.CreateSet(modelPath);
        }

        public string ModelPath { get; }
        public string Source { get; }
        public uint RowId { get; }
        public string SourcePath { get; }

        public void AddSearchTerms(IEnumerable<string> searchTerms)
            => _ = ObjectSearchTermUtility.AddTerms(_searchTerms, searchTerms);

        public void AddTerritoryMetadata(in ObjectTerritoryMetadata territoryMetadata)
            => _ = _territoryMetadata.Add(territoryMetadata);

        public GameDataBgObjectAsset ToAsset()
        {
            HashSet<string> searchTerms = ObjectSearchTermUtility.CreateSet(_searchTerms);
            _territoryMetadata.AddSearchTerms(searchTerms);

            return new GameDataBgObjectAsset(
                ModelPath,
                Source,
                RowId,
                SourcePath,
                _territoryMetadata.BuildStableIds(),
                _territoryMetadata.BuildStableNames(),
                ObjectSearchTermUtility.BuildStableTerms(searchTerms));
        }
    }
}
