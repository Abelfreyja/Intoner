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

    public GameDataVfxResolver(
        ILogger<GameDataVfxResolver> logger,
        IObjectAssetGameData gameData)
    {
        _logger   = logger;
        _gameData = gameData;
    }

    public ResolvedVfxDiscoverySnapshot Resolve(
        SqpackIndexSnapshot sqpackIndexSnapshot,
        GameDataLayoutAssetResolver.Snapshot layoutSnapshot,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Dictionary<string, ResolvedVfxPathAccumulator> resolvedPaths = new(StringComparer.OrdinalIgnoreCase);
        VfxTimelineReferenceCache timelineReferenceCache = new();
        bool isComplete = layoutSnapshot.VfxComplete;
        MergeLayoutVfx(resolvedPaths, sqpackIndexSnapshot, layoutSnapshot, cancellationToken);
        isComplete &= CollectStatusVfx(resolvedPaths, sqpackIndexSnapshot, cancellationToken);
        isComplete &= CollectActionVfx(resolvedPaths, sqpackIndexSnapshot, timelineReferenceCache, cancellationToken);
        isComplete &= CollectEmoteTimelineVfx(resolvedPaths, sqpackIndexSnapshot, timelineReferenceCache, cancellationToken);
        isComplete &= CollectGimmickTimelineVfx(resolvedPaths, sqpackIndexSnapshot, timelineReferenceCache, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyList<ResolvedVfxPath> snapshot = ResolvedVfxPathAccumulator.BuildSnapshot(resolvedPaths.Values);
        _logger.LogInformation("resolved {VfxCount} static vfx paths from game data sources", snapshot.Count);
        return new ResolvedVfxDiscoverySnapshot(snapshot, isComplete);
    }

    private void MergeLayoutVfx(
        IDictionary<string, ResolvedVfxPathAccumulator> resolvedPaths,
        SqpackIndexSnapshot sqpackIndexSnapshot,
        GameDataLayoutAssetResolver.Snapshot layoutSnapshot,
        CancellationToken cancellationToken)
    {
        foreach (ResolvedVfxPath resolvedVfxPath in layoutSnapshot.ResolvedVfxPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = VfxResolvedPathUtility.TryMergeResolvedPath(
                resolvedPaths,
                _gameData,
                resolvedVfxPath,
                sqpackIndexSnapshot);
        }
    }

    private bool CollectStatusVfx(
        IDictionary<string, ResolvedVfxPathAccumulator> resolvedPaths,
        SqpackIndexSnapshot sqpackIndexSnapshot,
        CancellationToken cancellationToken)
    {
        ExcelSheet<Status>? sheet = _gameData.GetCurrentLanguageExcelSheet<Status>();
        if (sheet is null)
        {
            return false;
        }

        foreach (Status row in sheet)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string rowName = row.Name.ExtractText();
            IReadOnlyList<string> rowSearchTerms = SearchTermUtility.BuildStableTerms(StatusEffectSource, row.RowId.ToString(), rowName);

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

        return true;
    }

    private bool CollectActionVfx(
        IDictionary<string, ResolvedVfxPathAccumulator> resolvedPaths,
        SqpackIndexSnapshot sqpackIndexSnapshot,
        VfxTimelineReferenceCache timelineReferenceCache,
        CancellationToken cancellationToken)
    {
        ExcelSheet<LuminaAction>? sheet = _gameData.GetCurrentLanguageExcelSheet<LuminaAction>();
        if (sheet is null)
        {
            return false;
        }

        bool isComplete = true;
        foreach (LuminaAction row in sheet)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string rowName = row.Name.ExtractText();
            IReadOnlyList<string> rowSearchTerms = SearchTermUtility.BuildStableTerms(ActionSource, row.RowId.ToString(), rowName);

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

            IReadOnlyList<string> timelineSearchTerms = [.. rowSearchTerms, ActionTimelineSource];
            foreach (string timelineKey in EnumerateActionTimelineKeys(row))
            {
                isComplete &= CollectTimelineReferences(
                    resolvedPaths,
                    sqpackIndexSnapshot,
                    RuntimeVfxEvidence.ActionTimeline,
                    timelineSearchTerms,
                    timelineKey,
                    timelineReferenceCache,
                    cancellationToken);
            }
        }

        return isComplete;
    }

    private static IEnumerable<string> EnumerateActionTimelineKeys(LuminaAction row)
    {
        yield return row.AnimationStart.ValueNullable?.Name.ValueNullable?.Key.ToString() ?? string.Empty;
        yield return row.AnimationEnd.ValueNullable?.Key.ToString() ?? string.Empty;
        yield return row.ActionTimelineHit.ValueNullable?.Key.ToString() ?? string.Empty;
        yield return row.AnimationEnd.ValueNullable?.WeaponTimeline.ValueNullable?.File.ToString() ?? string.Empty;
    }

    private bool CollectEmoteTimelineVfx(
        IDictionary<string, ResolvedVfxPathAccumulator> resolvedPaths,
        SqpackIndexSnapshot sqpackIndexSnapshot,
        VfxTimelineReferenceCache timelineReferenceCache,
        CancellationToken cancellationToken)
    {
        ExcelSheet<Emote>? sheet = _gameData.GetCurrentLanguageExcelSheet<Emote>();
        if (sheet is null)
        {
            return false;
        }

        bool isComplete = true;
        foreach (Emote row in sheet)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string rowName = row.Name.ExtractText();
            IReadOnlyList<string> rowSearchTerms = SearchTermUtility.BuildStableTerms(EmoteTimelineSource, row.RowId.ToString(), rowName);

            foreach (var timeline in row.ActionTimeline)
            {
                isComplete &= CollectTimelineReferences(
                    resolvedPaths,
                    sqpackIndexSnapshot,
                    RuntimeVfxEvidence.EmoteTimeline,
                    rowSearchTerms,
                    timeline.ValueNullable?.Key.ToString() ?? string.Empty,
                    timelineReferenceCache,
                    cancellationToken);
            }
        }

        return isComplete;
    }

    private bool CollectGimmickTimelineVfx(
        IDictionary<string, ResolvedVfxPathAccumulator> resolvedPaths,
        SqpackIndexSnapshot sqpackIndexSnapshot,
        VfxTimelineReferenceCache timelineReferenceCache,
        CancellationToken cancellationToken)
    {
        ExcelSheet<ActionTimeline>? sheet = _gameData.GetCurrentLanguageExcelSheet<ActionTimeline>();
        if (sheet is null)
        {
            return false;
        }

        bool isComplete = true;
        foreach (ActionTimeline row in sheet)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string key = row.Key.ToString();
            if (string.IsNullOrWhiteSpace(key)
             || !key.Contains("gimmick", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            isComplete &= CollectTimelineReferences(
                resolvedPaths,
                sqpackIndexSnapshot,
                RuntimeVfxEvidence.GimmickTimeline,
                SearchTermUtility.BuildStableTerms(GimmickTimelineSource, row.RowId.ToString(), key),
                key,
                timelineReferenceCache,
                cancellationToken);
        }

        return isComplete;
    }

    private bool CollectTimelineReferences(
        IDictionary<string, ResolvedVfxPathAccumulator> resolvedPaths,
        SqpackIndexSnapshot sqpackIndexSnapshot,
        RuntimeVfxEvidence sourceEvidence,
        IReadOnlyList<string> baseSearchTerms,
        string timelineKey,
        VfxTimelineReferenceCache timelineReferenceCache,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!GameDataAssetPathUtility.TryBuildActionTimelinePath(timelineKey, out string timelinePath))
        {
            return true;
        }

        TmbVfxReferenceCollection collection = timelineReferenceCache.Get(_gameData, timelinePath, cancellationToken);
        return VfxResolvedPathUtility.MergeTimelinePaths(
            resolvedPaths,
            _gameData,
            collection,
            sourceEvidence,
            SearchTermUtility.MergeTerms(baseSearchTerms, [timelinePath]),
            sqpackIndexSnapshot,
            cancellationToken);
    }
}
