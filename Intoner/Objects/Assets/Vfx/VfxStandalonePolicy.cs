using System.Runtime.InteropServices;

namespace Intoner.Objects.Assets;

internal enum VfxStandaloneSupportClass
{
    Unknown,
    SupportedStandalone,
    ContextRequired,
    Unsupported,
}

[StructLayout(LayoutKind.Auto)]
internal readonly record struct VfxStandalonePolicyResult(
    VfxStandaloneSupportClass SupportClass,
    RuntimeVfxEvidence AnalysisEvidence);

internal static class VfxStandalonePolicy
{
    [Flags]
    private enum ContextClue
    {
        None                         = 0,
        Binder                       = 1 << 0,
        TimelineBinder               = 1 << 1,
        CameraBinder                 = 1 << 2,
        EndpointBinder               = 1 << 3,
        TargetBindPoint              = 1 << 4,
        OriginBinder                 = 1 << 5,
        FitGroundBinder              = 1 << 6,
        DamageCircleBinder           = 1 << 7,
        FitGround                    = 1 << 8,
        CameraSpace                  = 1 << 9,
        AllStopOnHide                = 1 << 10,
        AsyncTimelineReference       = 1 << 11,
        ScreenLayer                  = 1 << 12,
        WaterLayer                   = 1 << 13,
        TriggerWithoutScheduledItems = 1 << 14,
        StrongContextPath            = 1 << 15,
        TimelineReference            = 1 << 16,
        TriggeredTimelineReference   = 1 << 17,
        NestedTimelineReference      = 1 << 18,
        AnimationTimelineReference   = 1 << 19,
        SoundTimelineReference       = 1 << 20,
        TimelineBindPointOverride    = 1 << 21,
    }

    private const VfxBinderProperties TargetBinderProperties =
        VfxBinderProperties.TargetOrigin
      | VfxBinderProperties.TargetFitGround
      | VfxBinderProperties.TargetDamageCircle
      | VfxBinderProperties.TargetByName;

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

    private const RuntimeVfxEvidence StrongContextEvidence =
        RuntimeVfxEvidence.ActorCreate
      | RuntimeVfxEvidence.TriggerUsed
      | RuntimeVfxEvidence.Status
      | RuntimeVfxEvidence.Action
      | RuntimeVfxEvidence.ActionTimeline
      | RuntimeVfxEvidence.EmoteTimeline
      | RuntimeVfxEvidence.GimmickTimeline;

    private const ContextClue ContextRequiredClues =
        ContextClue.TargetBindPoint
      | ContextClue.EndpointBinder
      | ContextClue.DamageCircleBinder
      | ContextClue.AsyncTimelineReference
      | ContextClue.TriggeredTimelineReference
      | ContextClue.TriggerWithoutScheduledItems
      | ContextClue.StrongContextPath
      | ContextClue.TimelineBindPointOverride;

    private const ContextClue BlockingUnknownClues =
        ContextClue.Binder
      | ContextClue.TimelineBinder
      | ContextClue.CameraBinder
      | ContextClue.OriginBinder
      | ContextClue.FitGroundBinder
      | ContextClue.FitGround
      | ContextClue.CameraSpace
      | ContextClue.AllStopOnHide
      | ContextClue.ScreenLayer
      | ContextClue.WaterLayer
      | ContextClue.TimelineReference
      | ContextClue.NestedTimelineReference
      | ContextClue.AnimationTimelineReference
      | ContextClue.SoundTimelineReference;

    public static VfxStandalonePolicyResult Evaluate(
        VfxAnalysis analysis,
        KnownVfxFamily familyHint,
        RuntimeVfxEvidence sourceEvidence,
        VfxTimelineContext timelineContext,
        AssetPathContract pathContracts)
    {
        bool canRewriteForStandaloneSpawn = AvfxRewritePolicy.CanRewriteForStandaloneSpawn(analysis);
        bool isUnsupported = IsUnsupported(analysis, canRewriteForStandaloneSpawn);
        ContextClue contextClues = GetContextClues(analysis, sourceEvidence, timelineContext);
        bool hasUnknownSource = !sourceEvidence.HasAny(RuntimeVfxEvidence.StaticCreate)
                             && IsSheetConventionOnly(pathContracts);

        RuntimeVfxEvidence analysisEvidence = GetAnalysisEvidence(analysis, canRewriteForStandaloneSpawn);
        VfxStandaloneSupportClass supportClass = DetermineSupportClass(
            analysis,
            familyHint,
            sourceEvidence,
            pathContracts,
            isUnsupported,
            contextClues,
            hasUnknownSource,
            canRewriteForStandaloneSpawn);

        return new VfxStandalonePolicyResult(
            supportClass,
            analysisEvidence);
    }

