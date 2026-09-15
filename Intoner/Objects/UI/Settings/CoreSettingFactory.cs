using Intoner.Objects.Runtime;
using Intoner.Objects.UI.Settings.Components;
using Intoner.Services.Configuration;
using Microsoft.Extensions.Logging;
using System.Runtime.InteropServices;

namespace Intoner.Objects.UI.Settings;

internal sealed partial class CoreSettingFactory
{
    private readonly IIntonerConfigurationService _configuration;
    private readonly IObjectHousingModePolicy      _housingMode;
    private readonly IntonerThemeStyle             _themeStyle;

    private static readonly ChoiceOption<ObjectWorkspaceMode>[] WorkspaceModeOptions =
    [
        new(
            ObjectWorkspaceMode.Normal,
            "Normal",
            "normal freeform unrestricted all objects",
            "Normal freeform and unrestricted mode."),
        new(
            ObjectWorkspaceMode.Housing,
            "Housing",
            "housing house furniture limit tabletop attachment floating prevention",
            "Mode to mimic XIV's native housing limits and restrictions. (For house designing purposes mostly)"),
    ];

    private static readonly ChoiceOption<ObjectHousingSize>[] HousingSizeOptions =
    [
        new(ObjectHousingSize.Apartment, "Apartment (150)", "apartment private chamber room 150"),
        new(ObjectHousingSize.Small, "Small (300/40)", "small cottage 300 40"),
        new(ObjectHousingSize.Medium, "Medium (450/60)", "medium house 450 60"),
        new(ObjectHousingSize.Large, "Large (600/80)", "large mansion 600 80"),
    ];

    private static readonly ChoiceOption<ObjectHousingArea>[] HousingAreaOptions =
    [
        new(
            ObjectHousingArea.Indoor,
            "Interior",
            "indoor interior inside",
            "Uses indoor furnishing limits and restrictions."),
        new(
            ObjectHousingArea.Outdoor,
            "Exterior",
            "outdoor exterior yard garden",
            "Uses outdoor furnishing limits and restrictions. (Apartments/Chambers are indoor only)"),
    ];

    private static readonly ChoiceOption<DrawMode>[] DrawModeOptions =
    [
        new(
            DrawMode.Automatic,
            "Automatic",
            "automatic native imgui fallback",
            "Uses Native first and falls back to ImGui when needed."),
        new(
            DrawMode.ImGui,
            "ImGui",
            "ui imgui dalamud fallback safe",
            "Draws object widgets with ImGui."),
        new(
            DrawMode.Native,
            "Native",
            "xiv native game renderer",
            "Draws object widgets with native game layer rendering."),
    ];

    private static readonly ChoiceOption<DrawDepthMode>[] DrawDepthModeOptions =
    [
        new(
            DrawDepthMode.AlwaysVisible,
            "Always Visible",
            "always visible show through walls",
            "Keeps object widget draws visible through any scene geometry, including objects."),
        new(
            DrawDepthMode.Occluded,
            "Occluded",
            "hide behind walls scene occlusion depth",
            "Lets scene geometry occlude object widget draws."),
        new(
            DrawDepthMode.InvertOccluded,
            "Invert Occluded",
            "invert color hidden behind walls scene geometry occlusion depth",
            "Inverts object widget draw color where scene geometry would hide it."),
    ];

    private static readonly ChoiceOption<SceneListRowSize>[] PlacedListRowSizeOptions =
    [
        new(
            SceneListRowSize.Large,
            "Large",
            "larger detailed rows",
            "Shows item details and full status labels."),
        new(
            SceneListRowSize.Compact,
            "Compact",
            "compact small dense rows",
            "Fits more rows into the list, sacrificing miscellaneous info."),
    ];

    private static readonly ChoiceOption<ColorChoice>[] ColorOptions =
    [
        new(
            new ColorChoice(UiColorPreset.Purple),
            "Purple",
            "purple",
            "Just the classic purple used throughout the initial development."),
        new(
            new ColorChoice(UiColorPreset.Pink),
            "Pink",
            "pink",
            "The color intended by the developer..."),
        new(
            new ColorChoice(null),
            "Custom",
            "custom color",
            "Choose your own color!"),
    ];

    private static readonly ChoiceOption<LogLevel>[] DalamudLogLevelOptions =
    [
        new(LogLevel.Trace, "Trace", "trace verbose detailed diagnostics"),
        new(LogLevel.Debug, "Debug", "debug diagnostics development"),
        new(LogLevel.Information, "Information", "information normal default info"),
        new(LogLevel.Warning, "Warning", "warning warn problems only"),
        new(LogLevel.Error, "Error", "error failures only"),
        new(LogLevel.Critical, "Critical", "critical fatal crashes only"),
    ];

