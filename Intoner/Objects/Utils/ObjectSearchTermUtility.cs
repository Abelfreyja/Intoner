using System.Text;

namespace Intoner.Objects.Utils;

internal static class ObjectSearchTermUtility
{
    public static string[] BuildSearchTokens(string searchText)
    {
        if (string.IsNullOrWhiteSpace(searchText))
        {
            return [];
        }

        List<string> tokens = [];
        HashSet<string> seenTokens = new(StringComparer.Ordinal);
        StringBuilder builder = new(searchText.Length);
        foreach (char character in searchText)
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
                continue;
            }

            AddSearchToken(tokens, seenTokens, builder);
        }

        AddSearchToken(tokens, seenTokens, builder);
        return tokens.ToArray();
    }

    public static string NormalizeSearchText(string searchText)
    {
        if (string.IsNullOrWhiteSpace(searchText))
        {
            return string.Empty;
        }

        StringBuilder builder = new(searchText.Length);
        foreach (char character in searchText)
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
            }
        }

        return builder.ToString();
    }

    public static bool MatchesSearchText(string searchText, IReadOnlyList<string> searchTokens)
        => MatchesNormalizedSearchText(NormalizeSearchText(searchText), searchTokens);

    public static bool MatchesNormalizedSearchText(string normalizedSearchText, IReadOnlyList<string> searchTokens)
    {
        if (searchTokens.Count == 0)
        {
            return true;
        }

        if (normalizedSearchText.Length == 0)
        {
            return false;
        }

        foreach (string token in searchTokens)
        {
            if (!normalizedSearchText.Contains(token, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    public static HashSet<string> CreateSet(params string?[] searchTerms)
    {
        HashSet<string> normalizedSearchTerms = new(StringComparer.OrdinalIgnoreCase);
        _ = AddTerms(normalizedSearchTerms, searchTerms);
        return normalizedSearchTerms;
    }

    public static HashSet<string> CreateSet(IEnumerable<string?> searchTerms)
    {
        HashSet<string> normalizedSearchTerms = new(StringComparer.OrdinalIgnoreCase);
        _ = AddTerms(normalizedSearchTerms, searchTerms);
        return normalizedSearchTerms;
    }

    public static bool AddTerms(HashSet<string> searchTerms, IEnumerable<string?> values)
    {
        var changed = false;
        foreach (string? value in values)
        {
            changed |= AddTerm(searchTerms, value);
        }

        return changed;
    }

    public static bool AddTerm(HashSet<string> searchTerms, string? value)
    {
        string normalizedValue = ObjectStringUtility.TrimOrEmpty(value);
        return normalizedValue.Length > 0 && searchTerms.Add(normalizedValue);
    }

    public static bool AddPathSegments(HashSet<string> searchTerms, string? path)
    {
        string normalizedPath = ObjectStringUtility.TrimOrEmpty(path);
        if (normalizedPath.Length == 0)
        {
            return false;
        }

        var changed = false;
        foreach (string segment in normalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            changed |= AddTerm(searchTerms, segment);
        }

        return changed;
    }

    public static string[] BuildStableTerms(IEnumerable<string?> searchTerms)
    {
        HashSet<string> normalizedSearchTerms = new(StringComparer.OrdinalIgnoreCase);
        _ = AddTerms(normalizedSearchTerms, searchTerms);
        return normalizedSearchTerms
            .OrderBy(static value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static string[] BuildStableTerms(params string?[] searchTerms)
        => BuildStableTerms((IEnumerable<string?>)searchTerms);

    public static string[] BuildStableTerms(
        IEnumerable<string?> seedTerms,
        params IReadOnlyList<string>?[] relatedTerms)
    {
        HashSet<string> normalizedSearchTerms = CreateSet(seedTerms);
        foreach (IReadOnlyList<string>? terms in relatedTerms)
        {
            if (terms is not null)
            {
                _ = AddTerms(normalizedSearchTerms, terms);
            }
        }

        return BuildStableTerms(normalizedSearchTerms);
    }

    public static IReadOnlyList<string> MergeTerms(IReadOnlyList<string> primaryTerms, IReadOnlyList<string> secondaryTerms)
    {
        if (secondaryTerms.Count == 0)
        {
            return primaryTerms;
        }

        HashSet<string> mergedTerms = CreateSet(primaryTerms);
        _ = AddTerms(mergedTerms, secondaryTerms);
        return BuildStableTerms(mergedTerms);
    }

    public static string BuildSearchText(IEnumerable<string?> searchTerms)
    {
        StringBuilder builder = new(128);
        HashSet<string> seenTerms = new(StringComparer.OrdinalIgnoreCase);
        foreach (string? searchTerm in searchTerms)
        {
            string normalizedSearchTerm = ObjectStringUtility.TrimOrEmpty(searchTerm);
            if (normalizedSearchTerm.Length == 0 || !seenTerms.Add(normalizedSearchTerm))
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.Append('\n');
            }

            builder.Append(normalizedSearchTerm);
        }

        return builder.ToString();
    }

    private static void AddSearchToken(List<string> tokens, HashSet<string> seenTokens, StringBuilder builder)
    {
        if (builder.Length == 0)
        {
            return;
        }

        string token = builder.ToString();
        if (seenTokens.Add(token))
        {
            tokens.Add(token);
        }

        builder.Clear();
    }
}

