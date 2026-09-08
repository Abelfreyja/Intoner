using Dalamud.Interface;
using Intoner.Objects.UI.Settings;
using Intoner.Objects.UI.Settings.Components;
using Intoner.Services.Configuration;
using Intoner.Services.Dependencies;

namespace Intoner.Objects.UI.Dependencies;

internal sealed class DependencySettings : ISettingsProvider
{
    private const string TabId = "dependencies";

    public DependencySettings(
        IDependencyService dependencies,
        IIntonerConfigurationService configuration)
    {
        List<ISettingEntry> entries =
        [
            CreateTitleBarSetting(configuration),
        ];
        foreach (DependencyStatus dependency in dependencies.Statuses)
        {
            entries.Add(new DependencyStatusEntry(dependencies, dependency.Definition));
        }

        SettingsTabDefinition tab = new(
            TabId,
            "Dependencies",
            "dependencies plugins optional required status api",
            FontAwesomeIcon.Plug,
            static () => ThemeColors.AccentBlue);
        SettingsSection section = new(
            "dependencies",
            FontAwesomeIcon.Plug,
            "Dependencies",
            "Optional plugins and services that Intoner is compatible with.",
            "dependencies plugins optional required status api title bar",
            entries,
            ShowEntryCount: false);
        Modules =
        [
            new SettingsModule(600, tab, [section]),
        ];
    }

    public IReadOnlyList<SettingsModule> Modules { get; }

    private static ISettingEntry CreateTitleBarSetting(IIntonerConfigurationService configuration)
        => new ToggleSettingEntry(
            new SettingDefinition(
                "showDependencyStatusInTitleBar",
                "Show Status in Title Bar",
                "Show a small indicator in Intoner's title bar whenever there's a dependency issue.",
                "dependency dependencies status title bar indicator warning show hide"),
            () => configuration.Current.Ui.ShowDependencyStatusInTitleBar,
            value => configuration.TryUpdate(
                current => current.Ui.ShowDependencyStatusInTitleBar = value),
            () => configuration.Current.Ui.ShowDependencyStatusInTitleBar
                ? new SettingStatus("Shown", ThemeColors.AccentGreen)
                : new SettingStatus("Hidden", ThemeColors.TextDisabled));
}
