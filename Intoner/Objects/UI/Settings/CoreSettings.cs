using Dalamud.Interface;

namespace Intoner.Objects.UI.Settings;

internal sealed class CoreSettings : ISettingsProvider
{
    public CoreSettings(CoreSettingFactory entries)
        => Modules = CreateModules(entries);

    public IReadOnlyList<SettingsModule> Modules { get; }

    private static IReadOnlyList<SettingsModule> CreateModules(CoreSettingFactory entries)
        =>
        [
            new SettingsModule(
                100,
                new SettingsTabDefinition(
                    "assets",
                    "Assets",
                    "asset capture catalog runtime observer cache",
                    FontAwesomeIcon.Cube,
                    static () => ThemeColors.AccentBlue),
                [
                    new SettingsSection(
                        "assetCapture",
                        FontAwesomeIcon.Cube,
                        "Asset Capture",
                        "Runtime asset caching.",
                        "asset capture catalog runtime observer cache discovery startup",
                        [
                            entries.CreateRuntimeAssetCapture(),
                        ]),
                ]),
            new SettingsModule(
                200,
                new SettingsTabDefinition(
                    "housing",
                    "Housing",
                    "housing house furniture furnishing mode limit",
                    FontAwesomeIcon.Home,
                    static () => ThemeColors.AccentOrange),
                [
                    new SettingsSection(
                        "housingMode",
                        FontAwesomeIcon.Home,
                        "Housing Mode",
                        "Set housing limits for designing in-game housing.",
                        "housing house mode strict furniture limit apartment small medium large indoor outdoor tabletop floating",
                        [
                            entries.CreateWorkspaceMode(),
                            entries.CreateHousingSize(),
                            entries.CreateHousingArea(),
                        ]),
                ]),
            new SettingsModule(
                300,
                new SettingsTabDefinition(
                    "layouts",
                    "Layouts",
                    "layout layouts save autosave recovery draft workspace",
                    FontAwesomeIcon.LayerGroup,
                    static () => ThemeColors.AccentGreen),
                [
                    new SettingsSection(
                        "layoutAutosave",
                        FontAwesomeIcon.Clock,
                        "Autosave",
                        "Temporary workspace recovery layouts.",
                        "layout autosave auto save recovery interval",
                        [
                            entries.CreateLayoutAutosaveEnabled(),
                            entries.CreateLayoutAutosaveInterval(),
                        ]),
                ]),
            new SettingsModule(
                400,
                new SettingsTabDefinition(
                    "ui",
                    "UI",
                    "ui interface splash screen startup window",
                    FontAwesomeIcon.WindowMaximize,
                    static () => ThemeColors.AccentPrimary),
                [
                    new SettingsSection(
                        "colors",
                        FontAwesomeIcon.Palette,
                        "Colors",
                        "Customize the colors used for the UI.",
                        "ui theme color custom purple pink",
                        [
                            entries.CreatePrimaryColor(),
                            entries.CreateCustomColor(),
                        ]),
                    new SettingsSection(
                        "splashScreen",
                        FontAwesomeIcon.Book,
                        "Splash Screen",
                        "Controls the splash screen behaviour.",
                        "ui interface splash screen startup start open",
                        [
                            entries.CreateSplashScreenOnStartup(),
                        ]),
                    new SettingsSection(
                        "windowVisibility",
                        FontAwesomeIcon.Eye,
                        "Window Visibility",
                        "Choose when Intoner hides its window automatically.",
                        "ui interface window visibility hide hidden game hud cutscene gpose group pose",
                        [
                            entries.CreateHideWithGameUi(),
                            entries.CreateHideInCutscenes(),
                            entries.CreateHideInGpose(),
                        ]),
                    new SettingsSection(
                        "windowState",
                        FontAwesomeIcon.WindowRestore,
                        "Window State",
                        "Choose what remains available when the editor window is closed or collapsed.",
                        "ui interface editor window closed collapsed hidden scene tools widgets gizmo bounds shortcuts keybinds",
                        [
                            entries.CreateSceneToolsWithoutEditor(),
                            entries.CreateShortcutsWithoutEditor(),
                        ]),
                    new SettingsSection(
                        "placedList",
                        FontAwesomeIcon.Bars,
                        "Placed List",
                        "Customize how placed list rows are shown.",
                        "ui interface placed list object item row size large compact",
                        [
                            entries.CreatePlacedListRowSize(),
                        ]),
                ]),
            new SettingsModule(
                500,
                new SettingsTabDefinition(
                    "rendering",
                    "Rendering",
                    "viewport rendering drawing bounds gizmo native imgui depth occlusion",
                    FontAwesomeIcon.Cog,
                    static () => ThemeColors.AccentPrimary),
                [
                    new SettingsSection(
                        "sceneDrawing",
                        FontAwesomeIcon.ProjectDiagram,
                        "Editor Rendering",
                        "Handles how various widgets are rendered based on settings.",
                        "viewport rendering drawing bounds gizmo native imgui depth occlusion ui hud anti aliasing smoothing",
                        [
                            entries.CreateDrawMode(),
                            entries.CreateDrawDepthMode(),
                            entries.CreateAntiAliasing(),
                            entries.CreateDrawOverGameUi(),
                        ]),
                ]),
            new SettingsModule(
                700,
                new SettingsTabDefinition(
                    "diagnostics",
                    "Diagnostics",
                    "diagnostics logging logs xllog debug",
                    FontAwesomeIcon.Bug,
                    static () => ThemeColors.AccentYellow),
                [
                    new SettingsSection(
                        "logging",
                        FontAwesomeIcon.Bug,
                        "Logging",
                        "Controls Intoner logging behavior.",
                        "diagnostics logging logs xllog trace debug information warning error critical",
                        [
                            entries.CreateDalamudLogLevel(),
                        ]),
                    new SettingsSection(
                        "developerTools",
                        FontAwesomeIcon.Tools,
                        "Developer Tools",
                        "Controls access to dev and debugging tools.",
                        "diagnostics developer development debug tools tab",
                        [
                            entries.CreateShowDebugTab(),
                            entries.CreateShowDebugInformation(),
                        ]),
                ]),
        ];
}
