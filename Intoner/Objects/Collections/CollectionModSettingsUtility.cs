using Intoner.Objects.Utils;

namespace Intoner.Objects.Collections;

internal static class CollectionModSettingsUtility
{
    public static string NormalizeGroupName(string? groupName)
        => ObjectStringUtility.TrimOrEmpty(groupName);

    public static bool TryNormalizeOptionNames(IEnumerable<string>? optionNames, out List<string> normalizedOptionNames)
    {
        normalizedOptionNames = [];
        if (optionNames is null)
        {
            return false;
        }

        normalizedOptionNames = optionNames
            .Select(ObjectStringUtility.TrimOrEmpty)
            .Where(static optionName => optionName.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static optionName => optionName, StringComparer.Ordinal)
            .ToList();
        return true;
    }

    public static Dictionary<string, List<string>> CloneSettings(IReadOnlyDictionary<string, List<string>> settings)
        => settings.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value.ToList(),
            StringComparer.Ordinal);

    public static bool RemoveGroup(Dictionary<string, List<string>> settings, string groupName)
    {
        string normalizedGroupName = NormalizeGroupName(groupName);
        if (normalizedGroupName.Length == 0)
        {
            return false;
        }

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


