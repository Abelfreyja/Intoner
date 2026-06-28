namespace Intoner.Objects.Utils;

internal static class ObjectTemporarySourceUtility
{
    public static string NormalizeSourceKey(string? sourceKey)
        => ObjectStringUtility.TrimOrEmpty(sourceKey);

    public static bool IsNewSession(Guid currentSessionId, Guid requestedSessionId)
        => requestedSessionId != Guid.Empty
        && requestedSessionId != currentSessionId;

    public static bool IsStaleRevision(long currentRevision, long requestedRevision)
        => requestedRevision > 0 && requestedRevision < currentRevision;

    public static long ResolveRevision(long currentRevision, long requestedRevision)
        => requestedRevision > 0
            ? requestedRevision
            : currentRevision + 1;

    public static Guid ResolveSessionId(Guid currentSessionId, Guid requestedSessionId)
        => requestedSessionId != Guid.Empty
            ? requestedSessionId
            : currentSessionId;

    public static string ResolveName(string? existingName, string requestedName, string fallback)
    {
        string normalizedName = ObjectStringUtility.TrimOrEmpty(requestedName);
        if (normalizedName.Length > 0)
        {
            return normalizedName;
        }

        return ObjectStringUtility.TrimOrFallback(existingName, fallback);
    }
}