    public CoreSettingFactory(
        IIntonerConfigurationService configuration,
        IObjectHousingModePolicy housingMode,
        IntonerThemeStyle themeStyle)
    {
        _configuration = configuration;
        _housingMode    = housingMode;
        _themeStyle     = themeStyle;
    }

    public ISettingEntry CreateWorkspaceMode()
        => new ChoiceSettingEntry<ObjectWorkspaceMode>(
            new SettingDefinition(
                "workspaceMode",
                "Workspace Mode",
                "Switches Intoner between Normal and Housing editing modes.",
                "workspace mode normal housing house"),
            WorkspaceModeOptions,
            () => _housingMode.GetState().Mode,
            value => _configuration.TryUpdate(
                configuration => configuration.HousingMode.Mode = value),
            style: ChoiceRowStyle.Segmented);

    public ISettingEntry CreateHousingSize()
        => new ChoiceSettingEntry<ObjectHousingSize>(
            new SettingDefinition(
                "housingSize",
                "House Size",
                "Selects the set furniture limit depending on size of the housing.",
                "housing size apartment small medium large furniture limit"),
            HousingSizeOptions,
            () => _housingMode.GetState().Size,
            value => _configuration.TryUpdate(configuration =>
            {
                configuration.HousingMode.Size = value;
                if (value == ObjectHousingSize.Apartment)
                {
                    configuration.HousingMode.Area = ObjectHousingArea.Indoor;
                }
            }),
            isEnabled: () => _housingMode.GetState().IsHousingMode);

    public ISettingEntry CreateHousingArea()
        => new ChoiceSettingEntry<ObjectHousingArea>(
            new SettingDefinition(
                "housingArea",
                "Housing Area",
                "Chooses which housing area is currently being edited.",
                "housing area indoor outdoor interior exterior yard garden"),
            HousingAreaOptions,
            () => _housingMode.GetState().Area,
            value => _configuration.TryUpdate(configuration =>
            {
                configuration.HousingMode.Area = configuration.HousingMode.Size == ObjectHousingSize.Apartment
                    ? ObjectHousingArea.Indoor
                    : value;
            }),
            value => value != ObjectHousingArea.Outdoor
                  || _housingMode.GetState().Size != ObjectHousingSize.Apartment,
            () => _housingMode.GetState().IsHousingMode,
            style: ChoiceRowStyle.Segmented);

    public ISettingEntry CreateRuntimeAssetCapture()
        => new ToggleSettingEntry(
            new SettingDefinition(
                "runtimeAssetCapture",
                "Runtime Asset Capture",
                "Allows the object asset index to observe runtime loaded assets after plugin startup.",
                "asset capture runtime catalog cache"),
            () => _configuration.Current.AssetCapture.EnableRuntimeCapture,
            value => _configuration.TryUpdate(
                configuration => configuration.AssetCapture.EnableRuntimeCapture = value),
            static () => new SettingStatus("Startup", ThemeColors.TextDisabled));

    public ISettingEntry CreateLayoutAutosaveEnabled()
        => new ToggleSettingEntry(
            new SettingDefinition(
                "layoutAutosaveEnabled",
                "Enable Autosave",
                "Writes a separate temporary recovery layout for the current object workspace.",
                "layout autosave auto save recovery temporary"),
            () => _configuration.Current.LayoutAutoSave.Enabled,
            value => _configuration.TryUpdate(
                configuration => configuration.LayoutAutoSave.Enabled = value),
            ResolveLayoutAutosaveStatus);

    public ISettingEntry CreateLayoutAutosaveInterval()
        => new IntegerSettingEntry(
            new SettingDefinition(
                "layoutAutosaveInterval",
                "Autosave Interval",
                "Controls how often the current object workspace recovery layout is written.",
                "layout autosave interval time seconds minutes recovery"),
            new IntegerSettingRange(
                LayoutAutoSaveConfiguration.MinimumIntervalSeconds,
                LayoutAutoSaveConfiguration.MaximumIntervalSeconds,
                15),
            () => _configuration.Current.LayoutAutoSave.IntervalSeconds,
            value => _configuration.TryUpdate(
                configuration => configuration.LayoutAutoSave.IntervalSeconds = value),
            static value => $"Every {FormatInterval(value)}",
            static value => FormatInterval(value),
            isEnabled: () => _configuration.Current.LayoutAutoSave.Enabled);

