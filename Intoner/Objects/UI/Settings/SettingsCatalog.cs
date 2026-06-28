using Dalamud.Interface;

namespace Intoner.Objects.UI.Settings;

internal sealed class SettingsCatalog
{
    private readonly Dictionary<SettingsTab, SettingsTabDefinition> _tabsByKey = [];

    public SettingsCatalog(
        IReadOnlyList<SettingsTabDefinition> tabs,
        IReadOnlyList<SettingsSection> sections)
    {
        Tabs = tabs;
        Sections = sections;
        IndexTabs(tabs);
        ValidateSections(sections);
    }

    public IReadOnlyList<SettingsTabDefinition> Tabs { get; }

    public IReadOnlyList<SettingsSection> Sections { get; }

    public SettingsTabDefinition GetTab(SettingsTab key)
        => _tabsByKey.TryGetValue(key, out SettingsTabDefinition? tab)
            ? tab
            : throw new InvalidOperationException($"missing object settings tab {key}");

    public static SettingsCatalog CreateDefault()
        => new(
            [
                new(SettingsTab.Assets, "Assets", "asset capture catalog runtime observer cache"),
                new(SettingsTab.Housing, "Housing", "housing house furniture furnishing culling display visibility mode limit"),
                new(SettingsTab.Layouts, "Layouts", "layout layouts save autosave recovery draft workspace"),
                new(SettingsTab.Ui, "UI", "ui interface splash screen startup window"),
                new(SettingsTab.Drawing, "Rendering", "viewport rendering drawing bounds gizmo native imgui depth occlusion"),
                new(SettingsTab.Diagnostics, "Diagnostics", "diagnostics logging logs xllog debug"),
            ],
            [
                new(
                    SettingsTab.Assets,
                    "assetCapture",
                    FontAwesomeIcon.Cube,
                    "Asset Capture",
                    "Runtime asset caching.",
                    "asset capture catalog runtime observer cache discovery startup",
                    [
                        SettingEntries.CreateRuntimeAssetCapture(),
                    ]),
                new(
                    SettingsTab.Housing,
                    "housingMode",
                    FontAwesomeIcon.Home,
                    "Housing Mode",
                    "Set housing limits for designing in-game housing.",
                    "housing house mode strict furniture limit apartment small medium large indoor outdoor tabletop floating",
                    [
                        SettingEntries.CreateWorkspaceMode(),
                        SettingEntries.CreateHousingSize(),
                        SettingEntries.CreateHousingArea(),
                    ]),
                new(
                    SettingsTab.Housing,
                    "housingCulling",
                    FontAwesomeIcon.Eye,
                    "Furniture Culling",
                    "Fix for housing furniture render culling.",
                    "fix housing furniture culling render",
                    [
                        SettingEntries.CreateHousingCulling(),
                    ]),
                new(
                    SettingsTab.Layouts,
                    "layoutAutosave",
                    FontAwesomeIcon.Clock,
                    "Autosave",
                    "Temporary workspace recovery layouts.",
                    "layout autosave auto save recovery interval",
                    [
                        SettingEntries.CreateLayoutAutosaveEnabled(),
                        SettingEntries.CreateLayoutAutosaveInterval(),
                    ]),
                new(
                    SettingsTab.Ui,
                    "splashScreen",
                    FontAwesomeIcon.Book,
                    "Splash Screen",
                    "Controls the splash screen behaviour.",
                    "ui interface splash screen startup start open",
                    [
                        SettingEntries.CreateSplashScreenOnStartup(),
                    ]),
                new(
                    SettingsTab.Drawing,
                    "sceneDrawing",
                    FontAwesomeIcon.ProjectDiagram,
                    "Editor Rendering",
                    "Handles how various widgets are rendered based on settings.",
                    "viewport rendering drawing bounds gizmo native imgui depth occlusion ui hud anti aliasing smoothing",
                    [
                        SettingEntries.CreateDrawMode(),
                        SettingEntries.CreateDrawDepthMode(),
                        SettingEntries.CreateAntiAliasing(),
                        SettingEntries.CreateDrawOverGameUi(),
                    ]),
                new(
                    SettingsTab.Diagnostics,
                    "logging",
                    FontAwesomeIcon.Bug,
                    "Logging",
                    "Controls Intoner logging behavior.",
                    "diagnostics logging logs xllog trace debug information warning error critical",
                    [
                        SettingEntries.CreateDalamudLogLevel(),
                    ]),
            ]);

    private void IndexTabs(IReadOnlyList<SettingsTabDefinition> tabs)
    {
        foreach (SettingsTabDefinition tab in tabs)
        {
            if (!_tabsByKey.TryAdd(tab.Key, tab))
            {
                throw new InvalidOperationException($"duplicate object settings tab {tab.Key}");
            }
        }
    }

    private void ValidateSections(IReadOnlyList<SettingsSection> sections)
    {
        foreach (SettingsSection section in sections)
        {
            if (!_tabsByKey.ContainsKey(section.Tab))
            {
                throw new InvalidOperationException(
                    $"object settings section {section.Id} references missing tab {section.Tab}");
            }
        }
    }
}

