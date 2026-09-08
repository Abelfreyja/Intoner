using Intoner.Objects.Utils;

namespace Intoner.Objects.UI.Settings;

internal static class SearchService
{
    public static SearchQuery BuildQuery(string searchText)
    {
        if (string.IsNullOrWhiteSpace(searchText))
        {
            return new SearchQuery([]);
        }

        return new SearchQuery(SearchTermUtility.BuildSearchTokens(searchText));
    }

    public static SettingsView BuildView(
        SettingsCatalog catalog,
        string? selectedTabId,
        SearchQuery query)
    {
        List<SectionResult> allSections = [];
        List<CategoryResult> categories = [];
        SearchResult? selectedResult = null;
        var totalEntryCount = 0;

        foreach (SettingsModule module in catalog.Modules)
        {
            SettingsTabDefinition tab = module.Tab;
            SearchResult tabResult = Filter(module, query);
            categories.Add(new CategoryResult(tab, tab.Label, tabResult));
            allSections.AddRange(tabResult.Sections);
            totalEntryCount += tabResult.EntryCount;

            if (string.Equals(selectedTabId, tab.Id, StringComparison.Ordinal))
            {
                selectedResult = tabResult;
            }
        }

        SearchResult allResult = new(allSections, totalEntryCount);
        categories.Insert(0, new CategoryResult(null, "All", allResult));

        return new SettingsView(
            query,
            allResult,
            selectedTabId is null ? allResult : selectedResult ?? allResult,
            categories);
    }

    private static SearchResult Filter(SettingsModule module, SearchQuery query)
    {
        List<SectionResult> sections = [];
        var entryCount = 0;
        foreach (SettingsSection section in module.Sections)
        {
            List<ISettingEntry> visibleEntries = section.Entries
                .Where(static entry => entry.IsVisible)
                .ToList();
            List<ISettingEntry> entries = [];
            if (!query.HasTokens)
            {
                entries.AddRange(visibleEntries);
            }
            else
            {
                bool sectionMatches = MatchesSection(query, module.Tab, section);
                entries.AddRange(visibleEntries.Where(entry => MatchesEntry(query, entry)));

                if (entries.Count == 0 && sectionMatches)
                {
                    entries.AddRange(visibleEntries);
                }
            }

            if (entries.Count == 0)
            {
                continue;
            }

            sections.Add(new SectionResult(module.Tab, section, entries));
            entryCount += entries.Count;
        }

        return new SearchResult(sections, entryCount);
    }

    private static bool MatchesSection(
        SearchQuery query,
        SettingsTabDefinition tab,
        SettingsSection section)
        => MatchesText(
            query,
            tab.Label,
            tab.Keywords,
            section.Id,
            section.Title,
            section.Description,
            section.Keywords);

    private static bool MatchesEntry(SearchQuery query, ISettingEntry entry)
        => MatchesText(
            query,
            entry.Definition.Id,
            entry.Definition.Label,
            entry.Definition.Description,
            entry.Definition.Keywords);

    private static bool MatchesText(SearchQuery query, params string[] values)
        => SearchTermUtility.MatchesSearchText(SearchTermUtility.BuildSearchText(values), query.Tokens);
}

