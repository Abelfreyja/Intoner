using System.Runtime.InteropServices;

namespace Intoner.Objects.Assets;

[Flags]
internal enum VfxStandaloneUnsupportedReason
{
    None                     = 0,
    MissingRenderableContent = 1 << 0,
    ModelSkinParticle        = 1 << 1,
    ByNameBinder             = 1 << 2,
    ExplicitBindPointId      = 1 << 3,
    TargetOriginBinder       = 1 << 4,
}

[Flags]
internal enum VfxStandaloneContextClue
{
    None                         = 0,
    Binder                       = 1 << 0,
    TimelineBinder               = 1 << 1,
    CameraBinder                 = 1 << 2,
    TargetBindPoint              = 1 << 3,
    OriginBinder                 = 1 << 4,
    FitGroundBinder              = 1 << 5,
    DamageCircleBinder           = 1 << 6,
    FitGround                    = 1 << 7,
    CameraSpace                  = 1 << 8,
    AllStopOnHide                = 1 << 9,
    GroundProjectedParticle      = 1 << 10,
    SchedulerTrigger             = 1 << 11,
    AsyncTimelineReference       = 1 << 12,
    ScreenLayer                  = 1 << 13,
    WaterLayer                   = 1 << 14,
    TriggerWithoutScheduledItems = 1 << 15,
    StrongContextPath            = 1 << 16,
    CameraSpaceTriggerOnly       = 1 << 17,
    TimelineReference            = 1 << 18,
    TriggeredTimelineReference   = 1 << 19,
    NestedTimelineReference      = 1 << 20,
    AnimationTimelineReference   = 1 << 21,
    SoundTimelineReference       = 1 << 22,
    TimelineBindPointOverride    = 1 << 23,
}

[Flags]
internal enum VfxStandaloneUnknownReason
{
    None                = 0,
    SheetConventionOnly = 1 << 0,
}

internal enum VfxStandaloneSupportClass
{
    Unknown,
    SupportedStandalone,
    ContextRequired,
    Unsupported,
}

internal enum VfxStandaloneValidatedSupportShape
{
    None,
    DeterministicOmenFitGround,
    GroupPoseCameraOrigin,
}

[StructLayout(LayoutKind.Auto)]
internal readonly record struct VfxStandalonePolicyResult(
    VfxStandaloneSupportClass SupportClass,
    RuntimeVfxEvidence AnalysisEvidence,
    VfxStandaloneUnsupportedReason UnsupportedReasons,
    VfxStandaloneContextClue ContextClues,
    VfxStandaloneUnknownReason UnknownReasons)
{
    public bool IsSupportedStandalone
        => SupportClass == VfxStandaloneSupportClass.SupportedStandalone;
}

internal static class VfxStandalonePolicy
{
    private readonly record struct ValidatedSupportRule
    {
        public required VfxStandaloneValidatedSupportShape Shape { get; init; }
        public required RuntimeVfxEvidence RequiredEvidence { get; init; }
        public required AssetPathContract RequiredPathContracts { get; init; }
        public required int RequiredBinderCount { get; init; }
        public required int RequiredTimelineBinderCount { get; init; }
        public required VfxBinderTypes RequiredBinderTypes { get; init; }
        public required VfxBinderTypes ForbiddenBinderTypes { get; init; }
        public required VfxBinderProperties RequiredBinderProperties { get; init; }
        public required VfxBinderProperties ForbiddenBinderProperties { get; init; }
        public string? RequiredPathPrefix { get; init; }
    }

    private const VfxBinderProperties TargetBinderProperties =
        VfxBinderProperties.TargetOrigin
      | VfxBinderProperties.TargetFitGround
      | VfxBinderProperties.TargetDamageCircle
      | VfxBinderProperties.TargetByName;

    private const VfxBinderProperties FitGroundBinderProperties =
        VfxBinderProperties.CasterFitGround
      | VfxBinderProperties.TargetFitGround;

    private const VfxBinderProperties DamageCircleBinderProperties =
        VfxBinderProperties.CasterDamageCircle
      | VfxBinderProperties.TargetDamageCircle;

