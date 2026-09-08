namespace Intoner.Objects.Collections;

internal static class CollectionModSettingsUtility
{
    public static string NormalizeGroupName(string? groupName)
        => groupName ?? string.Empty;

    public static bool TryNormalizeOptionNames(IEnumerable<string>? optionNames, out List<string> normalizedOptionNames)
    {
        normalizedOptionNames = [];
        if (optionNames is null)
        {
            return false;
        }

        foreach (string? optionName in optionNames)
        {
            if (optionName is null)
            {
                normalizedOptionNames = [];
                return false;
            }

            normalizedOptionNames.Add(optionName);
        }

        return true;
    }

    public static Dictionary<string, List<string>> CloneSettings(IReadOnlyDictionary<string, List<string>> settings)
        => settings.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value.ToList(),
            StringComparer.Ordinal);

    public static bool AreEqual(
        IReadOnlyDictionary<string, List<string>> left,
        IReadOnlyDictionary<string, List<string>> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        foreach ((string groupName, List<string> optionNames) in left)
        {
            if (!right.TryGetValue(groupName, out List<string>? rightOptionNames)
             || optionNames.Count != rightOptionNames.Count)
            {
                return false;
            }

            for (int index = 0; index < optionNames.Count; ++index)
            {
                if (!string.Equals(optionNames[index], rightOptionNames[index], StringComparison.Ordinal))
                {
                    return false;
                }
            }
        }

        return true;
    }

    public static bool RemoveGroup(Dictionary<string, List<string>> settings, string groupName)
    {
        string normalizedGroupName = NormalizeGroupName(groupName);
        if (settings.Remove(normalizedGroupName))
        {
            return true;
        }

        foreach (string savedGroupName in settings.Keys.ToArray())
        {
            if (string.Equals(savedGroupName, normalizedGroupName, StringComparison.OrdinalIgnoreCase))
            {
                settings.Remove(savedGroupName);
                return true;
            }
        }

        return false;
    }
}