    public ISettingEntry CreateSplashScreenOnStartup()
        => new ToggleSettingEntry(
            new SettingDefinition(
                "showSplashScreenOnStartup",
                "Show On Startup",
                "Shows the splash screen automatically when Intoner finishes loading.",
                "ui interface splash screen startup start open"),
            () => _configuration.Current.Ui.ShowSplashScreenOnStartup,
            value => _configuration.TryUpdate(
                configuration => configuration.Ui.ShowSplashScreenOnStartup = value),
            ResolveSplashScreenStartupStatus);

    public ISettingEntry CreatePrimaryColor()
        => new ChoiceSettingEntry<ColorChoice>(
            new SettingDefinition(
                "primaryColor",
                "Primary Color",
                "Choose a preset or a custom color.",
                "ui theme color preset custom purple pink"),
            ColorOptions,
            ReadPrimaryColor,
            WritePrimaryColor,
            style: ChoiceRowStyle.Segmented);

    public ISettingEntry CreateCustomColor()
        => new ColorSettingEntry(
            new SettingDefinition(
                "customColor",
                "Custom Color",
                "A set custom UI color, overiding the preset color.",
                "ui theme custom color"),
            () => _configuration.Current.Ui.Theme.PrimaryColor.CustomColor.ToNormalizedVector3(),
            value => _configuration.TryUpdate(
                configuration => configuration.Ui.Theme.PrimaryColor.CustomColor = RgbColor.FromNormalizedVector3(value)),
            previewValue: value => _themeStyle.PreviewPrimaryColor(RgbColor.FromNormalizedVector3(value)))
            .VisibleWhen(() => _configuration.Current.Ui.Theme.PrimaryColor.Source == UiColorSource.Custom);

    public ISettingEntry CreateHideWithGameUi()
        => new ToggleSettingEntry(
            new SettingDefinition(
                "hideWithGameUi",
                "Hide Intoner When UI Is Hidden",
                "Hide the Intoner window when the game UI is hidden.",
                "ui interface window visibility hide hidden game hud"),
            () => _configuration.Current.Ui.HideWithGameUi,
            value => _configuration.TryUpdate(
                configuration => configuration.Ui.HideWithGameUi = value),
            () => ResolveWindowVisibilityStatus(_configuration.Current.Ui.HideWithGameUi));

    public ISettingEntry CreateHideInCutscenes()
        => new ToggleSettingEntry(
            new SettingDefinition(
                "hideInCutscenes",
                "Hide Intoner in Cutscenes",
                "Hide the Intoner window while you are watching a cutscene.",
                "ui interface window visibility hide hidden cutscene"),
            () => _configuration.Current.Ui.HideInCutscenes,
            value => _configuration.TryUpdate(
                configuration => configuration.Ui.HideInCutscenes = value),
            () => ResolveWindowVisibilityStatus(_configuration.Current.Ui.HideInCutscenes));

    public ISettingEntry CreateHideInGpose()
        => new ToggleSettingEntry(
            new SettingDefinition(
                "hideInGpose",
                "Hide Intoner in GPose",
                "Hide the Intoner window while you are in GPose.",
                "ui interface window visibility hide hidden gpose group pose"),
            () => _configuration.Current.Ui.HideInGpose,
            value => _configuration.TryUpdate(
                configuration => configuration.Ui.HideInGpose = value),
            () => ResolveWindowVisibilityStatus(_configuration.Current.Ui.HideInGpose));

    public ISettingEntry CreateSceneToolsWithoutEditor()
        => new ToggleSettingEntry(
            new SettingDefinition(
                "showSceneToolsWithoutEditor",
                "Show Scene Tools Without Editor",
                "Keeps the gizmo, bounds, and other scene widgets visible when the editor window is closed or collapsed.",
                "ui editor window closed collapsed hidden scene tools gizmo bounds widgets"),
            () => _configuration.Current.Ui.ShowSceneToolsWithoutEditor,
            value => _configuration.TryUpdate(
                configuration => configuration.Ui.ShowSceneToolsWithoutEditor = value),
            () => ResolveOutsideEditorStatus(_configuration.Current.Ui.ShowSceneToolsWithoutEditor));