    private const VfxBinderProperties ByNameBinderProperties =
        VfxBinderProperties.CasterByName
      | VfxBinderProperties.TargetByName;

    private const VfxBinderProperties DeterministicOmenForbiddenBinderProperties =
        TargetBinderProperties
      | DamageCircleBinderProperties
      | ByNameBinderProperties
      | VfxBinderProperties.ExplicitBindPointId;

    private const VfxBinderProperties GroupPoseForbiddenBinderProperties =
        TargetBinderProperties
      | FitGroundBinderProperties
      | DamageCircleBinderProperties
      | ByNameBinderProperties
      | VfxBinderProperties.ExplicitBindPointId;

    private const RuntimeVfxEvidence StrongContextEvidence =
        RuntimeVfxEvidence.ActorCreate
      | RuntimeVfxEvidence.TriggerUsed
      | RuntimeVfxEvidence.Status
      | RuntimeVfxEvidence.Action
      | RuntimeVfxEvidence.ActionTimeline
      | RuntimeVfxEvidence.EmoteTimeline
      | RuntimeVfxEvidence.GimmickTimeline;

    private const VfxStandaloneContextClue ContextRequiredClues =
        VfxStandaloneContextClue.TargetBindPoint
      | VfxStandaloneContextClue.DamageCircleBinder
      | VfxStandaloneContextClue.AsyncTimelineReference
      | VfxStandaloneContextClue.TriggeredTimelineReference
      | VfxStandaloneContextClue.TriggerWithoutScheduledItems
      | VfxStandaloneContextClue.StrongContextPath
      | VfxStandaloneContextClue.CameraSpaceTriggerOnly
      | VfxStandaloneContextClue.TimelineBindPointOverride;

    private const VfxStandaloneContextClue BlockingUnknownClues =
        VfxStandaloneContextClue.Binder
      | VfxStandaloneContextClue.TimelineBinder
      | VfxStandaloneContextClue.CameraBinder
      | VfxStandaloneContextClue.OriginBinder
      | VfxStandaloneContextClue.FitGroundBinder
      | VfxStandaloneContextClue.FitGround
      | VfxStandaloneContextClue.CameraSpace
      | VfxStandaloneContextClue.AllStopOnHide
      | VfxStandaloneContextClue.ScreenLayer
      | VfxStandaloneContextClue.WaterLayer
      | VfxStandaloneContextClue.TimelineReference
      | VfxStandaloneContextClue.NestedTimelineReference
      | VfxStandaloneContextClue.AnimationTimelineReference
      | VfxStandaloneContextClue.SoundTimelineReference;

    private static readonly ValidatedSupportRule[] ValidatedSupportRules =
    [
        new()
        {
            Shape                       = VfxStandaloneValidatedSupportShape.DeterministicOmenFitGround,
            RequiredEvidence            = RuntimeVfxEvidence.Omen,
            RequiredPathContracts       = AssetPathContract.DeterministicBuilder,
            RequiredBinderCount         = 1,
            RequiredTimelineBinderCount = 0,
            RequiredBinderTypes         = VfxBinderTypes.None,
            ForbiddenBinderTypes        = VfxBinderTypes.Camera,
            RequiredBinderProperties    = VfxBinderProperties.CasterFitGround,
            ForbiddenBinderProperties   = DeterministicOmenForbiddenBinderProperties,
        },
        new()
        {
            Shape                       = VfxStandaloneValidatedSupportShape.GroupPoseCameraOrigin,
            RequiredEvidence            = RuntimeVfxEvidence.Event,
            RequiredPathContracts       = AssetPathContract.DeterministicBuilder,
            RequiredBinderCount         = 1,
            RequiredTimelineBinderCount = 0,
            RequiredBinderTypes         = VfxBinderTypes.Camera,
            ForbiddenBinderTypes        = VfxBinderTypes.None,
            RequiredBinderProperties    = VfxBinderProperties.CasterOrigin,
            ForbiddenBinderProperties   = GroupPoseForbiddenBinderProperties,
            RequiredPathPrefix          = "vfx/grouppose/eff/",
        },
    ];

