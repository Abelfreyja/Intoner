using Intoner.Objects.Utils;

namespace Intoner.Objects.Assets;

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

    public static bool HasSameClassification(VfxStandaloneReport? left, VfxStandaloneReport? right)
        => ReferenceEquals(left, right)
        || (left is not null && right is not null
            && left.PathContracts == right.PathContracts
            && left.FamilyHint == right.FamilyHint
            && left.SupportClass == right.SupportClass
            && HasSameCatalogData(left, right));

    public static bool HasSameProjection(VfxStandaloneReport? left, VfxStandaloneReport? right)
    {
        if (left is not { IsSupportedStandalone: true })
        {
            return right?.IsSupportedStandalone != true
                && (left is null || right is null || HasSamePath(left, right));
        }

        return right is { IsSupportedStandalone: true }
            && HasSameCatalogData(left, right)
            && left.Analysis.LoopFacts == right.Analysis.LoopFacts;
    }

    private static bool HasSameCatalogData(VfxStandaloneReport left, VfxStandaloneReport right)
        => HasSamePath(left, right)
        && string.Equals(left.CatalogSource, right.CatalogSource, StringComparison.OrdinalIgnoreCase)
        && left.SearchTerms.SequenceEqual(right.SearchTerms, StringComparer.OrdinalIgnoreCase);

    private static bool HasSamePath(VfxStandaloneReport left, VfxStandaloneReport right)
        => string.Equals(left.Path, right.Path, StringComparison.OrdinalIgnoreCase);
}
