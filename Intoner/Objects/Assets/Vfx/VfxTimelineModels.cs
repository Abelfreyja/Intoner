using Intoner.Objects.Utils;

namespace Intoner.Objects.Assets;

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
        HashSet<string> searchTerms = ObjectSearchTermUtility.CreateSet("timeline referenced", "timeline vfx");
        AppendEvidenceTerms(searchTerms, referenceInfo.NormalizedEvidence);
        AppendContextTerms(searchTerms, referenceInfo.Context);
        return ObjectSearchTermUtility.BuildStableTerms(searchTerms);
    }

    private static void AppendEvidenceTerms(HashSet<string> searchTerms, RuntimeVfxEvidence evidence)
    {
        foreach (TimelineEvidenceSearchRule rule in EvidenceSearchRules)
        {
            if (evidence.HasAny(rule.Evidence))
            {
                _ = ObjectSearchTermUtility.AddTerms(searchTerms, rule.Terms);
            }
        }
    }

    private static void AppendContextTerms(HashSet<string> searchTerms, VfxTimelineContext context)
    {
        foreach (TimelineContextSearchRule rule in ContextSearchRules)
        {
            if (context.HasAny(rule.Context))
            {
                _ = ObjectSearchTermUtility.AddTerms(searchTerms, rule.Terms);
            }
        }
    }
}

internal static class VfxTimelineContextExtensions
{
    public static bool HasAny(this VfxTimelineContext value, VfxTimelineContext flags)
        => (value & flags) != VfxTimelineContext.None;
}

