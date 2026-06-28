namespace Intoner.Objects.Assets;

internal enum VfxDrawLayer
{
    Unknown     = -1,
    Screen      = 0,
    BaseUpper   = 1,
    Base        = 2,
    BaseLower   = 3,
    InWater     = 4,
    BeforeCloud = 5,
    BehindCloud = 6,
    BeforeSky   = 7,
    PostUi      = 8,
    PrevUi      = 9,
    FitWater    = 10,
}

[Flags]
internal enum VfxBinderTypes : byte
{
    None         = 0,
    Point        = 1 << 0,
    Linear       = 1 << 1,
    Spline       = 1 << 2,
    Camera       = 1 << 3,
    LinearAdjust = 1 << 4,
}

[Flags]
internal enum VfxBinderProperties : ushort
{
    None                = 0,
    CasterOrigin        = 1 << 0,
    TargetOrigin        = 1 << 1,
    CasterFitGround     = 1 << 2,
    TargetFitGround     = 1 << 3,
    CasterDamageCircle  = 1 << 4,
    TargetDamageCircle  = 1 << 5,
    CasterByName        = 1 << 6,
    TargetByName        = 1 << 7,
    ExplicitBindPointId = 1 << 8,
}

[Flags]
internal enum VfxParticleTypes : ushort
{
    None       = 0,
    Parameter  = 1 << 0,
    Powder     = 1 << 1,
    Windmill   = 1 << 2,
    Line       = 1 << 3,
    Laser      = 1 << 4,
    Model      = 1 << 5,
    Polyline   = 1 << 6,
    Reserve0   = 1 << 7,
    Quad       = 1 << 8,
    Polygon    = 1 << 9,
    Decal      = 1 << 10,
    DecalRing  = 1 << 11,
    Disc       = 1 << 12,
    LightModel = 1 << 13,
    ModelSkin  = 1 << 14,
    Dissolve   = 1 << 15,
}

[Flags]
internal enum VfxAnalysisFeatures : ushort
{
    None              = 0,
    FitGround         = 1 << 0,
    CameraSpace       = 1 << 1,
    AllStopOnHide     = 1 << 2,
    UsesWaterLayer    = 1 << 3,
    UsesScreenLayer   = 1 << 4,
    StrongContextPath = 1 << 5,
}

internal sealed record VfxBinderFacts(
    int Count,
    int TimelineCount,
    VfxBinderTypes Types,
    VfxBinderProperties PropertyFlags)
{
    private const VfxBinderProperties TargetPropertyFlags =
        VfxBinderProperties.TargetOrigin
      | VfxBinderProperties.TargetFitGround
      | VfxBinderProperties.TargetDamageCircle
      | VfxBinderProperties.TargetByName;

    private const VfxBinderProperties OriginPropertyFlags =
        VfxBinderProperties.CasterOrigin
      | VfxBinderProperties.TargetOrigin;

    private const VfxBinderProperties FitGroundPropertyFlags =
        VfxBinderProperties.CasterFitGround
      | VfxBinderProperties.TargetFitGround;

    private const VfxBinderProperties DamageCirclePropertyFlags =
        VfxBinderProperties.CasterDamageCircle
      | VfxBinderProperties.TargetDamageCircle;

    private const VfxBinderProperties ByNamePropertyFlags =
        VfxBinderProperties.CasterByName
      | VfxBinderProperties.TargetByName;

    public bool HasTimelineBinder
        => TimelineCount > 0;

    public bool HasCameraBinder
        => Types.HasAny(VfxBinderTypes.Camera);

    public bool HasTargetBindPoint
        => PropertyFlags.HasAny(TargetPropertyFlags);

    public bool HasOriginBinder
        => PropertyFlags.HasAny(OriginPropertyFlags);

    public bool HasCasterOriginBinder
        => PropertyFlags.HasAny(VfxBinderProperties.CasterOrigin);

    public bool HasTargetOriginBinder
        => PropertyFlags.HasAny(VfxBinderProperties.TargetOrigin);

    public bool HasFitGroundBinder
        => PropertyFlags.HasAny(FitGroundPropertyFlags);

    public bool HasCasterFitGroundBinder
        => PropertyFlags.HasAny(VfxBinderProperties.CasterFitGround);

    public bool HasTargetFitGroundBinder
        => PropertyFlags.HasAny(VfxBinderProperties.TargetFitGround);

    public bool HasDamageCircleBinder
        => PropertyFlags.HasAny(DamageCirclePropertyFlags);

    public bool HasByNameBinder
        => PropertyFlags.HasAny(ByNamePropertyFlags);

    public bool HasExplicitBindPointId
        => PropertyFlags.HasAny(VfxBinderProperties.ExplicitBindPointId);
}

