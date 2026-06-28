namespace Intoner.Objects.Utils;

internal static class ObjectStringUtility
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
}