    public static VfxStandalonePolicyResult Evaluate(
        string path,
        VfxAnalysis analysis,
        RuntimeVfxEvidence sourceEvidence,
        VfxTimelineContext timelineContext,
        AssetPathContract pathContracts)
    {
        VfxStandaloneUnsupportedReason unsupportedReasons = GetUnsupportedReasons(analysis);
        VfxStandaloneContextClue contextClues = GetContextClues(analysis, sourceEvidence, timelineContext);
        VfxStandaloneUnknownReason unknownReasons = GetUnknownReasons(pathContracts, sourceEvidence);

        RuntimeVfxEvidence analysisEvidence = GetAnalysisEvidence(analysis);
        VfxStandaloneSupportClass supportClass = DetermineSupportClass(
            path,
            analysis,
            sourceEvidence,
            pathContracts,
            unsupportedReasons,
            contextClues,
            unknownReasons);

        return new VfxStandalonePolicyResult(
            supportClass,
            analysisEvidence,
            unsupportedReasons,
            contextClues,
            unknownReasons);
    }

    private static RuntimeVfxEvidence GetAnalysisEvidence(VfxAnalysis analysis)
    {
        RuntimeVfxEvidence analysisEvidence = RuntimeVfxEvidence.None;
        if (analysis.HasRenderableContent)
        {
            analysisEvidence |= RuntimeVfxEvidence.Renderable;
        }

        if (analysis.HasBinder)
        {
            analysisEvidence |= RuntimeVfxEvidence.Binder;
        }

        if (analysis.HasTimelineBinder)
        {
            analysisEvidence |= RuntimeVfxEvidence.TimelineBinder;
        }

        if (analysis.SchedulerTriggerCount > 0)
        {
            analysisEvidence |= RuntimeVfxEvidence.SchedulerTrigger;
        }

        return analysisEvidence;
    }

    private static VfxStandaloneSupportClass DetermineSupportClass(
        string path,
        VfxAnalysis analysis,
        RuntimeVfxEvidence sourceEvidence,
        AssetPathContract pathContracts,
        VfxStandaloneUnsupportedReason unsupportedReasons,
        VfxStandaloneContextClue contextClues,
        VfxStandaloneUnknownReason unknownReasons)
    {
        bool hasDetachedRuntimeSignal = sourceEvidence.HasAny(RuntimeVfxEvidence.StaticCreate);
        bool hasUnsupportedReasons = unsupportedReasons != VfxStandaloneUnsupportedReason.None;
        bool hasContextRequiredClues = contextClues.HasAny(ContextRequiredClues);
        bool hasStrongContextEvidence = !hasDetachedRuntimeSignal && sourceEvidence.HasAny(StrongContextEvidence);
        bool hasUnknownReasons = unknownReasons != VfxStandaloneUnknownReason.None;
        bool hasBlockingUnknownClues = contextClues.HasAny(BlockingUnknownClues);
        bool hasValidatedSupportedShape =
            !hasDetachedRuntimeSignal
         && !hasUnknownReasons
         && ResolveValidatedSupportShape(path, analysis, sourceEvidence, pathContracts) != VfxStandaloneValidatedSupportShape.None;

        if (hasUnsupportedReasons)
        {
            return VfxStandaloneSupportClass.Unsupported;
        }

        if (hasContextRequiredClues
         || hasStrongContextEvidence)
        {
            return VfxStandaloneSupportClass.ContextRequired;
        }

        if (hasValidatedSupportedShape)
        {
            return VfxStandaloneSupportClass.SupportedStandalone;
        }

        if (!hasDetachedRuntimeSignal && (hasUnknownReasons || hasBlockingUnknownClues))
        {
            return VfxStandaloneSupportClass.Unknown;
        }

        return VfxStandaloneSupportClass.SupportedStandalone;
    }

