using Lumina.Excel;
using Lumina.Excel.Sheets;
using Microsoft.Extensions.Logging;

namespace Intoner.Objects.Assets;

internal sealed class RootExlVfxFamilyResolver
{
    private const uint VfxDatasetId = 0x76;
    private const uint OmenDatasetId = 0xEE;
    private const uint ChannelingDatasetId = 0x10A;
    private const uint LockonDatasetId = 0x126;
    private const uint EventVfxDatasetId = 0x23D;

    private const string CommonVfxPrefix = "vfx/common/eff";
    private const string OmenVfxPrefix = "vfx/omen/eff";
    private const string ChannelingVfxPrefix = "vfx/channeling/eff";
    private const string LockonVfxPrefix = "vfx/lockon/eff";
    private const string CutEventVfxPrefix = "vfx/cut/general/eff";
    private const string GroupPoseVfxPrefix = "vfx/grouppose/eff";

    private enum EventVfxPathType : byte
    {
        None      = 0,
        Cut       = 1,
        GroupPose = 2,
    }

    private readonly ILogger<RootExlVfxFamilyResolver> _logger;
    private readonly IObjectAssetGameData _gameData;

    public RootExlVfxFamilyResolver(ILogger<RootExlVfxFamilyResolver> logger, IObjectAssetGameData gameData)
    {
        _logger = logger;
        _gameData = gameData;
    }

    public ResolvedVfxDiscoverySnapshot Resolve(
        RootExlDatasetIndex rootExlDatasetIndex,
        SqpackIndexSnapshot sqpackIndexSnapshot,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Dictionary<string, ResolvedVfxPathAccumulator> resolvedPaths = new(StringComparer.OrdinalIgnoreCase);

        bool isComplete = ResolveCommon(rootExlDatasetIndex, sqpackIndexSnapshot, resolvedPaths, cancellationToken);
        isComplete &= ResolveOmen(rootExlDatasetIndex, sqpackIndexSnapshot, resolvedPaths, cancellationToken);
        isComplete &= ResolveChanneling(rootExlDatasetIndex, sqpackIndexSnapshot, resolvedPaths, cancellationToken);
        isComplete &= ResolveLockon(rootExlDatasetIndex, sqpackIndexSnapshot, resolvedPaths, cancellationToken);
        isComplete &= ResolveEvent(rootExlDatasetIndex, sqpackIndexSnapshot, resolvedPaths, cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<ResolvedVfxPath> snapshot = ResolvedVfxPathAccumulator.BuildSnapshot(resolvedPaths.Values);

        _logger.LogInformation("resolved {VfxCount} static vfx paths from root.exl-backed family resolvers", snapshot.Count);
        return new ResolvedVfxDiscoverySnapshot(snapshot, isComplete);
    }

    private bool TryRequireDataset(RootExlDatasetIndex rootExlDatasetIndex, string datasetName, uint expectedDatasetId)
    {
        if (!rootExlDatasetIndex.TryGetDatasetId(datasetName, out uint actualDatasetId))
        {
            _logger.LogWarning("root.exl is missing dataset {DatasetName}", datasetName);
            return false;
        }

        if (actualDatasetId != expectedDatasetId)
        {
            _logger.LogWarning(
                "root.exl dataset id changed for {DatasetName}: expected 0x{ExpectedId:x}, got 0x{ActualId:x}; continuing with live root.exl data",
                datasetName,
                expectedDatasetId,
                actualDatasetId);
        }

        return true;
    }

    private bool ResolveCommon(
        RootExlDatasetIndex rootExlDatasetIndex,
        SqpackIndexSnapshot sqpackIndexSnapshot,
        IDictionary<string, ResolvedVfxPathAccumulator> resolvedPaths,
        CancellationToken cancellationToken)
    {
        if (!TryRequireDataset(rootExlDatasetIndex, "VFX", VfxDatasetId))
        {
            return false;
        }

        ExcelSheet<VFX>? sheet = _gameData.GetExcelSheet<VFX>();
        if (sheet is null)
        {
            return false;
        }

        foreach (VFX row in sheet)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string token = row.Location.ExtractText();
            TryAddTokenResolvedPath(
                sqpackIndexSnapshot,
                resolvedPaths,
                CommonVfxPrefix,
                token,
                KnownVfxFamily.Common,
                RuntimeVfxEvidence.Common,
                ["common effect", "VFX", token]);
        }

        return true;
    }

    private bool ResolveOmen(
        RootExlDatasetIndex rootExlDatasetIndex,
        SqpackIndexSnapshot sqpackIndexSnapshot,
        IDictionary<string, ResolvedVfxPathAccumulator> resolvedPaths,
        CancellationToken cancellationToken)
    {
        if (!TryRequireDataset(rootExlDatasetIndex, "Omen", OmenDatasetId))
        {
            return false;
        }

        ExcelSheet<Omen>? sheet = _gameData.GetExcelSheet<Omen>();
        if (sheet is null)
        {
            return false;
        }

        foreach (Omen row in sheet)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string token = row.Path.ExtractText();
            TryAddTokenResolvedPath(
                sqpackIndexSnapshot,
                resolvedPaths,
                OmenVfxPrefix,
                token,
                KnownVfxFamily.Omen,
                RuntimeVfxEvidence.Omen,
                ["omen", "Omen", token]);

            string allyToken = row.PathAlly.ExtractText();
            TryAddTokenResolvedPath(
                sqpackIndexSnapshot,
                resolvedPaths,
                OmenVfxPrefix,
                allyToken,
                KnownVfxFamily.Omen,
                RuntimeVfxEvidence.Omen,
                ["omen", "Omen", "ally", allyToken]);
        }

