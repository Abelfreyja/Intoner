namespace Intoner.Objects.Filesystem.Configuration;

internal sealed class AssetCaptureConfiguration
{
    public required bool EnableRuntimeCapture { get; set; }

    public static AssetCaptureConfiguration CreateDefault()
        => new()
        {
            EnableRuntimeCapture = false,
        };

    public AssetCaptureConfiguration Copy()
        => new()
        {
            EnableRuntimeCapture = EnableRuntimeCapture,
        };
}

