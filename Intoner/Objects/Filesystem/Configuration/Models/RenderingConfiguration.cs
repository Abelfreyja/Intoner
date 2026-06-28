namespace Intoner.Objects.Filesystem.Configuration;

internal enum DrawMode
{
    Automatic,
    ImGui,
    Native,
}

internal enum DrawDepthMode
{
    AlwaysVisible,
    Occluded,
    InvertOccluded,
}

internal sealed class RenderingConfiguration
{
    public const int MinimumAntiAliasing = 0;
    public const int MaximumAntiAliasing = 200;
    public const int DefaultAntiAliasing = 100;
    public const int AntiAliasingStep = 5;
    public const float AntiAliasingUnitsPerPixel = 100f;

    public required DrawMode DrawMode { get; set; }
    public required DrawDepthMode DepthMode { get; set; }
    public required int AntiAliasing { get; set; }
    public required bool DrawOverGameUi { get; set; }

    public static int ClampAntiAliasing(int value)
        => Math.Clamp(value, MinimumAntiAliasing, MaximumAntiAliasing);

    public static float AntiAliasingToPixels(int value)
        => ClampAntiAliasing(value) / AntiAliasingUnitsPerPixel;

    public static RenderingConfiguration CreateDefault()
        => new()
        {
            DrawMode = DrawMode.Automatic,
            DepthMode = DrawDepthMode.AlwaysVisible,
            AntiAliasing = DefaultAntiAliasing,
            DrawOverGameUi = false,
        };

    public RenderingConfiguration Copy()
        => new()
        {
            DrawMode = DrawMode,
            DepthMode = DepthMode,
            AntiAliasing = AntiAliasing,
            DrawOverGameUi = DrawOverGameUi,
        };
}

