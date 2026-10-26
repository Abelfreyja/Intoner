namespace Intoner.Objects.Utils;

internal static class TextUtility
{
    public static string TrimOrEmpty(string? value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();

    public static string TrimOrFallback(string? value, string fallbackValue)
    {
        string normalizedValue = TrimOrEmpty(value);
        return normalizedValue.Length > 0
            ? normalizedValue
            : fallbackValue;
    }

    public static string ResolveAvailableName(string preferred, IReadOnlySet<string> unavailableNames, int maximumLength = int.MaxValue)
    {
        if (preferred.Length == 0 || !unavailableNames.Contains(preferred))
        {
            return preferred;
        }

        for (int number = 2; number < int.MaxValue; ++number)
        {
            string suffix = $" ({number})";
            int baseLength = Math.Min(preferred.Length, maximumLength - suffix.Length);
            string candidate = preferred[..baseLength].TrimEnd() + suffix;

            if (!unavailableNames.Contains(candidate))
            {
                return candidate;
            }
        }

        return string.Empty;
    }
}