    public ISettingEntry CreateShortcutsWithoutEditor()
        => new ToggleSettingEntry(
            new SettingDefinition(
                "enableShortcutsWithoutEditor",
                "Enable Shortcuts Without Editor",
                "Allows editor shortcuts when the editor window is closed or collapsed.",
                "ui editor window closed collapsed hidden keyboard shortcuts keybinds"),
            () => _configuration.Current.Ui.EnableShortcutsWithoutEditor,
            value => _configuration.TryUpdate(
                configuration => configuration.Ui.EnableShortcutsWithoutEditor = value),
            () => ResolveOutsideEditorStatus(_configuration.Current.Ui.EnableShortcutsWithoutEditor));

    public ISettingEntry CreatePlacedListRowSize()
        => new ChoiceSettingEntry<SceneListRowSize>(
            new SettingDefinition(
                "placedListRowSize",
                "Placed Item Size",
                "Controls the size and spacing used by rows in the placed list.",
                "ui interface placed list object item row size large compact"),
            PlacedListRowSizeOptions,
            () => _configuration.Current.Ui.PlacedListRowSize,
            value => _configuration.TryUpdate(
                configuration => configuration.Ui.PlacedListRowSize = value),
            style: ChoiceRowStyle.Segmented);

    public ISettingEntry CreateDalamudLogLevel()
        => new ChoiceSettingEntry<LogLevel>(
            new SettingDefinition(
                "dalamudLogLevel",
                "Dalamud Log Level",
                "Minimum Intoner log level written to Dalamud logs in /xllog.",
                "logging log level dalamud xllog diagnostics trace debug information warning error critical"),
            DalamudLogLevelOptions,
            () => _configuration.Current.Logging.DalamudMinimumLevel,
            value => _configuration.TryUpdate(
                configuration => configuration.Logging.DalamudMinimumLevel = value),
            style: ChoiceRowStyle.Combo,
            layout: new SettingRowLayout(SettingsChrome.DefaultControlWidth));

    public ISettingEntry CreateShowDebugTab()
        => new ToggleSettingEntry(
            new SettingDefinition(
                "showDebugTab",
                "Show Debug Tab",
                "Shows the Debug tab in the editor.",
                "diagnostics developer development debug tab"),
            () => _configuration.Current.Ui.ShowDebugTab,
            value => _configuration.TryUpdate(
                configuration => configuration.Ui.ShowDebugTab = value),
            ResolveDebugTabStatus);

    public ISettingEntry CreateShowDebugInformation()
        => new ToggleSettingEntry(
            new SettingDefinition(
                "showDebugInformation",
                "Show Debug Information",
                "Shows additional debug information throughout the plugin.",
                "diagnostics developer development debug information details ui"),
            () => _configuration.Current.Ui.ShowDebugInformation,
            value => _configuration.TryUpdate(
                configuration => configuration.Ui.ShowDebugInformation = value),
            () => _configuration.Current.Ui.ShowDebugInformation
                ? new SettingStatus("Visible", ThemeColors.AccentGreen)
                : new SettingStatus("Hidden", ThemeColors.TextDisabled));

    public ISettingEntry CreateDrawMode()
        => new ChoiceSettingEntry<DrawMode>(
            new SettingDefinition(
                "drawMode",
                "Draw Mode",
                "Mode that determines how object widget draws are rendered.",
                "bounds outline drawing gizmo draw"),
            DrawModeOptions,
            () => _configuration.Current.Rendering.DrawMode,
            value => _configuration.TryUpdate(
                configuration => configuration.Rendering.DrawMode = value),
            style: ChoiceRowStyle.Segmented,
            layout: new SettingRowLayout(SettingsChrome.WideControlWidth));

    public ISettingEntry CreateDrawDepthMode()
        => new ChoiceSettingEntry<DrawDepthMode>(
            new SettingDefinition(
                "drawDepth",
                "Occlusion Mode",
                "Controls how the render mode for object widgets responds to scene geometry.",
                "bounds occlude visible behind walls furniture depth"),
            DrawDepthModeOptions,
            () => _configuration.Current.Rendering.DepthMode,
            value => _configuration.TryUpdate(
                configuration => configuration.Rendering.DepthMode = value),
            isEnabled: () => _configuration.Current.Rendering.DrawMode != DrawMode.ImGui,
            style: ChoiceRowStyle.Segmented,
            layout: new SettingRowLayout(SettingsChrome.WiderControlWidth));

