using System.Runtime.InteropServices;
using Intoner.Objects.Utils;

namespace Intoner.Objects.Assets;

[StructLayout(LayoutKind.Auto)]
internal readonly record struct TmbVfxReference(
    string Path,
    RuntimeVfxEvidence Evidence,
    VfxTimelineContext ContextFlags)
{
    public IReadOnlyList<string> SearchTerms
        => new VfxTimelineReferenceInfo(Evidence, ContextFlags).BuildSearchTerms();
}

[StructLayout(LayoutKind.Auto)]
internal readonly record struct TmbVfxReferenceCollection(
    IReadOnlyList<TmbVfxReference> References,
    bool IsComplete);

[Flags]
internal enum VfxTimelineContext : byte
{
    None                 = 0,
    NestedTimeline       = 1 << 0,
    AnimationReferenced  = 1 << 1,
    SoundReferenced      = 1 << 2,
    NonDefaultBindPoints = 1 << 3,
}

internal readonly record struct VfxTimelineReferenceInfo(
    RuntimeVfxEvidence Evidence,
    VfxTimelineContext Context)
{
    public static VfxTimelineReferenceInfo None { get; } = new(RuntimeVfxEvidence.None, VfxTimelineContext.None);

    public RuntimeVfxEvidence NormalizedEvidence
        => Evidence & (
            RuntimeVfxEvidence.TimelineReferenced
          | RuntimeVfxEvidence.TriggerReferenced
          | RuntimeVfxEvidence.AsyncTimelineReferenced);

    public bool HasEvidence
        => NormalizedEvidence != RuntimeVfxEvidence.None;

    public bool IsCoveredBy(VfxTimelineReferenceInfo other)
        => (NormalizedEvidence & ~other.NormalizedEvidence) == RuntimeVfxEvidence.None
            && (Context & ~other.Context) == VfxTimelineContext.None;

    public VfxTimelineReferenceInfo Merge(VfxTimelineReferenceInfo other)
        => new(NormalizedEvidence | other.NormalizedEvidence, Context | other.Context);
}

internal static class VfxTimelineReferenceInfoExtensions
{
    private readonly record struct TimelineEvidenceSearchRule(RuntimeVfxEvidence Evidence, string[] Terms);
    private readonly record struct TimelineContextSearchRule(VfxTimelineContext Context, string[] Terms);

    private static readonly TimelineEvidenceSearchRule[] EvidenceSearchRules =
    [
        new(RuntimeVfxEvidence.TriggerReferenced, ["trigger timeline", "trigger referenced"]),
        new(RuntimeVfxEvidence.AsyncTimelineReferenced, ["async timeline", "async vfx"]),
    ];

    private static readonly TimelineContextSearchRule[] ContextSearchRules =
    [
        new(VfxTimelineContext.NestedTimeline, ["nested timeline", "nested tmb"]),
        new(VfxTimelineContext.AnimationReferenced, ["animation timeline", "animation referenced"]),
        new(VfxTimelineContext.SoundReferenced, ["sound timeline", "sound referenced"]),
        new(VfxTimelineContext.NonDefaultBindPoints, ["timeline bind point", "bound timeline"]),
    ];

    public static IReadOnlyList<string> BuildSearchTerms(this VfxTimelineReferenceInfo referenceInfo)
    {
        HashSet<string> searchTerms = SearchTermUtility.CreateSet("timeline referenced", "timeline vfx");
        AppendEvidenceTerms(searchTerms, referenceInfo.NormalizedEvidence);
        AppendContextTerms(searchTerms, referenceInfo.Context);
        return SearchTermUtility.BuildStableTerms(searchTerms);
    }

    private static void AppendEvidenceTerms(HashSet<string> searchTerms, RuntimeVfxEvidence evidence)
    {
        foreach (TimelineEvidenceSearchRule rule in EvidenceSearchRules)
        {
            if (evidence.HasAny(rule.Evidence))
            {
                _ = SearchTermUtility.AddTerms(searchTerms, rule.Terms);
            }
        }
    }

    private static void AppendContextTerms(HashSet<string> searchTerms, VfxTimelineContext context)
    {
        foreach (TimelineContextSearchRule rule in ContextSearchRules)
        {
            if (context.HasAny(rule.Context))
            {
                _ = SearchTermUtility.AddTerms(searchTerms, rule.Terms);
            }
        }
    }
}

internal static class VfxTimelineContextExtensions
{
    public static bool HasAny(this VfxTimelineContext value, VfxTimelineContext flags)
        => (value & flags) != VfxTimelineContext.None;
}
