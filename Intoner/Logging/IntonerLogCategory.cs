namespace Intoner.Logging;

internal readonly record struct IntonerLogCategory(string FullName, string DisplayName);

internal static class IntonerLogCategoryFormatter
{
    public static IntonerLogCategory Create(string categoryName, int displayWidth)
    {
        var fullName = string.IsNullOrWhiteSpace(categoryName) ? "Intoner" : categoryName;
        var displayName = fullName.Split('.', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? fullName;
        return new IntonerLogCategory(fullName, FitDisplayName(displayName, Math.Max(8, displayWidth)));
    }

    private static string FitDisplayName(string name, int width)
    {
        if (name.Length == width)
        {
            return name;
        }

        if (name.Length < width)
        {
            return name.PadLeft(width);
        }

        if (width <= 5)
        {
            return name[..width];
        }

        var left = Math.Max(1, (width - 3) / 2);
        var right = width - 3 - left;
        return string.Concat(name.AsSpan(0, left), "...", name.AsSpan(name.Length - right, right));
    }
}
