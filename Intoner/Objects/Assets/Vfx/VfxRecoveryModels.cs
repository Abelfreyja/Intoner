using Intoner.Objects.Utils;

namespace Intoner.Objects.Assets;

[Flags]
internal enum RuntimeVfxEvidence
{
    None                    = 0,
    Omen                    = 1 << 0,
    LayoutAutoplay          = 1 << 1,
    ResourceLoad            = 1 << 2,
    StaticCreate            = 1 << 3,
    ActorCreate             = 1 << 4,
    TriggerReferenced       = 1 << 5,
    TriggerUsed             = 1 << 6,
    Binder                  = 1 << 7,
    TimelineBinder          = 1 << 8,
    SchedulerTrigger        = 1 << 9,
    Renderable              = 1 << 10,
    Common                  = 1 << 11,
    Channeling              = 1 << 12,
    Lockon                  = 1 << 13,
    Event                   = 1 << 14,
    Equipment               = 1 << 15,
    Weapon                  = 1 << 16,
    Monster                 = 1 << 17,
    DemiHuman               = 1 << 18,
    Accessory               = 1 << 19,
    AsyncTimelineReferenced = 1 << 20,
    LoVM                    = 1 << 21,
    Status                  = 1 << 22,
    Action                  = 1 << 23,
    ActionTimeline          = 1 << 24,
    EmoteTimeline           = 1 << 25,
    GimmickTimeline         = 1 << 26,
    LayoutTimeline          = 1 << 27,
    Glasses                 = 1 << 28,
    LayoutInstance          = 1 << 29,
    TimelineReferenced      = 1 << 30,
}

internal sealed record ResolvedVfxPath(
    string Path,
    KnownVfxFamily Family,
    RuntimeVfxEvidence Evidence,
    AssetPathSource Sources,
    AssetPathContract Contracts,
    IReadOnlyList<string> SearchTerms,
    VfxAnalysis? Analysis = null);

internal sealed class ResolvedVfxPathAccumulator
{
    public ResolvedVfxPathAccumulator(
        string path,
        KnownVfxFamily family,
        AssetPathSource sources,
        AssetPathContract contracts)
    {
        Path = path;
        Family = family;
        Sources = sources;
        Contracts = contracts;
        SearchTerms = ObjectSearchTermUtility.CreateSet(path);
    }

    public string Path { get; }
    public KnownVfxFamily Family { get; private set; }
    public RuntimeVfxEvidence Evidence { get; private set; }
    public AssetPathSource Sources { get; private set; }
    public AssetPathContract Contracts { get; private set; }
    public HashSet<string> SearchTerms { get; }
    public VfxAnalysis? Analysis { get; private set; }

    public void Merge(
        KnownVfxFamily family,
        RuntimeVfxEvidence evidence,
        AssetPathSource sources,
        AssetPathContract contracts,
        IEnumerable<string> searchTerms,
        VfxAnalysis? analysis)
    {
        Family |= family;
        Evidence |= evidence;
        Sources |= sources;
        Contracts |= contracts;
        _ = ObjectSearchTermUtility.AddTerms(SearchTerms, searchTerms);
        Analysis ??= analysis;
    }

    public ResolvedVfxPath ToResolvedVfxPath()
        => new(
            Path,
            Family,
            Evidence,
            Sources,
            Contracts,
            ObjectSearchTermUtility.BuildStableTerms(SearchTerms),
            Analysis);

    public static void MergeInto(
        IDictionary<string, ResolvedVfxPathAccumulator> resolvedPaths,
        string normalizedPath,
        KnownVfxFamily family,
        RuntimeVfxEvidence evidence,
        AssetPathSource sources,
        AssetPathContract contracts,
        IEnumerable<string> searchTerms,
        VfxAnalysis? analysis = null)
    {
        if (!resolvedPaths.TryGetValue(normalizedPath, out ResolvedVfxPathAccumulator? state))
        {
            state = new ResolvedVfxPathAccumulator(normalizedPath, family, sources, contracts);
            resolvedPaths.Add(normalizedPath, state);
        }

        state.Merge(family, evidence, sources, contracts, searchTerms, analysis);
    }

    public static IReadOnlyList<ResolvedVfxPath> BuildSnapshot(IEnumerable<ResolvedVfxPathAccumulator> resolvedPaths)
        => resolvedPaths
            .Select(static state => state.ToResolvedVfxPath())
            .OrderBy(static resolvedPath => resolvedPath.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
}

internal static class RuntimeVfxEvidenceExtensions
{
    private readonly record struct RuntimeVfxCatalogSourceRule(RuntimeVfxEvidence Evidence, string Label);

    private static readonly RuntimeVfxCatalogSourceRule[] CatalogSourceRules =
    [
        new(RuntimeVfxEvidence.LayoutInstance, "layout vfx"),
        new(RuntimeVfxEvidence.LayoutAutoplay, "layout autoplay"),
        new(RuntimeVfxEvidence.LayoutTimeline, "layout timeline"),
        new(RuntimeVfxEvidence.GimmickTimeline, "gimmick timeline"),
        new(RuntimeVfxEvidence.EmoteTimeline, "emote timeline"),
        new(RuntimeVfxEvidence.ActionTimeline, "action timeline"),
        new(RuntimeVfxEvidence.Status, "status effect"),
        new(RuntimeVfxEvidence.Action, "action effect"),
        new(RuntimeVfxEvidence.Omen, "omen"),
        new(RuntimeVfxEvidence.Channeling, "channeling"),
        new(RuntimeVfxEvidence.Lockon, "lockon"),
        new(RuntimeVfxEvidence.Event, "event vfx"),
        new(RuntimeVfxEvidence.Weapon, "weapon effect"),
        new(RuntimeVfxEvidence.Equipment, "equipment effect"),
        new(RuntimeVfxEvidence.Glasses, "glasses effect"),
        new(RuntimeVfxEvidence.Accessory, "accessory effect"),
        new(RuntimeVfxEvidence.Monster, "monster effect"),
        new(RuntimeVfxEvidence.DemiHuman, "demihuman effect"),
        new(RuntimeVfxEvidence.LoVM, "lovm effect"),
        new(RuntimeVfxEvidence.Common, "common effect"),
        new(RuntimeVfxEvidence.StaticCreate, "observed static"),
        new(RuntimeVfxEvidence.TimelineReferenced, "timeline vfx"),
    ];

    public static RuntimeVfxEvidence WithoutAnalysisFlags(this RuntimeVfxEvidence evidence)
        => evidence & ~(
            RuntimeVfxEvidence.Binder
          | RuntimeVfxEvidence.TimelineBinder
          | RuntimeVfxEvidence.SchedulerTrigger
          | RuntimeVfxEvidence.Renderable);

    public static bool HasAny(this RuntimeVfxEvidence value, RuntimeVfxEvidence flags)
        => (value & flags) != RuntimeVfxEvidence.None;

    public static bool HasAll(this RuntimeVfxEvidence value, RuntimeVfxEvidence flags)
        => (value & flags) == flags;

    public static string ResolveCatalogSourceLabel(this RuntimeVfxEvidence evidence)
    {
        foreach (RuntimeVfxCatalogSourceRule rule in CatalogSourceRules)
        {
            if (evidence.HasAny(rule.Evidence))
            {
                return rule.Label;
            }
        }

        return "vfx";
    }
}

