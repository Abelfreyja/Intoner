using Intoner.Objects.Utils;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using Microsoft.Extensions.Logging;
using LuminaAction = Lumina.Excel.Sheets.Action;

namespace Intoner.Objects.Assets;

internal sealed class GameDataVfxResolver
{
    private const string StatusEffectSource = "status effect";
    private const string ActionSource = "action effect";
    private const string ActionTimelineSource = "action timeline";
    private const string EmoteTimelineSource = "emote timeline";
    private const string GimmickTimelineSource = "gimmick timeline";

    private readonly ILogger<GameDataVfxResolver> _logger;
    private readonly IObjectAssetGameData _gameData;
    private readonly GameDataLayoutAssetResolver _layoutAssetResolver;

    public GameDataVfxResolver(
        ILogger<GameDataVfxResolver> logger,
        IObjectAssetGameData gameData,
        GameDataLayoutAssetResolver layoutAssetResolver)
    {
        _logger              = logger;
        _gameData            = gameData;
        _layoutAssetResolver = layoutAssetResolver;
    }

    public IReadOnlyList<ResolvedVfxPath> Resolve(
        SqpackIndexSnapshot sqpackIndexSnapshot,
        CancellationToken cancellationToken = default)
    {
        Dictionary<string, ResolvedVfxPathAccumulator> resolvedPaths = new(StringComparer.OrdinalIgnoreCase);
        VfxTimelineReferenceCache timelineReferenceCache = new(_gameData);
        MergeLayoutVfx(resolvedPaths, sqpackIndexSnapshot, cancellationToken);
        CollectStatusVfx(resolvedPaths, sqpackIndexSnapshot, cancellationToken);
        CollectActionVfx(resolvedPaths, sqpackIndexSnapshot, timelineReferenceCache, cancellationToken);
        CollectEmoteTimelineVfx(resolvedPaths, sqpackIndexSnapshot, timelineReferenceCache, cancellationToken);
        CollectGimmickTimelineVfx(resolvedPaths, sqpackIndexSnapshot, timelineReferenceCache, cancellationToken);

        IReadOnlyList<ResolvedVfxPath> snapshot = ResolvedVfxPathAccumulator.BuildSnapshot(resolvedPaths.Values);
        _logger.LogInformation("resolved {VfxCount} static vfx paths from game data sources", snapshot.Count);
        return snapshot;
    }

    private void MergeLayoutVfx(
        IDictionary<string, ResolvedVfxPathAccumulator> resolvedPaths,
        SqpackIndexSnapshot sqpackIndexSnapshot,
        CancellationToken cancellationToken)
    {
        foreach (ResolvedVfxPath resolvedVfxPath in _layoutAssetResolver.Resolve(cancellationToken).ResolvedVfxPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = VfxResolvedPathUtility.TryMergeResolvedPath(
                resolvedPaths,
                _gameData,
                resolvedVfxPath,
                sqpackIndexSnapshot);
        }
    }

    private void CollectStatusVfx(
        IDictionary<string, ResolvedVfxPathAccumulator> resolvedPaths,
        SqpackIndexSnapshot sqpackIndexSnapshot,
        CancellationToken cancellationToken)
    {
        ExcelSheet<Status>? sheet = _gameData.GetCurrentLanguageExcelSheet<Status>();
        if (sheet is null)
        {
            return;
        }

        foreach (Status row in sheet)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
        VfxTimelineReferenceCache timelineReferenceCache,
        CancellationToken cancellationToken)
    {
        ExcelSheet<LuminaAction>? sheet = _gameData.GetCurrentLanguageExcelSheet<LuminaAction>();
        if (sheet is null)
        {
            return;
        }

        foreach (LuminaAction row in sheet)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
                timelineReferenceCache,
                cancellationToken);
            CollectTimelineReferences(
                resolvedPaths,
                sqpackIndexSnapshot,
                RuntimeVfxEvidence.ActionTimeline,
                AssetPathSource.GameData,
                [.. rowSearchTerms, ActionTimelineSource],
                row.AnimationEnd.ValueNullable?.Key.ToString() ?? string.Empty,
                timelineReferenceCache,
                cancellationToken);
            CollectTimelineReferences(
                resolvedPaths,
                sqpackIndexSnapshot,
                RuntimeVfxEvidence.ActionTimeline,
                AssetPathSource.GameData,
                [.. rowSearchTerms, ActionTimelineSource],
                row.ActionTimelineHit.ValueNullable?.Key.ToString() ?? string.Empty,
                timelineReferenceCache,
                cancellationToken);
            CollectTimelineReferences(
                resolvedPaths,
                sqpackIndexSnapshot,
                RuntimeVfxEvidence.ActionTimeline,
                AssetPathSource.GameData,
                [.. rowSearchTerms, ActionTimelineSource],
                row.AnimationEnd.ValueNullable?.WeaponTimeline.ValueNullable?.File.ToString() ?? string.Empty,
                timelineReferenceCache,
                cancellationToken);
        }
    }

    private void CollectEmoteTimelineVfx(
        IDictionary<string, ResolvedVfxPathAccumulator> resolvedPaths,
        SqpackIndexSnapshot sqpackIndexSnapshot,
        VfxTimelineReferenceCache timelineReferenceCache,
        CancellationToken cancellationToken)
    {
        ExcelSheet<Emote>? sheet = _gameData.GetCurrentLanguageExcelSheet<Emote>();
        if (sheet is null)
        {
            return;
        }

        foreach (Emote row in sheet)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
                    timelineReferenceCache,
                    cancellationToken);
            }
        }
    }

    private void CollectGimmickTimelineVfx(
        IDictionary<string, ResolvedVfxPathAccumulator> resolvedPaths,
        SqpackIndexSnapshot sqpackIndexSnapshot,
        VfxTimelineReferenceCache timelineReferenceCache,
        CancellationToken cancellationToken)
    {
        ExcelSheet<ActionTimeline>? sheet = _gameData.GetCurrentLanguageExcelSheet<ActionTimeline>();
        if (sheet is null)
        {
            return;
        }

        foreach (ActionTimeline row in sheet)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
                timelineReferenceCache,
                cancellationToken);
        }
    }

    private void CollectTimelineReferences(
        IDictionary<string, ResolvedVfxPathAccumulator> resolvedPaths,
        SqpackIndexSnapshot sqpackIndexSnapshot,
        RuntimeVfxEvidence sourceEvidence,
        AssetPathSource recoverySource,
        IReadOnlyList<string> baseSearchTerms,
        string timelineKey,
        VfxTimelineReferenceCache timelineReferenceCache,
        CancellationToken cancellationToken)
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
            cancellationToken.ThrowIfCancellationRequested();
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