    private static VfxStandaloneUnsupportedReason GetUnsupportedReasons(VfxAnalysis analysis)
        => FlagIf(
                !analysis.HasRenderableContent,
                VfxStandaloneUnsupportedReason.MissingRenderableContent)
         | FlagIf(
                analysis.HasModelSkinParticle,
                VfxStandaloneUnsupportedReason.ModelSkinParticle)
         | FlagIf(
                analysis.HasByNameBinder,
                VfxStandaloneUnsupportedReason.ByNameBinder)
         | FlagIf(
                analysis.HasExplicitBindPointId,
                VfxStandaloneUnsupportedReason.ExplicitBindPointId)
         | FlagIf(
                analysis.HasTargetOriginBinder,
                VfxStandaloneUnsupportedReason.TargetOriginBinder);

    private static VfxStandaloneContextClue GetContextClues(
        VfxAnalysis analysis,
        RuntimeVfxEvidence sourceEvidence,
        VfxTimelineContext timelineContext)
        => FlagIf(analysis.HasBinder, VfxStandaloneContextClue.Binder)
         | FlagIf(analysis.HasTimelineBinder, VfxStandaloneContextClue.TimelineBinder)
         | FlagIf(analysis.HasCameraBinder, VfxStandaloneContextClue.CameraBinder)
         | FlagIf(analysis.HasTargetBindPoint, VfxStandaloneContextClue.TargetBindPoint)
         | FlagIf(analysis.HasCasterOriginBinder, VfxStandaloneContextClue.OriginBinder)
         | FlagIf(analysis.HasFitGroundBinder, VfxStandaloneContextClue.FitGroundBinder)
         | FlagIf(analysis.HasDamageCircleBinder, VfxStandaloneContextClue.DamageCircleBinder)
         | FlagIf(analysis.IsFitGround, VfxStandaloneContextClue.FitGround)
         | FlagIf(analysis.IsCameraSpace, VfxStandaloneContextClue.CameraSpace)
         | FlagIf(analysis.IsAllStopOnHide, VfxStandaloneContextClue.AllStopOnHide)
         | FlagIf(analysis.HasGroundProjectedParticle, VfxStandaloneContextClue.GroundProjectedParticle)
         | FlagIf(analysis.UsesScreenLayer, VfxStandaloneContextClue.ScreenLayer)
         | FlagIf(analysis.UsesWaterLayer, VfxStandaloneContextClue.WaterLayer)
         | FlagIf(analysis.SchedulerTriggerCount > 0, VfxStandaloneContextClue.SchedulerTrigger)
         | FlagIf(analysis.HasTriggerWithoutScheduledItems, VfxStandaloneContextClue.TriggerWithoutScheduledItems)
         | FlagIf(analysis.HasStrongContextPath, VfxStandaloneContextClue.StrongContextPath)
         | FlagIf(analysis.HasCameraSpaceTriggerOnly, VfxStandaloneContextClue.CameraSpaceTriggerOnly)
         | FlagIf(sourceEvidence.HasAny(RuntimeVfxEvidence.TimelineReferenced), VfxStandaloneContextClue.TimelineReference)
         | FlagIf(sourceEvidence.HasAny(RuntimeVfxEvidence.TriggerReferenced), VfxStandaloneContextClue.TriggeredTimelineReference)
         | FlagIf(sourceEvidence.HasAny(RuntimeVfxEvidence.AsyncTimelineReferenced), VfxStandaloneContextClue.AsyncTimelineReference)
         | FlagIf(timelineContext.HasAny(VfxTimelineContext.NestedTimeline), VfxStandaloneContextClue.NestedTimelineReference)
         | FlagIf(timelineContext.HasAny(VfxTimelineContext.AnimationReferenced), VfxStandaloneContextClue.AnimationTimelineReference)
         | FlagIf(timelineContext.HasAny(VfxTimelineContext.SoundReferenced), VfxStandaloneContextClue.SoundTimelineReference)
         | FlagIf(timelineContext.HasAny(VfxTimelineContext.NonDefaultBindPoints), VfxStandaloneContextClue.TimelineBindPointOverride);

