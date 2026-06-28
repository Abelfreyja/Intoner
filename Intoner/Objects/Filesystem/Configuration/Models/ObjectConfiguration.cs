namespace Intoner.Objects.Filesystem.Configuration;

internal sealed class ObjectConfiguration
{
    public const int CurrentVersion = 2;

    public required int Version { get; set; }
    public required AssetCaptureConfiguration AssetCapture { get; set; }
    public required HousingCullingConfiguration HousingCulling { get; set; }
    public required HousingModeConfiguration HousingMode { get; set; }
    public required LayoutConfiguration Layouts { get; set; }
    public required LayoutAutoSaveConfiguration LayoutAutoSave { get; set; }
    public required LoggingConfiguration Logging { get; set; }
    public required RenderingConfiguration Rendering { get; set; }
    public required UiConfiguration Ui { get; set; }

    public static ObjectConfiguration CreateDefault()
        => new()
        {
            Version = CurrentVersion,
            AssetCapture = AssetCaptureConfiguration.CreateDefault(),
            HousingCulling = HousingCullingConfiguration.CreateDefault(),
            HousingMode = HousingModeConfiguration.CreateDefault(),
            Layouts = LayoutConfiguration.CreateDefault(),
            LayoutAutoSave = LayoutAutoSaveConfiguration.CreateDefault(),
            Logging = LoggingConfiguration.CreateDefault(),
            Rendering = RenderingConfiguration.CreateDefault(),
            Ui = UiConfiguration.CreateDefault(),
        };

    public ObjectConfiguration Copy()
        => new()
        {
            Version = Version,
            AssetCapture = AssetCapture.Copy(),
            HousingCulling = HousingCulling.Copy(),
            HousingMode = HousingMode.Copy(),
            Layouts = Layouts.Copy(),
            LayoutAutoSave = LayoutAutoSave.Copy(),
            Logging = Logging.Copy(),
            Rendering = Rendering.Copy(),
            Ui = Ui.Copy(),
        };
}

