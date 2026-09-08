namespace Intoner.Objects.UI.Settings;

internal sealed class SettingsCatalog
{
    public SettingsCatalog(IEnumerable<ISettingsProvider> providers)
    {
        Modules = providers
            .SelectMany(static provider => provider.Modules)
            .OrderBy(static module => module.Order)
            .ThenBy(static module => module.Tab.Id, StringComparer.Ordinal)
            .ToList();
        Entries = Modules
            .SelectMany(static module => module.Sections)
            .SelectMany(static section => section.Entries)
            .ToList();
        ValidateModules(Modules);
    }

    public IReadOnlyList<SettingsModule> Modules { get; }

    public IReadOnlyList<ISettingEntry> Entries { get; }

    public int EntryCount
        => Entries.Count;

    private static void ValidateModules(IReadOnlyList<SettingsModule> modules)
    {
        HashSet<string> tabIds = new(StringComparer.Ordinal);
        HashSet<string> sectionIds = new(StringComparer.Ordinal);
        HashSet<string> settingIds = new(StringComparer.Ordinal);
        foreach (SettingsModule module in modules)
        {
            if (string.IsNullOrWhiteSpace(module.Tab.Id))
            {
                throw new InvalidOperationException("settings tab id cannot be empty");
            }

            if (!tabIds.Add(module.Tab.Id))
            {
                throw new InvalidOperationException($"duplicate settings tab {module.Tab.Id}");
            }

            SettingsSection? invalidSection = module.Sections.FirstOrDefault(
                static section => string.IsNullOrWhiteSpace(section.Id));
            if (invalidSection is not null)
            {
                throw new InvalidOperationException($"settings section id cannot be empty in tab {module.Tab.Id}");
            }

            SettingsSection? duplicateSection = module.Sections.FirstOrDefault(
                section => !sectionIds.Add(section.Id));
            if (duplicateSection is not null)
            {
                throw new InvalidOperationException($"duplicate settings section {duplicateSection.Id}");
            }

            IEnumerable<ISettingEntry> entries = module.Sections.SelectMany(static section => section.Entries);
            ISettingEntry? invalidEntry = entries.FirstOrDefault(
                static entry => string.IsNullOrWhiteSpace(entry.Definition.Id));
            if (invalidEntry is not null)
            {
                throw new InvalidOperationException($"setting id cannot be empty in tab {module.Tab.Id}");
            }

            ISettingEntry? duplicateEntry = entries.FirstOrDefault(
                entry => !settingIds.Add(entry.Definition.Id));
            if (duplicateEntry is not null)
            {
                throw new InvalidOperationException($"duplicate setting {duplicateEntry.Definition.Id}");
            }
        }
    }
}
