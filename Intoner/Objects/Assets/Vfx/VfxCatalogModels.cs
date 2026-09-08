using Intoner.Objects.Utils;

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
    IReadOnlyList<string> SearchTerms,
    VfxLoopFacts LoopFacts);

internal static class VfxCatalogSearchTerms
{
    private static readonly string[] PermanentLoopSearchTerms =
    [
        "loop",
        "looping vfx",
        "permanent loop",
    ];

    public static IReadOnlyList<string> Build(
        string path,
        string catalogSource,
        KnownVfxFamily familyHint,
        VfxTimelineReferenceInfo timelineReference,
        VfxLoopFacts loopFacts,
        IEnumerable<string> knownSearchTerms)
    {
        HashSet<string> searchTerms = SearchTermUtility.CreateSet(path, catalogSource);
        _ = SearchTermUtility.AddPathSegments(searchTerms, path);
        _ = SearchTermUtility.AddTerms(searchTerms, knownSearchTerms);
        foreach (string familyLabel in familyHint.EnumerateSearchLabels())
        {
            _ = SearchTermUtility.AddTerm(searchTerms, familyLabel);
        }

        if (timelineReference.HasEvidence)
        {
            _ = SearchTermUtility.AddTerms(searchTerms, timelineReference.BuildSearchTerms());
        }

        if (loopFacts.IsPermanent)
        {
            _ = SearchTermUtility.AddTerms(searchTerms, PermanentLoopSearchTerms);
        }

        return SearchTermUtility.BuildStableTerms(searchTerms);
    }
}

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
    IReadOnlyList<string> SearchTerms,
    VfxStandaloneSupportClass SupportClass,
    bool SeenFromRuntime)
{
    public bool IsSupportedStandalone
        => SupportClass == VfxStandaloneSupportClass.SupportedStandalone;
}
