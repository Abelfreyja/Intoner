namespace Intoner.Services.Configuration;

internal enum GizmoColorPreset
{
    Default,
    ImGuizmo,
    Custom,
}

internal sealed class GizmoAppearanceConfiguration
{
    public const int MinimumOpacity = 10;
    public const int MaximumOpacity = 100;

    public GizmoColorPreset Preset { get; set; }
    public int Opacity { get; set; } = MaximumOpacity;
    public RgbColor XAxis { get; set; } = new(242, 77, 77);
    public RgbColor YAxis { get; set; } = new(102, 217, 115);
    public RgbColor ZAxis { get; set; } = new(89, 153, 255);
    public RgbColor Highlight { get; set; } = new(255, 166, 51);
    public RgbColor Center { get; set; } = new(255, 255, 255);
    public RgbColor Inactive { get; set; } = new(140, 140, 148);

    public GizmoAppearanceConfiguration Copy()
        => new()
        {
            Preset = Preset,
            Opacity = Opacity,
            XAxis = XAxis,
            YAxis = YAxis,
            ZAxis = ZAxis,
            Highlight = Highlight,
            Center = Center,
            Inactive = Inactive,
        };
}