    private static VfxStandaloneUnknownReason GetUnknownReasons(
        AssetPathContract pathContracts,
        RuntimeVfxEvidence sourceEvidence)
    {
        if (sourceEvidence.HasAny(RuntimeVfxEvidence.StaticCreate))
        {
            return VfxStandaloneUnknownReason.None;
        }

        return IsSheetConventionOnly(pathContracts)
            ? VfxStandaloneUnknownReason.SheetConventionOnly
            : VfxStandaloneUnknownReason.None;
    }

    private static bool IsSheetConventionOnly(AssetPathContract pathContracts)
        => pathContracts.HasAny(AssetPathContract.SheetConvention)
         && (pathContracts & ~AssetPathContract.SheetConvention) == AssetPathContract.None;

    private static VfxStandaloneValidatedSupportShape ResolveValidatedSupportShape(
        string path,
        VfxAnalysis analysis,
        RuntimeVfxEvidence sourceEvidence,
        AssetPathContract pathContracts)
        => ValidatedSupportRules
            .Where(rule => MatchesValidatedSupportRule(path, analysis, sourceEvidence, pathContracts, rule))
            .Select(static rule => rule.Shape)
            .FirstOrDefault();

    private static bool MatchesValidatedSupportRule(
        string path,
        VfxAnalysis analysis,
        RuntimeVfxEvidence sourceEvidence,
        AssetPathContract pathContracts,
        in ValidatedSupportRule rule)
    {
        if (!sourceEvidence.HasAll(rule.RequiredEvidence)
         || !pathContracts.HasAll(rule.RequiredPathContracts)
         || analysis.BinderCount != rule.RequiredBinderCount
         || analysis.BinderFacts.TimelineCount != rule.RequiredTimelineBinderCount)
        {
            return false;
        }

        if (!string.IsNullOrEmpty(rule.RequiredPathPrefix)
         && !path.StartsWith(rule.RequiredPathPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!analysis.BinderFacts.Types.HasAll(rule.RequiredBinderTypes)
         || analysis.BinderFacts.Types.HasAny(rule.ForbiddenBinderTypes)
         || !analysis.BinderFacts.PropertyFlags.HasAll(rule.RequiredBinderProperties)
         || analysis.BinderFacts.PropertyFlags.HasAny(rule.ForbiddenBinderProperties))
        {
            return false;
        }

        return true;
    }

    private static VfxStandaloneUnsupportedReason FlagIf(bool condition, VfxStandaloneUnsupportedReason flag)
        => condition ? flag : VfxStandaloneUnsupportedReason.None;

    private static VfxStandaloneContextClue FlagIf(bool condition, VfxStandaloneContextClue flag)
        => condition ? flag : VfxStandaloneContextClue.None;
}

internal static class VfxStandaloneFlagExtensions
{
    public static bool HasAny(this VfxStandaloneUnsupportedReason value, VfxStandaloneUnsupportedReason flags)
        => (value & flags) != VfxStandaloneUnsupportedReason.None;

    public static bool HasAny(this VfxStandaloneContextClue value, VfxStandaloneContextClue flags)
        => (value & flags) != VfxStandaloneContextClue.None;

    public static bool HasAny(this VfxStandaloneUnknownReason value, VfxStandaloneUnknownReason flags)
        => (value & flags) != VfxStandaloneUnknownReason.None;
}

internal static class VfxStandaloneClassificationLabelExtensions
{
    private readonly record struct UnsupportedReasonLabelRule(VfxStandaloneUnsupportedReason Reason, string Label);
    private readonly record struct ContextClueLabelRule(VfxStandaloneContextClue Clue, string Label);
    private readonly record struct UnknownReasonLabelRule(VfxStandaloneUnknownReason Reason, string Label);

    private static readonly UnsupportedReasonLabelRule[] UnsupportedReasonRules =
    [
        new(VfxStandaloneUnsupportedReason.MissingRenderableContent, "missing renderable content"),
        new(VfxStandaloneUnsupportedReason.ModelSkinParticle, "model skin particle"),
        new(VfxStandaloneUnsupportedReason.ByNameBinder, "by name binder"),
        new(VfxStandaloneUnsupportedReason.ExplicitBindPointId, "explicit bind point id"),
        new(VfxStandaloneUnsupportedReason.TargetOriginBinder, "target origin binder"),
    ];

