namespace Intoner.Objects.Assets;

internal static class AvfxRewritePolicy
{
    private const VfxBinderProperties FitGroundBinderProperties =
        VfxBinderProperties.CasterFitGround
      | VfxBinderProperties.TargetFitGround;

    private const VfxBinderProperties DamageCircleBinderProperties =
        VfxBinderProperties.CasterDamageCircle
      | VfxBinderProperties.TargetDamageCircle;

    private const VfxBinderProperties UnsupportedStandaloneBinderProperties =
        FitGroundBinderProperties
      | DamageCircleBinderProperties;

    public static bool CanRewriteForStandaloneSpawn(VfxAnalysis analysis)
    {
        if (!analysis.HasBoundTimelineItems
         || !analysis.HasRenderableContent
         || analysis.HasModelSkinParticle
         || analysis.HasCameraBinder
         || analysis.HasEndpointBinder
         || analysis.IsFitGround
         || analysis.IsCameraSpace
         || analysis.IsAllStopOnHide
         || analysis.UsesScreenLayer
         || analysis.UsesWaterLayer)
        {
            return false;
        }

        return !analysis.BinderFacts.PropertyFlags.HasAny(UnsupportedStandaloneBinderProperties);
    }

    public static Capability FromRewriteCounts(
        int timelineBindPointCount,
        int timelineBinderCount,
        int binderPropertyCount)
    {
        Capability capabilities = Capability.None;
        if (timelineBindPointCount > 0)
        {
            capabilities |= Capability.TimelineBindPoint;
        }

        if (timelineBinderCount > 0)
        {
            capabilities |= Capability.TimelineBinder;
        }

        if (binderPropertyCount > 0)
        {
            capabilities |= Capability.BinderAttachPoint;
        }

        return capabilities;
    }

    [Flags]
    public enum Capability
    {
        None              = 0,
        TimelineBindPoint = 1 << 0,
        TimelineBinder    = 1 << 1,
        BinderAttachPoint = 1 << 2,
    }
}
