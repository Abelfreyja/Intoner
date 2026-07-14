using Intoner.Objects.Filesystem.Configuration;
using Intoner.Objects.UI;
using Intoner.Objects.UI.Settings.Components;
using Microsoft.Extensions.Logging;

namespace Intoner.Objects.UI.Settings;

internal static class SettingEntries
{
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

    private static readonly ChoiceOption<LogLevel>[] DalamudLogLevelOptions =
    [
        new(LogLevel.Trace, "Trace", "trace verbose detailed diagnostics"),
        new(LogLevel.Debug, "Debug", "debug diagnostics development"),
        new(LogLevel.Information, "Information", "information normal default info"),
        new(LogLevel.Warning, "Warning", "warning warn problems only"),
        new(LogLevel.Error, "Error", "error failures only"),
        new(LogLevel.Critical, "Critical", "critical fatal crashes only"),
    ];

    public static ISettingEntry CreateWorkspaceMode()
        => new ChoiceSettingEntry<ObjectWorkspaceMode>(
            new SettingDefinition(
                "workspaceMode",
                "Workspace Mode",
                "Switches Intoner between Normal and Housing editing modes.",
                "workspace mode normal housing house"),
            WorkspaceModeOptions,
            static context => context.HousingModePolicy.GetState().Mode,
            static (context, value) => context.ConfigurationService.Update(
                configuration => configuration.HousingMode.Mode = value),
            style: ChoiceRowStyle.Segmented);

    public static ISettingEntry CreateHousingSize()
        => new ChoiceSettingEntry<ObjectHousingSize>(
            new SettingDefinition(
                "housingSize",
                "House Size",
                "Selects the set furniture limit depending on size of the housing.",
                "housing size apartment small medium large furniture limit"),
            HousingSizeOptions,
            static context => context.HousingModePolicy.GetState().Size,
            static (context, value) => context.ConfigurationService.Update(configuration =>
            {
                configuration.HousingMode.Size = value;
                if (value == ObjectHousingSize.Apartment)
                {
                    configuration.HousingMode.Area = ObjectHousingArea.Indoor;
                }
            }),
            isEnabled: static context => context.HousingModePolicy.GetState().IsHousingMode);

    public static ISettingEntry CreateHousingArea()
        => new ChoiceSettingEntry<ObjectHousingArea>(
            new SettingDefinition(
                "housingArea",
                "Housing Area",
                "Chooses which housing area is currently being edited.",
                "housing area indoor outdoor interior exterior yard garden"),
            HousingAreaOptions,
            static context => context.HousingModePolicy.GetState().Area,
            static (context, value) => context.ConfigurationService.Update(configuration =>
            {
                configuration.HousingMode.Area = configuration.HousingMode.Size == ObjectHousingSize.Apartment
                    ? ObjectHousingArea.Indoor
                    : value;
            }),
            static (context, value) => value != ObjectHousingArea.Outdoor
                                      || context.HousingModePolicy.GetState().Size != ObjectHousingSize.Apartment,
            static context => context.HousingModePolicy.GetState().IsHousingMode,
            style: ChoiceRowStyle.Segmented);

    public static ISettingEntry CreateRuntimeAssetCapture()
        => new ToggleSettingEntry(
            new SettingDefinition(
                "runtimeAssetCapture",
                "Runtime Asset Capture",
                "Allows the object asset index to observe runtime loaded assets after plugin startup.",
                "asset capture runtime catalog cache"),
            static context => context.ConfigurationService.Current.AssetCapture.EnableRuntimeCapture,
            static (context, value) => context.ConfigurationService.Update(
                configuration => configuration.AssetCapture.EnableRuntimeCapture = value),
            static _ => new SettingStatus("Startup", EditorColors.TextDisabled));

    public static ISettingEntry CreateHousingCulling()
        => new ToggleSettingEntry(
            new SettingDefinition(
                "disableFurnitureRenderCulling",
                "Disable Furniture Render Culling",
                "Keeps loaded housing furniture slots visible after the client loads them. (Doesn't affect furniture spawned by Intoner, since culling doesn't affect them at all)",
                "housing furniture culling visibility display cap hidden"),
            static context => context.HousingCullingService.DisableFurnitureDisplayCulling,
            static (context, value) => context.HousingCullingService.SetDisableFurnitureDisplayCulling(value),
            ResolveHousingCullingStatus,
            static context => context.HousingCullingService.IsHookAvailable);