    private static readonly ContextClueLabelRule[] ContextClueRules =
    [
        new(VfxStandaloneContextClue.Binder, "binder"),
        new(VfxStandaloneContextClue.TimelineBinder, "timeline binder"),
        new(VfxStandaloneContextClue.CameraBinder, "camera binder"),
        new(VfxStandaloneContextClue.TargetBindPoint, "target bind point"),
        new(VfxStandaloneContextClue.OriginBinder, "origin binder"),
        new(VfxStandaloneContextClue.FitGroundBinder, "fit ground binder"),
        new(VfxStandaloneContextClue.DamageCircleBinder, "damage circle binder"),
        new(VfxStandaloneContextClue.FitGround, "fit ground"),
        new(VfxStandaloneContextClue.CameraSpace, "camera space"),
        new(VfxStandaloneContextClue.AllStopOnHide, "all stop on hide"),
        new(VfxStandaloneContextClue.GroundProjectedParticle, "ground projected particle"),
        new(VfxStandaloneContextClue.SchedulerTrigger, "scheduler trigger"),
        new(VfxStandaloneContextClue.AsyncTimelineReference, "async timeline reference"),
        new(VfxStandaloneContextClue.ScreenLayer, "screen layer"),
        new(VfxStandaloneContextClue.WaterLayer, "water layer"),
        new(VfxStandaloneContextClue.TriggerWithoutScheduledItems, "trigger without scheduled items"),
        new(VfxStandaloneContextClue.StrongContextPath, "strong context path"),
        new(VfxStandaloneContextClue.CameraSpaceTriggerOnly, "camera space trigger only"),
        new(VfxStandaloneContextClue.TimelineReference, "timeline reference"),
        new(VfxStandaloneContextClue.TriggeredTimelineReference, "triggered timeline reference"),
        new(VfxStandaloneContextClue.NestedTimelineReference, "nested timeline reference"),
        new(VfxStandaloneContextClue.AnimationTimelineReference, "animation timeline reference"),
        new(VfxStandaloneContextClue.SoundTimelineReference, "sound timeline reference"),
        new(VfxStandaloneContextClue.TimelineBindPointOverride, "timeline bind point override"),
    ];

    private static readonly UnknownReasonLabelRule[] UnknownReasonRules =
    [
        new(VfxStandaloneUnknownReason.SheetConventionOnly, "sheet convention only"),
    ];

    public static IReadOnlyList<string> ToLabels(this VfxStandaloneUnsupportedReason reasons)
        => BuildLabels(reasons, UnsupportedReasonRules);

    public static IReadOnlyList<string> ToLabels(this VfxStandaloneContextClue clues)
        => BuildLabels(clues, ContextClueRules);

    public static IReadOnlyList<string> ToLabels(this VfxStandaloneUnknownReason reasons)
        => BuildLabels(reasons, UnknownReasonRules);

    private static IReadOnlyList<string> BuildLabels(
        VfxStandaloneUnsupportedReason reasons,
        IReadOnlyList<UnsupportedReasonLabelRule> rules)
        => rules
            .Where(rule => reasons.HasAny(rule.Reason))
            .Select(static rule => rule.Label)
            .ToArray();

    private static IReadOnlyList<string> BuildLabels(
        VfxStandaloneContextClue clues,
        IReadOnlyList<ContextClueLabelRule> rules)
        => rules
            .Where(rule => clues.HasAny(rule.Clue))
            .Select(static rule => rule.Label)
            .ToArray();

    private static IReadOnlyList<string> BuildLabels(
        VfxStandaloneUnknownReason reasons,
        IReadOnlyList<UnknownReasonLabelRule> rules)
        => rules
            .Where(rule => reasons.HasAny(rule.Reason))
            .Select(static rule => rule.Label)
            .ToArray();
}