    private static RuntimeVfxEvidence GetAnalysisEvidence(VfxAnalysis analysis, bool canRewriteForStandaloneSpawn)
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

        if (canRewriteForStandaloneSpawn)
        {
            analysisEvidence |= RuntimeVfxEvidence.StandaloneRewriteCandidate;
        }

        return analysisEvidence;
    }

    private static VfxStandaloneSupportClass DetermineSupportClass(
        VfxAnalysis analysis,
        KnownVfxFamily familyHint,
        RuntimeVfxEvidence sourceEvidence,
        AssetPathContract pathContracts,
        bool isUnsupported,
        ContextClue contextClues,
        bool hasUnknownSource,
        bool canRewriteForStandaloneSpawn)
    {
        bool hasDetachedRuntimeSignal = sourceEvidence.HasAny(RuntimeVfxEvidence.StaticCreate);
        bool hasContextRequiredClues = HasAny(contextClues, GetContextRequiredClues(analysis, canRewriteForStandaloneSpawn));
        bool hasStrongContextEvidence = !hasDetachedRuntimeSignal
                                     && !canRewriteForStandaloneSpawn
                                     && sourceEvidence.HasAny(StrongContextEvidence);
        bool hasBlockingUnknownClues = HasAny(contextClues, GetBlockingUnknownClues(analysis, canRewriteForStandaloneSpawn));
        bool isKnownGroupPoseScreenEffect = IsKnownGroupPoseScreenEffect(
            analysis,
            familyHint,
            sourceEvidence,
            pathContracts);
        bool isValidatedOmenFitGround =
            !hasDetachedRuntimeSignal
         && !hasUnknownSource
         && IsValidatedOmenFitGround(
                analysis,
                sourceEvidence,
                pathContracts);

        if (isKnownGroupPoseScreenEffect)
        {
            return VfxStandaloneSupportClass.SupportedStandalone;
        }

        if (isUnsupported)
        {
            return VfxStandaloneSupportClass.Unsupported;
        }

        if (hasContextRequiredClues
         || hasStrongContextEvidence)
        {
            return VfxStandaloneSupportClass.ContextRequired;
        }

        if (isValidatedOmenFitGround)
        {
            return VfxStandaloneSupportClass.SupportedStandalone;
        }

        if (!hasDetachedRuntimeSignal && (hasUnknownSource || hasBlockingUnknownClues))
        {
            return VfxStandaloneSupportClass.Unknown;
        }

        return VfxStandaloneSupportClass.SupportedStandalone;
    }

    private static ContextClue GetContextRequiredClues(VfxAnalysis analysis, bool canRewriteForStandaloneSpawn)
    {
        ContextClue clues = ContextRequiredClues;
        if (analysis.HasBoundTimelineItems || canRewriteForStandaloneSpawn)
        {
            clues &= ~ContextClue.TimelineBindPointOverride;
        }

        if (canRewriteForStandaloneSpawn)
        {
            clues &= ~ContextClue.TargetBindPoint;
        }

        return clues;
    }

    private static ContextClue GetBlockingUnknownClues(VfxAnalysis analysis, bool canRewriteForStandaloneSpawn)
    {
        ContextClue clues = BlockingUnknownClues;
        if (analysis.HasBoundTimelineItems || canRewriteForStandaloneSpawn)
        {
            clues &= ~ContextClue.TimelineReference;
        }

        if (canRewriteForStandaloneSpawn)
        {
            clues &= ~(ContextClue.Binder
                     | ContextClue.TimelineBinder
                     | ContextClue.OriginBinder);
        }

        return clues;
    }

    private static bool IsUnsupported(VfxAnalysis analysis, bool canRewriteForStandaloneSpawn)
    {
        bool endpointContextRequired = analysis.HasEndpointBinder;
        return !analysis.HasRenderableContent
            || analysis.HasModelSkinParticle
            || (!canRewriteForStandaloneSpawn
             && !endpointContextRequired
             && (analysis.HasByNameBinder
              || analysis.HasExplicitBindPointId
              || analysis.HasTargetOriginBinder));
    }

    private static ContextClue GetContextClues(
        VfxAnalysis analysis,
        RuntimeVfxEvidence sourceEvidence,
        VfxTimelineContext timelineContext)
        => FlagIf(analysis.HasBinder, ContextClue.Binder)
         | FlagIf(analysis.HasTimelineBinder, ContextClue.TimelineBinder)
         | FlagIf(analysis.HasCameraBinder, ContextClue.CameraBinder)
         | FlagIf(analysis.HasEndpointBinder, ContextClue.EndpointBinder)
         | FlagIf(analysis.HasTargetBindPoint, ContextClue.TargetBindPoint)
         | FlagIf(analysis.HasCasterOriginBinder, ContextClue.OriginBinder)
         | FlagIf(analysis.HasFitGroundBinder, ContextClue.FitGroundBinder)
         | FlagIf(analysis.HasDamageCircleBinder, ContextClue.DamageCircleBinder)
         | FlagIf(analysis.IsFitGround, ContextClue.FitGround)
         | FlagIf(analysis.IsCameraSpace, ContextClue.CameraSpace)
         | FlagIf(analysis.IsAllStopOnHide, ContextClue.AllStopOnHide)
         | FlagIf(analysis.UsesScreenLayer, ContextClue.ScreenLayer)
         | FlagIf(analysis.UsesWaterLayer, ContextClue.WaterLayer)
         | FlagIf(analysis.HasTriggerWithoutScheduledItems, ContextClue.TriggerWithoutScheduledItems)
         | FlagIf(analysis.HasStrongContextPath, ContextClue.StrongContextPath)
         | FlagIf(sourceEvidence.HasAny(RuntimeVfxEvidence.TimelineReferenced), ContextClue.TimelineReference)
         | FlagIf(sourceEvidence.HasAny(RuntimeVfxEvidence.TriggerReferenced), ContextClue.TriggeredTimelineReference)
         | FlagIf(sourceEvidence.HasAny(RuntimeVfxEvidence.AsyncTimelineReferenced), ContextClue.AsyncTimelineReference)
         | FlagIf(timelineContext.HasAny(VfxTimelineContext.NestedTimeline), ContextClue.NestedTimelineReference)
         | FlagIf(timelineContext.HasAny(VfxTimelineContext.AnimationReferenced), ContextClue.AnimationTimelineReference)
         | FlagIf(timelineContext.HasAny(VfxTimelineContext.SoundReferenced), ContextClue.SoundTimelineReference)
         | FlagIf(timelineContext.HasAny(VfxTimelineContext.NonDefaultBindPoints), ContextClue.TimelineBindPointOverride);

    private static bool IsSheetConventionOnly(AssetPathContract pathContracts)
        => pathContracts.HasAny(AssetPathContract.SheetConvention)
         && (pathContracts & ~AssetPathContract.SheetConvention) == AssetPathContract.None;

    private static bool IsKnownGroupPoseScreenEffect(
        VfxAnalysis analysis,
        KnownVfxFamily familyHint,
        RuntimeVfxEvidence sourceEvidence,
        AssetPathContract pathContracts)
        => familyHint.HasAll(KnownVfxFamily.GroupPose)
        && sourceEvidence.HasAll(RuntimeVfxEvidence.Event)
        && pathContracts.HasAll(AssetPathContract.DeterministicBuilder)
        && analysis.HasRenderableContent
        && analysis.UsesScreenLayer
        && !analysis.HasModelSkinParticle;

    private static bool IsValidatedOmenFitGround(
        VfxAnalysis analysis,
        RuntimeVfxEvidence sourceEvidence,
        AssetPathContract pathContracts)
        => sourceEvidence.HasAll(RuntimeVfxEvidence.Omen)
        && pathContracts.HasAll(AssetPathContract.DeterministicBuilder)
        && analysis.BinderCount == 1
        && analysis.BinderFacts.TimelineCount == 0
        && !analysis.BinderFacts.Types.HasAny(VfxBinderTypes.Camera)
        && analysis.BinderFacts.PropertyFlags.HasAll(VfxBinderProperties.CasterFitGround)
        && !analysis.BinderFacts.PropertyFlags.HasAny(DeterministicOmenForbiddenBinderProperties);

    private static ContextClue FlagIf(bool condition, ContextClue flag)
        => condition ? flag : ContextClue.None;

    private static bool HasAny(ContextClue value, ContextClue flags)
        => (value & flags) != ContextClue.None;
}