        return true;
    }

    private bool ResolveChanneling(
        RootExlDatasetIndex rootExlDatasetIndex,
        SqpackIndexSnapshot sqpackIndexSnapshot,
        IDictionary<string, ResolvedVfxPathAccumulator> resolvedPaths,
        CancellationToken cancellationToken)
    {
        if (!TryRequireDataset(rootExlDatasetIndex, "Channeling", ChannelingDatasetId))
        {
            return false;
        }

        ExcelSheet<Channeling>? sheet = _gameData.GetExcelSheet<Channeling>();
        if (sheet is null)
        {
            return false;
        }

        foreach (Channeling row in sheet)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string token = row.File.ExtractText();
            TryAddTokenResolvedPath(
                sqpackIndexSnapshot,
                resolvedPaths,
                ChannelingVfxPrefix,
                token,
                KnownVfxFamily.Channeling,
                RuntimeVfxEvidence.Channeling,
                ["channeling", "Channeling", token]);
        }

        return true;
    }

    private bool ResolveLockon(
        RootExlDatasetIndex rootExlDatasetIndex,
        SqpackIndexSnapshot sqpackIndexSnapshot,
        IDictionary<string, ResolvedVfxPathAccumulator> resolvedPaths,
        CancellationToken cancellationToken)
    {
        if (!TryRequireDataset(rootExlDatasetIndex, "Lockon", LockonDatasetId))
        {
            return false;
        }

        ExcelSheet<Lockon>? sheet = _gameData.GetExcelSheet<Lockon>();
        if (sheet is null)
        {
            return false;
        }

        foreach (Lockon row in sheet)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string token = row.IconName.ExtractText();
            TryAddTokenResolvedPath(
                sqpackIndexSnapshot,
                resolvedPaths,
                LockonVfxPrefix,
                token,
                KnownVfxFamily.Lockon,
                RuntimeVfxEvidence.Lockon,
                ["lockon", "Lockon", token]);
        }

        return true;
    }

    private bool ResolveEvent(
        RootExlDatasetIndex rootExlDatasetIndex,
        SqpackIndexSnapshot sqpackIndexSnapshot,
        IDictionary<string, ResolvedVfxPathAccumulator> resolvedPaths,
        CancellationToken cancellationToken)
    {
        if (!TryRequireDataset(rootExlDatasetIndex, "EventVfx", EventVfxDatasetId))
        {
            return false;
        }

        ExcelSheet<EventVfx>? sheet = _gameData.GetExcelSheet<EventVfx>();
        if (sheet is null)
        {
            return false;
        }

        foreach (EventVfx row in sheet)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string token = row.Unknown1.ExtractText();
            string label = row.Unknown0.ExtractText();
            EventVfxPathType eventType = (EventVfxPathType)row.Unknown2;
            if (!TryResolveEventPathPrefix(eventType, out string pathPrefix))
            {
                continue;
            }

            TryAddTokenResolvedPath(
                sqpackIndexSnapshot,
                resolvedPaths,
                pathPrefix,
                token,
                ResolveEventFamily(eventType),
                RuntimeVfxEvidence.Event,
                BuildEventSearchTerms(label, token, eventType));
        }

        return true;
    }

    private void TryAddTokenResolvedPath(
        SqpackIndexSnapshot sqpackIndexSnapshot,
        IDictionary<string, ResolvedVfxPathAccumulator> resolvedPaths,
        string pathPrefix,
        string token,
        KnownVfxFamily family,
        RuntimeVfxEvidence evidence,
        IEnumerable<string> searchTerms)
    {
        if (!GameDataAssetPathUtility.TryBuildVfxFamilyPath(pathPrefix, token, out string path))
        {
            return;
        }

        _ = VfxResolvedPathUtility.TryMergeResolvedPath(
            resolvedPaths,
            _gameData,
            path,
            sqpackIndexSnapshot,
            family,
            evidence,
            AssetPathSource.RootExl,
            AssetPathContract.DeterministicBuilder,
            searchTerms);
    }

    private static IReadOnlyList<string> BuildEventSearchTerms(string label, string token, EventVfxPathType eventType)
    {
        List<string> searchTerms =
        [
            "event vfx",
            "EventVfx",
            token,
        ];

        if (!string.IsNullOrWhiteSpace(label))
        {
            searchTerms.Add(label);
        }

        if (eventType == EventVfxPathType.Cut)
        {
            searchTerms.Add("cut");
        }
        else if (eventType == EventVfxPathType.GroupPose)
        {
            searchTerms.Add("grouppose");
        }

        return searchTerms;
    }

    private static bool TryResolveEventPathPrefix(EventVfxPathType eventType, out string pathPrefix)
    {
        pathPrefix = eventType switch
        {
            EventVfxPathType.Cut       => CutEventVfxPrefix,
            EventVfxPathType.GroupPose => GroupPoseVfxPrefix,
            _                          => string.Empty,
        };

        return !string.IsNullOrWhiteSpace(pathPrefix);
    }

    private static KnownVfxFamily ResolveEventFamily(EventVfxPathType eventType)
        => eventType == EventVfxPathType.GroupPose
            ? KnownVfxFamily.Event | KnownVfxFamily.GroupPose
            : KnownVfxFamily.Event;
}