internal sealed record VfxAnalysis(
    int SchedulerCount,
    int SchedulerItemCount,
    int SchedulerTriggerCount,
    int TimelineCount,
    int EmitterCount,
    int ParticleCount,
    int ModelCount,
    VfxDrawLayer DrawLayer,
    VfxBinderFacts BinderFacts,
    VfxParticleTypes ParticleTypes,
    VfxAnalysisFeatures FeatureFlags)
{
    public int BinderCount
        => BinderFacts.Count;

    public bool HasTimelineBinder
        => BinderFacts.HasTimelineBinder;

    public bool HasCameraBinder
        => BinderFacts.HasCameraBinder;

    public bool HasTargetBindPoint
        => BinderFacts.HasTargetBindPoint;

    public bool HasOriginBinder
        => BinderFacts.HasOriginBinder;

    public bool HasCasterOriginBinder
        => BinderFacts.HasCasterOriginBinder;

    public bool HasTargetOriginBinder
        => BinderFacts.HasTargetOriginBinder;

    public bool HasFitGroundBinder
        => BinderFacts.HasFitGroundBinder;

    public bool HasCasterFitGroundBinder
        => BinderFacts.HasCasterFitGroundBinder;

    public bool HasTargetFitGroundBinder
        => BinderFacts.HasTargetFitGroundBinder;

    public bool HasDamageCircleBinder
        => BinderFacts.HasDamageCircleBinder;

    public bool HasByNameBinder
        => BinderFacts.HasByNameBinder;

    public bool HasExplicitBindPointId
        => BinderFacts.HasExplicitBindPointId;

    public bool IsFitGround
        => FeatureFlags.HasAny(VfxAnalysisFeatures.FitGround);

    public bool IsCameraSpace
        => FeatureFlags.HasAny(VfxAnalysisFeatures.CameraSpace);

    public bool IsAllStopOnHide
        => FeatureFlags.HasAny(VfxAnalysisFeatures.AllStopOnHide);

    public bool UsesWaterLayer
        => FeatureFlags.HasAny(VfxAnalysisFeatures.UsesWaterLayer);

    public bool UsesScreenLayer
        => FeatureFlags.HasAny(VfxAnalysisFeatures.UsesScreenLayer);

    public bool HasStrongContextPath
        => FeatureFlags.HasAny(VfxAnalysisFeatures.StrongContextPath);

    public bool HasModelSkinParticle
        => ParticleTypes.HasAny(VfxParticleTypes.ModelSkin);

    public bool HasGroundProjectedParticle
        => ParticleTypes.HasAny(
            VfxParticleTypes.Decal
          | VfxParticleTypes.DecalRing
          | VfxParticleTypes.Disc);

    public bool HasBinder
        => BinderCount > 0;

    public bool HasTriggerWithoutScheduledItems
        => SchedulerTriggerCount > 0
         && SchedulerItemCount == 0;

    public bool HasCameraSpaceTriggerOnly
        => IsCameraSpace
         && HasTriggerWithoutScheduledItems;

    public bool HasRenderableContent
        => EmitterCount > 0
         || ParticleCount > 0
         || ModelCount > 0;
}

internal static class VfxAnalysisFlagExtensions
{
    public static bool HasAny(this VfxBinderTypes value, VfxBinderTypes flags)
        => (value & flags) != VfxBinderTypes.None;

    public static bool HasAll(this VfxBinderTypes value, VfxBinderTypes flags)
        => (value & flags) == flags;

    public static bool HasAny(this VfxBinderProperties value, VfxBinderProperties flags)
        => (value & flags) != VfxBinderProperties.None;

    public static bool HasAll(this VfxBinderProperties value, VfxBinderProperties flags)
        => (value & flags) == flags;

    public static bool HasAny(this VfxParticleTypes value, VfxParticleTypes flags)
        => (value & flags) != VfxParticleTypes.None;

    public static bool HasAll(this VfxParticleTypes value, VfxParticleTypes flags)
        => (value & flags) == flags;

    public static bool HasAny(this VfxAnalysisFeatures value, VfxAnalysisFeatures flags)
        => (value & flags) != VfxAnalysisFeatures.None;

    public static bool HasAll(this VfxAnalysisFeatures value, VfxAnalysisFeatures flags)
        => (value & flags) == flags;
}

