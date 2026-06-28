namespace Intoner.Objects.Assets;

internal sealed record ObservedBgAsset(
    string Path,
    string Source,
    IReadOnlyList<uint> TerritoryIds,
    IReadOnlyList<string> TerritoryNames,
    IReadOnlyList<string> SearchTerms);

internal sealed record RuntimeVfxAsset(
    string Path,
    string Source,
    IReadOnlyList<string> SearchTerms);

/// <summary> describes classification for one vfx path </summary>
internal sealed record VfxStandaloneReport(
    string Path,
    string CatalogSource,
    KnownVfxFamily FamilyHint,
    AssetPathContract PathContracts,
    RuntimeVfxEvidence SourceEvidence,
    RuntimeVfxEvidence CombinedEvidence,
    VfxTimelineReferenceInfo TimelineReference,
    VfxAnalysis Analysis,
    VfxStandaloneSupportClass SupportClass,
    VfxStandaloneUnsupportedReason UnsupportedReasons,
    VfxStandaloneContextClue ContextClues,
    VfxStandaloneUnknownReason UnknownReasons,
    bool SeenFromRuntime)
{
    public bool IsSupportedStandalone
        => SupportClass == VfxStandaloneSupportClass.SupportedStandalone;

    public IReadOnlyList<string> UnsupportedReasonLabels
        => UnsupportedReasons.ToLabels();

    public IReadOnlyList<string> ContextClueLabels
        => ContextClues.ToLabels();

    public IReadOnlyList<string> UnknownReasonLabels
        => UnknownReasons.ToLabels();
}