    public ISettingEntry CreateAntiAliasing()
        => new IntegerSettingEntry(
            new SettingDefinition(
                "drawAntiAliasing",
                "Anti Aliasing",
                "Anti-aliasing for all native draws. (bounds, gizmo, etc.)",
                "line smoothing anti aliasing aa bounds grid gizmo native"),
            new IntegerSettingRange(
                RenderingConfiguration.MinimumAntiAliasing,
                RenderingConfiguration.MaximumAntiAliasing,
                RenderingConfiguration.AntiAliasingStep),
            () => _configuration.Current.Rendering.AntiAliasing,
            value => _configuration.TryUpdate(
                configuration => configuration.Rendering.AntiAliasing = value),
            static value => FormatAntiAliasing(value),
            static value => FormatAntiAliasing(value),
            isEnabled: () => _configuration.Current.Rendering.DrawMode != DrawMode.ImGui,
            layout: new SettingRowLayout(SettingsChrome.WideControlWidth));

    public ISettingEntry CreateDrawOverGameUi()
        => new ToggleSettingEntry(
            new SettingDefinition(
                "drawOverGameUi",
                "Draw Over Game UI",
                "Controls whether object widgets draw over native game UI.",
                "outline drawing bounds gizmo native ui hud occlude"),
            () => _configuration.Current.Rendering.DrawOverGameUi,
            value => _configuration.TryUpdate(
                configuration => configuration.Rendering.DrawOverGameUi = value),
            ResolveDrawOverGameUiStatus,
            () => _configuration.Current.Rendering.DrawMode != DrawMode.ImGui);

    private SettingStatus ResolveLayoutAutosaveStatus()
        => _configuration.Current.LayoutAutoSave.Enabled
            ? new SettingStatus("Active", ThemeColors.AccentGreen)
            : new SettingStatus("Paused", ThemeColors.TextDisabled);

    private SettingStatus ResolveSplashScreenStartupStatus()
        => _configuration.Current.Ui.ShowSplashScreenOnStartup
            ? new SettingStatus("Startup", ThemeColors.AccentGreen)
            : new SettingStatus("Manual", ThemeColors.TextDisabled);

    private static SettingStatus ResolveWindowVisibilityStatus(bool hidesWithCondition)
        => hidesWithCondition
            ? new SettingStatus("Hides", ThemeColors.AccentGreen)
            : new SettingStatus("Stays Visible", ThemeColors.TextDisabled);

    private static SettingStatus ResolveOutsideEditorStatus(bool enabled)
        => enabled
            ? new SettingStatus("Always", ThemeColors.AccentGreen)
            : new SettingStatus("Window Only", ThemeColors.TextDisabled);

    private SettingStatus ResolveDebugTabStatus()
        => _configuration.Current.Ui.ShowDebugTab
            ? new SettingStatus("Visible", ThemeColors.AccentGreen)
            : new SettingStatus("Hidden", ThemeColors.TextDisabled);

    private SettingStatus ResolveDrawOverGameUiStatus()
        => _configuration.Current.Rendering.DrawOverGameUi
            ? new SettingStatus("Over UI", ThemeColors.AccentBlue)
            : new SettingStatus("Behind UI", ThemeColors.TextDisabled);

    private ColorChoice ReadPrimaryColor()
        => _configuration.Current.Ui.Theme.PrimaryColor.Source == UiColorSource.Custom
            ? new ColorChoice(null)
            : new ColorChoice(_configuration.Current.Ui.Theme.PrimaryColor.Preset);

    private void WritePrimaryColor(ColorChoice choice)
        => _configuration.TryUpdate(configuration =>
        {
            UiThemeColorConfiguration primaryColor = configuration.Ui.Theme.PrimaryColor;
            primaryColor.Source = choice.Preset is null ? UiColorSource.Custom : UiColorSource.Preset;
            if (choice.Preset is { } preset)
            {
                primaryColor.Preset = preset;
            }
        });

    private static string FormatInterval(int seconds)
    {
        if (seconds < 60)
        {
            return $"{seconds} sec";
        }

        var minutes = seconds / 60;
        var remainingSeconds = seconds % 60;
        if (remainingSeconds == 0)
        {
            return minutes == 1
                ? "1 min"
                : $"{minutes} min";
        }

        return $"{minutes}m {remainingSeconds}s";
    }

    private static string FormatAntiAliasing(int value)
        => value <= RenderingConfiguration.MinimumAntiAliasing
            ? "Off"
            : $"{RenderingConfiguration.AntiAliasingToPixels(value):0.00} px";

    [StructLayout(LayoutKind.Auto)]
    private readonly record struct ColorChoice(UiColorPreset? Preset);
}

