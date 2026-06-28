namespace Intoner.Objects.Resources;

/// <summary> helpers for stable object resource redirect lists </summary>
internal static class ObjectPathRedirectionUtility
{
    public static bool TryCreate(string requestedPath, ObjectResolvedPath resolvedPath, out ObjectPathRedirection redirection)
    {
        if (!ObjectResourcePathUtility.IsSupportedRedirection(requestedPath, resolvedPath))
        {
            redirection = default;
            return false;
        }

        redirection = new ObjectPathRedirection(requestedPath, resolvedPath);
        return true;
    }

    public static IReadOnlyList<ObjectPathRedirection> CreateStableList(IEnumerable<ObjectPathRedirection> redirects)
    {
        Dictionary<string, ObjectResolvedPath> redirectMap = new(StringComparer.OrdinalIgnoreCase);
        foreach (ObjectPathRedirection redirect in redirects)
        {
            if (!TryCreate(redirect.RequestedPath, redirect.ResolvedPath, out ObjectPathRedirection supportedRedirect))
            {
                continue;
            }

            redirectMap[supportedRedirect.RequestedPath] = supportedRedirect.ResolvedPath;
        }

        return redirectMap
            .OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(static pair => new ObjectPathRedirection(pair.Key, pair.Value))
            .ToList();
    }
}


