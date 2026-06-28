using System.Collections.Immutable;

namespace Intoner.Objects.Utils;

internal static class ObjectCollectionDiagnosticUtility
{
    public static IReadOnlyList<string> NormalizeWarnings(IEnumerable<string> warnings)
    {
        List<string> normalizedWarnings = [];
        HashSet<string> seenWarnings = new(StringComparer.OrdinalIgnoreCase);
        foreach (string warning in warnings)
        {
            string normalizedWarning = ObjectStringUtility.TrimOrEmpty(warning);
            if (normalizedWarning.Length == 0 || !seenWarnings.Add(normalizedWarning))
            {
                continue;
            }

            normalizedWarnings.Add(normalizedWarning);
        }

        return normalizedWarnings.Count == 0
            ? []
            : normalizedWarnings.ToImmutableArray();
    }
}

