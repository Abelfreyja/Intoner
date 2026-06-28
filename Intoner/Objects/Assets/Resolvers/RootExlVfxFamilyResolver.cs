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

    public IReadOnlyList<ResolvedVfxPath> Resolve(RootExlDatasetIndex rootExlDatasetIndex, SqpackIndexSnapshot sqpackIndexSnapshot)
    {
        Dictionary<string, ResolvedVfxPathAccumulator> resolvedPaths = new(StringComparer.OrdinalIgnoreCase);

        ResolveCommon(rootExlDatasetIndex, sqpackIndexSnapshot, resolvedPaths);
        ResolveOmen(rootExlDatasetIndex, sqpackIndexSnapshot, resolvedPaths);
        ResolveChanneling(rootExlDatasetIndex, sqpackIndexSnapshot, resolvedPaths);
        ResolveLockon(rootExlDatasetIndex, sqpackIndexSnapshot, resolvedPaths);
        ResolveEvent(rootExlDatasetIndex, sqpackIndexSnapshot, resolvedPaths);

        IReadOnlyList<ResolvedVfxPath> snapshot = ResolvedVfxPathAccumulator.BuildSnapshot(resolvedPaths.Values);

        _logger.LogInformation("resolved {VfxCount} static vfx paths from root.exl-backed family resolvers", snapshot.Count);
        return snapshot;
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

    private void ResolveCommon(
        RootExlDatasetIndex rootExlDatasetIndex,
        SqpackIndexSnapshot sqpackIndexSnapshot,
        IDictionary<string, ResolvedVfxPathAccumulator> resolvedPaths)
    {
        if (!TryRequireDataset(rootExlDatasetIndex, "VFX", VfxDatasetId))
        {
            return;
        }

        ExcelSheet<VFX>? sheet = _gameData.GetExcelSheet<VFX>();
        if (sheet is null)
        {
            return;
        }

        foreach (VFX row in sheet)
        {
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
    }

    private void ResolveOmen(
        RootExlDatasetIndex rootExlDatasetIndex,
        SqpackIndexSnapshot sqpackIndexSnapshot,
        IDictionary<string, ResolvedVfxPathAccumulator> resolvedPaths)
    {
        if (!TryRequireDataset(rootExlDatasetIndex, "Omen", OmenDatasetId))
        {
            return;
        }

        ExcelSheet<Omen>? sheet = _gameData.GetExcelSheet<Omen>();
        if (sheet is null)
        {
            return;
        }

        foreach (Omen row in sheet)
        {
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
    }

    private void ResolveChanneling(
        RootExlDatasetIndex rootExlDatasetIndex,
        SqpackIndexSnapshot sqpackIndexSnapshot,
        IDictionary<string, ResolvedVfxPathAccumulator> resolvedPaths)
    {
        if (!TryRequireDataset(rootExlDatasetIndex, "Channeling", ChannelingDatasetId))
        {
            return;
        }

        ExcelSheet<Channeling>? sheet = _gameData.GetExcelSheet<Channeling>();
        if (sheet is null)
        {
            return;
        }

        foreach (Channeling row in sheet)
        {
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
    }

    private void ResolveLockon(
        RootExlDatasetIndex rootExlDatasetIndex,
        SqpackIndexSnapshot sqpackIndexSnapshot,
        IDictionary<string, ResolvedVfxPathAccumulator> resolvedPaths)
    {
        if (!TryRequireDataset(rootExlDatasetIndex, "Lockon", LockonDatasetId))
        {
            return;
        }

        ExcelSheet<Lockon>? sheet = _gameData.GetExcelSheet<Lockon>();
        if (sheet is null)
        {
            return;
        }

        foreach (Lockon row in sheet)
        {
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
    }

    private void ResolveEvent(
        RootExlDatasetIndex rootExlDatasetIndex,
        SqpackIndexSnapshot sqpackIndexSnapshot,
        IDictionary<string, ResolvedVfxPathAccumulator> resolvedPaths)
    {
        if (!TryRequireDataset(rootExlDatasetIndex, "EventVfx", EventVfxDatasetId))
        {
            return;
        }

        ExcelSheet<EventVfx>? sheet = _gameData.GetExcelSheet<EventVfx>();
        if (sheet is null)
        {
            return;
        }

        foreach (EventVfx row in sheet)
        {
            string token = row.Unknown1.ExtractText();
            string label = row.Unknown0.ExtractText();
            if (!TryResolveEventPathPrefix((EventVfxPathType)row.Unknown2, out string pathPrefix))
            {
                continue;
            }

            TryAddTokenResolvedPath(
                sqpackIndexSnapshot,
                resolvedPaths,
                pathPrefix,
                token,
                KnownVfxFamily.Event,
                RuntimeVfxEvidence.Event,
                BuildEventSearchTerms(label, token, (EventVfxPathType)row.Unknown2));
        }
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
        if (!TryBuildVfxPath(pathPrefix, token, out string path))
        {
            return;
        }

        TryAddResolvedPath(
            sqpackIndexSnapshot,
            resolvedPaths,
            path,
            family,
            evidence,
            AssetPathSource.RootExl,
            AssetPathContract.DeterministicBuilder,
            searchTerms);
    }

    private void TryAddResolvedPath(
        SqpackIndexSnapshot sqpackIndexSnapshot,
        IDictionary<string, ResolvedVfxPathAccumulator> resolvedPaths,
        string path,
        KnownVfxFamily family,
        RuntimeVfxEvidence evidence,
        AssetPathSource sources,
        AssetPathContract contracts,
        IEnumerable<string> searchTerms)
        => _ = VfxResolvedPathUtility.TryMergeResolvedPath(
            resolvedPaths,
            _gameData,
            path,
            sqpackIndexSnapshot,
            family,
            evidence,
            sources,
            contracts,
            searchTerms);

    private static bool TryBuildVfxPath(string prefix, string token, out string path)
    {
        string normalizedPrefix = ObjectPathRules.NormalizeGamePath(prefix);
        string normalizedToken = ObjectPathRules.NormalizeGamePath(token);
        if (string.IsNullOrWhiteSpace(normalizedPrefix) || string.IsNullOrWhiteSpace(normalizedToken))
        {
            path = string.Empty;
            return false;
        }

        bool hasExtension = ObjectPathRules.IsVfxPath(normalizedToken);
        if (normalizedToken.StartsWith("vfx/", StringComparison.OrdinalIgnoreCase))
        {
            path = hasExtension ? normalizedToken : $"{normalizedToken}.avfx";
            return true;
        }

        path = hasExtension
            ? $"{normalizedPrefix}/{normalizedToken}"
            : $"{normalizedPrefix}/{normalizedToken}.avfx";
        return true;
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
}