    public static ISettingEntry CreateLayoutAutosaveEnabled()
        => new ToggleSettingEntry(
            new SettingDefinition(
                "layoutAutosaveEnabled",
                "Enable Autosave",
                "Writes a separate temporary recovery layout for the current object workspace.",
                "layout autosave auto save recovery temporary"),
            static context => context.ConfigurationService.Current.LayoutAutoSave.Enabled,
            static (context, value) => context.ConfigurationService.Update(
                configuration => configuration.LayoutAutoSave.Enabled = value),
            ResolveLayoutAutosaveStatus);

    public static ISettingEntry CreateLayoutAutosaveInterval()
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
            static context => context.ConfigurationService.Current.LayoutAutoSave.IntervalSeconds,
            static (context, value) => context.ConfigurationService.Update(
                configuration => configuration.LayoutAutoSave.IntervalSeconds = value),
            static (_, value) => $"Every {FormatInterval(value)}",
            static (_, value) => FormatInterval(value),
            isEnabled: static context => context.ConfigurationService.Current.LayoutAutoSave.Enabled);

    public static ISettingEntry CreateSplashScreenOnStartup()
        => new ToggleSettingEntry(
            new SettingDefinition(
                "showSplashScreenOnStartup",
                "Show On Startup",
                "Shows the splash screen automatically when Intoner finishes loading.",
                "ui interface splash screen startup start open"),
            static context => context.ConfigurationService.Current.Ui.ShowSplashScreenOnStartup,
            static (context, value) => context.ConfigurationService.Update(
                configuration => configuration.Ui.ShowSplashScreenOnStartup = value),
            ResolveSplashScreenStartupStatus);

    public static ISettingEntry CreateHideWithGameUi()
        => new ToggleSettingEntry(
            new SettingDefinition(
                "hideWithGameUi",
                "Hide Intoner When UI Is Hidden",
                "Hide the Intoner window when the game UI is hidden.",
                "ui interface window visibility hide hidden game hud"),
            static context => context.ConfigurationService.Current.Ui.HideWithGameUi,
            static (context, value) => context.ConfigurationService.Update(
                configuration => configuration.Ui.HideWithGameUi = value),
            static context => ResolveWindowVisibilityStatus(context.ConfigurationService.Current.Ui.HideWithGameUi));

    public static ISettingEntry CreateHideInCutscenes()
        => new ToggleSettingEntry(
            new SettingDefinition(
                "hideInCutscenes",
                "Hide Intoner in Cutscenes",
                "Hide the Intoner window while you are watching a cutscene.",
                "ui interface window visibility hide hidden cutscene"),
            static context => context.ConfigurationService.Current.Ui.HideInCutscenes,
            static (context, value) => context.ConfigurationService.Update(
                configuration => configuration.Ui.HideInCutscenes = value),
            static context => ResolveWindowVisibilityStatus(context.ConfigurationService.Current.Ui.HideInCutscenes));

    public static ISettingEntry CreateHideInGpose()
        => new ToggleSettingEntry(
            new SettingDefinition(
                "hideInGpose",
                "Hide Intoner in GPose",
                "Hide the Intoner window while you are in GPose.",
                "ui interface window visibility hide hidden gpose group pose"),
            static context => context.ConfigurationService.Current.Ui.HideInGpose,
            static (context, value) => context.ConfigurationService.Update(
                configuration => configuration.Ui.HideInGpose = value),
            static context => ResolveWindowVisibilityStatus(context.ConfigurationService.Current.Ui.HideInGpose));

    public static ISettingEntry CreateDalamudLogLevel()
        => new ChoiceSettingEntry<LogLevel>(
            new SettingDefinition(
                "dalamudLogLevel",
                "Dalamud Log Level",
                "Minimum Intoner log level written to Dalamud logs in /xllog.",
                "logging log level dalamud xllog diagnostics trace debug information warning error critical"),
            DalamudLogLevelOptions,
            static context => context.ConfigurationService.Current.Logging.DalamudMinimumLevel,
            static (context, value) => context.ConfigurationService.Update(
                configuration => configuration.Logging.DalamudMinimumLevel = value),
            style: ChoiceRowStyle.Combo,
            layout: new SettingRowLayout(SettingsChrome.DefaultControlWidth));

    public static ISettingEntry CreateDrawMode()
        => new ChoiceSettingEntry<DrawMode>(
            new SettingDefinition(
                "drawMode",
                "Draw Mode",
                "Mode that determines how object widget draws are rendered.",
                "bounds outline drawing gizmo draw"),
            DrawModeOptions,
            static context => context.ConfigurationService.Current.Rendering.DrawMode,
            static (context, value) => context.ConfigurationService.Update(
                configuration => configuration.Rendering.DrawMode = value),
            style: ChoiceRowStyle.Segmented,
            layout: new SettingRowLayout(SettingsChrome.WideControlWidth));

    public static ISettingEntry CreateDrawDepthMode()
        => new ChoiceSettingEntry<DrawDepthMode>(
            new SettingDefinition(
                "drawDepth",
                "Occlusion Mode",
                "Controls how the render mode for object widgets responds to scene geometry.",
                "bounds occlude visible behind walls furniture depth"),
            DrawDepthModeOptions,
            static context => context.ConfigurationService.Current.Rendering.DepthMode,
            static (context, value) => context.ConfigurationService.Update(
                configuration => configuration.Rendering.DepthMode = value),
            isEnabled: static context => context.ConfigurationService.Current.Rendering.DrawMode != DrawMode.ImGui,
            style: ChoiceRowStyle.Segmented,
            layout: new SettingRowLayout(SettingsChrome.WiderControlWidth));

    public static ISettingEntry CreateAntiAliasing()
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
            static context => context.ConfigurationService.Current.Rendering.AntiAliasing,
            static (context, value) => context.ConfigurationService.Update(
                configuration => configuration.Rendering.AntiAliasing = value),
            static (_, value) => FormatAntiAliasing(value),
            static (_, value) => FormatAntiAliasing(value),
            isEnabled: static context => context.ConfigurationService.Current.Rendering.DrawMode != DrawMode.ImGui,
            layout: new SettingRowLayout(SettingsChrome.WideControlWidth));

    public static ISettingEntry CreateDrawOverGameUi()
        => new ToggleSettingEntry(
            new SettingDefinition(
                "drawOverGameUi",
                "Draw Over Game UI",
                "Controls whether object widgets draw over native game UI.",
                "outline drawing bounds gizmo native ui hud occlude"),
            static context => context.ConfigurationService.Current.Rendering.DrawOverGameUi,
            static (context, value) => context.ConfigurationService.Update(
                configuration => configuration.Rendering.DrawOverGameUi = value),
            ResolveDrawOverGameUiStatus,
            static context => context.ConfigurationService.Current.Rendering.DrawMode != DrawMode.ImGui);

    private static SettingStatus ResolveHousingCullingStatus(DrawContext context)
        => (context.HousingCullingService.IsHookAvailable, context.HousingCullingService.DisableFurnitureDisplayCulling) switch
        {
            (false, _) => new SettingStatus("Unavailable", EditorColors.DimRed),
            (_, true)  => new SettingStatus("Active", EditorColors.AccentGreen),
            _          => new SettingStatus("Ready", EditorColors.TextDisabled),
        };

    private static SettingStatus ResolveLayoutAutosaveStatus(DrawContext context)
        => context.ConfigurationService.Current.LayoutAutoSave.Enabled
            ? new SettingStatus("Active", EditorColors.AccentGreen)
            : new SettingStatus("Paused", EditorColors.TextDisabled);

    private static SettingStatus ResolveSplashScreenStartupStatus(DrawContext context)
        => context.ConfigurationService.Current.Ui.ShowSplashScreenOnStartup
            ? new SettingStatus("Startup", EditorColors.AccentGreen)
            : new SettingStatus("Manual", EditorColors.TextDisabled);

    private static SettingStatus ResolveWindowVisibilityStatus(bool hidesWithCondition)
        => hidesWithCondition
            ? new SettingStatus("Hides", EditorColors.AccentGreen)
            : new SettingStatus("Stays Visible", EditorColors.TextDisabled);

    private static SettingStatus ResolveDrawOverGameUiStatus(DrawContext context)
        => context.ConfigurationService.Current.Rendering.DrawOverGameUi
            ? new SettingStatus("Over UI", EditorColors.AccentBlue)
            : new SettingStatus("Behind UI", EditorColors.TextDisabled);

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
}

