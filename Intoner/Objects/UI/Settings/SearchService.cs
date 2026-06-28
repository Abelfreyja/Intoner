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

        return new SearchQuery(ObjectSearchTermUtility.BuildSearchTokens(searchText));
    }

    public static SearchResult Filter(
        SettingsCatalog catalog,
        SettingsTab? selectedTab,
        SearchQuery query)
    {
        List<SectionResult> sections = [];
        var entryCount = 0;

        foreach (SettingsSection section in catalog.Sections)
        {
            if (selectedTab.HasValue && section.Tab != selectedTab.Value)
            {
                continue;
            }

            SettingsTabDefinition tab = catalog.GetTab(section.Tab);
            List<ISettingEntry> entries = [];

            if (!query.HasTokens)
            {
                entries.AddRange(section.Entries);
            }
            else
            {
                bool sectionMatches = MatchesSection(query, tab, section);
                foreach (ISettingEntry entry in section.Entries)
                {
                    if (MatchesEntry(query, entry))
                    {
                        entries.Add(entry);
                    }
                }

                if (entries.Count == 0 && sectionMatches)
                {
                    entries.AddRange(section.Entries);
                }
            }

            if (entries.Count == 0)
            {
                continue;
            }

            sections.Add(new SectionResult(section, entries));
            entryCount += entries.Count;
        }

        return new SearchResult(sections, entryCount);
    }

    public static SettingsView BuildView(
        SettingsCatalog catalog,
        SettingsTab? selectedTab,
        SearchQuery query)
    {
        SearchResult allResult = Filter(catalog, null, query);
        SearchResult? selectedResult = selectedTab.HasValue ? null : allResult;
        List<CategoryResult> categories =
        [
            new(null, "All", allResult),
        ];

        foreach (SettingsTabDefinition tab in catalog.Tabs)
        {
            SearchResult tabResult = Filter(catalog, tab.Key, query);
            categories.Add(new CategoryResult(tab.Key, tab.Label, tabResult));

            if (selectedTab == tab.Key)
            {
                selectedResult = tabResult;
            }
        }

        return new SettingsView(
            query,
            allResult,
            selectedResult ?? allResult,
            categories);
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
        => ObjectSearchTermUtility.MatchesSearchText(ObjectSearchTermUtility.BuildSearchText(values), query.Tokens);
}

