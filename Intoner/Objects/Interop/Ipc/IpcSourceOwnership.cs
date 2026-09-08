using Dalamud.Plugin.Ipc;
using Intoner.Objects.Utils;

namespace Intoner.Objects.Interop.Ipc;

/// <summary> derives caller owned temporary source keys from Dalamud invocation context </summary>
internal static class IpcSourceOwnership
{
    public static bool TryCreateSourceKey(IpcContext? context, string? sourceId, out string sourceKey)
    {
        if (!TryCreateOwnerPrefix(context, out string ownerPrefix))
        {
            sourceKey = string.Empty;
            return false;
        }

        string normalizedSourceId = TemporarySourceUtility.NormalizeKey(sourceId);
        if (normalizedSourceId.Length == 0)
        {
            sourceKey = string.Empty;
            return false;
        }

        sourceKey = ownerPrefix + normalizedSourceId;
        return true;
    }

    public static bool TryCreateOwnerPrefix(IpcContext? context, out string ownerPrefix)
    {
        string owner = context?.SourcePlugin?.InternalName?.Trim() ?? string.Empty;
        if (owner.Length == 0)
        {
            ownerPrefix = string.Empty;
            return false;
        }

        ownerPrefix = $"ipc:{owner.Length}:{owner}:";
        return true;
    }

    public static bool TryGetSourceId(string ownerPrefix, string sourceKey, out string sourceId)
    {
        if (ownerPrefix.Length > 0 && sourceKey.StartsWith(ownerPrefix, StringComparison.Ordinal))
        {
            sourceId = sourceKey[ownerPrefix.Length..];
            return sourceId.Length > 0;
        }

        sourceId = string.Empty;
        return false;
    }
}
