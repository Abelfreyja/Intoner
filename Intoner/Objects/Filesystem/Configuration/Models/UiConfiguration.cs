using System.Runtime.InteropServices;

namespace Intoner.Objects.Filesystem.Configuration;

internal sealed class UiConfiguration
{
    [StructLayout(LayoutKind.Auto)]
    public readonly record struct SplitRatios(float CatalogCreate, float PlacedInspector, float LayoutManager)
    {
        public const float Minimum = 0.20f;
        public const float Maximum = 0.80f;
        public const float DefaultRatio = 0.50f;

        public static SplitRatios Default { get; } = new(DefaultRatio, DefaultRatio, DefaultRatio);

        public SplitRatios Clamp()
            => new(ClampRatio(CatalogCreate), ClampRatio(PlacedInspector), ClampRatio(LayoutManager));

        public static float ClampRatio(float value)
            => float.IsFinite(value)
                ? Math.Clamp(value, Minimum, Maximum)
                : DefaultRatio;
    }

    public required bool ShowSplashScreenOnStartup { get; set; }
    public SplitRatios WorkspaceSplits { get; set; } = SplitRatios.Default;

    public static UiConfiguration CreateDefault()
        => new()
        {
            ShowSplashScreenOnStartup = true,
            WorkspaceSplits = SplitRatios.Default,
        };

    public UiConfiguration Copy()
        => new()
        {
            ShowSplashScreenOnStartup = ShowSplashScreenOnStartup,
            WorkspaceSplits = WorkspaceSplits,
        };
}

