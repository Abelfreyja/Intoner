namespace Intoner.Objects.Assets;

/// <summary> builds standalone vfx reports from static object asset discovery data </summary>
internal sealed class StandaloneVfxStaticCatalog
{
    private readonly IObjectAssetGameData _gameData;
    private readonly PathKnowledgeBase _knowledgeBase;
    private readonly IReadOnlyDictionary<string, ResolvedVfxPath> _resolvedPaths;
    private readonly IReadOnlyDictionary<string, VfxTimelineReferenceInfo> _timelineReferences;

    public StandaloneVfxStaticCatalog(IObjectAssetGameData gameData, StaticAssetDiscoverySnapshot snapshot)
    {
        _gameData = gameData;
        _knowledgeBase = snapshot.BuildKnowledgeBase();
        _resolvedPaths = snapshot.StaticResolvedVfxPaths;
        _timelineReferences = BuildTimelineReferences(gameData, snapshot.StaticCollisionPaths);
    }

    public bool TryGetReport(string vfxPath, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out VfxStandaloneReport? report)
    {
        string normalizedPath = ObjectPathRules.NormalizeGamePath(vfxPath);
        report = null;
        if (!ObjectPathRules.IsVfxPath(normalizedPath) || !_gameData.FileExists(normalizedPath))
        {
            return false;
        }

        RuntimeVfxEvidence sourceEvidence = RuntimeVfxEvidence.None;
        AssetPathContract pathContracts = _knowledgeBase.TryGetPath(normalizedPath, out KnownAssetPath? knownPath)
            ? knownPath.Contracts
            : AssetPathContract.None;
        KnownVfxFamily familyHint = _resolvedPaths.TryGetValue(normalizedPath, out ResolvedVfxPath? resolvedPath)
            ? resolvedPath.Family
            : knownPath?.VfxFamily ?? KnownVfxFamilyExtensions.InferFamilyHintFromPath(normalizedPath);

        VfxAnalysis? analysis = null;
        if (resolvedPath is not null)
        {
            sourceEvidence |= resolvedPath.Evidence;
            pathContracts |= resolvedPath.Contracts;
            analysis = resolvedPath.Analysis;
        }

        VfxTimelineReferenceInfo timelineReference = _timelineReferences.TryGetValue(normalizedPath, out VfxTimelineReferenceInfo referenceInfo)
            ? referenceInfo
            : VfxTimelineReferenceInfo.None;
        sourceEvidence = sourceEvidence.WithoutAnalysisFlags() | timelineReference.NormalizedEvidence;

        if (analysis is null && !VfxAssetAnalyzer.TryAnalyzeAvfx(_gameData, normalizedPath, out analysis))
        {
            return false;
        }

        report = VfxStandaloneReportFactory.Create(
            normalizedPath,
            familyHint,
            pathContracts,
            sourceEvidence,
            timelineReference,
            analysis,
            runtimeObserved: false);
        return true;
    }

    private static IReadOnlyDictionary<string, VfxTimelineReferenceInfo> BuildTimelineReferences(
        IObjectAssetGameData gameData,
        IReadOnlyList<string> staticCollisionPaths)
    {
        Dictionary<string, VfxTimelineReferenceInfo> timelineReferences = new(StringComparer.OrdinalIgnoreCase);
        foreach (string path in staticCollisionPaths)
        {
            if (!ObjectPathRules.IsCatalogTimelinePath(path))
            {
                continue;
            }

            foreach (TmbVfxReference reference in VfxAssetAnalyzer.CollectTmbVfxReferences(gameData, path))
            {
                VfxTimelineReferenceInfo referenceInfo = new(reference.Evidence, reference.ContextFlags);
                string normalizedPath = ObjectPathRules.NormalizeGamePath(reference.Path);
                if (!timelineReferences.TryGetValue(normalizedPath, out VfxTimelineReferenceInfo existingReference))
                {
                    timelineReferences.Add(normalizedPath, referenceInfo);
                    continue;
                }

                timelineReferences[normalizedPath] = existingReference.Merge(referenceInfo);
            }
        }

        return timelineReferences;
    }
}

internal static class VfxStandaloneReportFactory
{
    public static VfxStandaloneReport Create(
        string path,
        KnownVfxFamily familyHint,
        AssetPathContract pathContracts,
        RuntimeVfxEvidence sourceEvidence,
        VfxTimelineReferenceInfo timelineReference,
        VfxAnalysis analysis,
        bool runtimeObserved)
    {
        VfxStandalonePolicyResult policyResult = VfxStandalonePolicy.Evaluate(
            path,
            analysis,
            sourceEvidence,
            timelineReference.Context,
            pathContracts);
        RuntimeVfxEvidence combinedEvidence = sourceEvidence | policyResult.AnalysisEvidence;

        return new VfxStandaloneReport(
            path,
            combinedEvidence.ResolveCatalogSourceLabel(),
            familyHint,
            pathContracts,
            sourceEvidence,
            combinedEvidence,
            timelineReference,
            analysis,
            policyResult.SupportClass,
            policyResult.UnsupportedReasons,
            policyResult.ContextClues,
            policyResult.UnknownReasons,
            runtimeObserved);
    }
}

