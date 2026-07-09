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
        HashSet<string> searchTerms = ObjectSearchTermUtility.CreateSet(path, catalogSource);
        _ = ObjectSearchTermUtility.AddPathSegments(searchTerms, path);
        _ = ObjectSearchTermUtility.AddTerms(searchTerms, knownSearchTerms);
        foreach (string familyLabel in familyHint.EnumerateSearchLabels())
        {
            _ = ObjectSearchTermUtility.AddTerm(searchTerms, familyLabel);
        }

        if (timelineReference.HasEvidence)
        {
            _ = ObjectSearchTermUtility.AddTerms(searchTerms, timelineReference.BuildSearchTerms());
        }

        if (loopFacts.IsPermanent)
        {
            _ = ObjectSearchTermUtility.AddTerms(searchTerms, PermanentLoopSearchTerms);
        }

        return ObjectSearchTermUtility.BuildStableTerms(searchTerms);
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
