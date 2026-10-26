using Intoner.Objects.Assets;
using Intoner.Objects.Utils;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Intoner.Objects.Resources;

/// <summary> object collection scope encoded into native resource handle paths </summary>
internal readonly record struct ObjectScopedResourcePath(long ResourceScopeId, string Path);

internal static class ObjectScopedResourcePathUtility
{
    private const string Prefix = "common-intoner://";
    private const string ObsoletePrefix = "intoner://";
    private const string RejectedPrefix = Prefix + "rejected/";
    private const string UriMarker = "://";
    private const char Separator = '/';

    public static bool TryParse(string path, out ObjectScopedResourcePath scopedPath)
    {
        scopedPath = default;

        string normalizedPath = TextUtility.TrimOrEmpty(path);

        if (!normalizedPath.StartsWith(Prefix, StringComparison.Ordinal)
         || !ObjectResourcePathEncoding.TryGetNativePathByteCount(path, out _))
        {
            return false;
        }

        int separatorIndex = normalizedPath.IndexOf(Separator, Prefix.Length);
        if (separatorIndex < 0)
        {
            return false;
        }

        ReadOnlySpan<char> scopeIdSpan = normalizedPath.AsSpan(Prefix.Length, separatorIndex - Prefix.Length);
        if (!long.TryParse(scopeIdSpan, NumberStyles.None, CultureInfo.InvariantCulture, out long resourceScopeId)
         || resourceScopeId <= 0)
        {
            return false;
        }

        string scopedActualPath = NormalizeActualPath(normalizedPath[(separatorIndex + 1)..]);

        if (scopedActualPath.Length == 0 || !FitsScopedNativePath(resourceScopeId, scopedActualPath))
        {
            return false;
        }

        scopedPath = new ObjectScopedResourcePath(resourceScopeId, scopedActualPath);
        return true;
    }

    public static bool IsForeignScopedPath(string path)
    {
        string normalizedPath = TextUtility.TrimOrEmpty(path);
        return normalizedPath.Length > 0
            && !IsObjectScopedPath(normalizedPath)
            && !ObjectMemoryResourcePathUtility.IsMemoryResourcePath(normalizedPath)
            && (normalizedPath[0] == '|'
                || normalizedPath.Contains(UriMarker, StringComparison.Ordinal));
    }

    public static bool IsObjectScopedPath(string path)
    {
        string normalizedPath = TextUtility.TrimOrEmpty(path);
        return normalizedPath.StartsWith(Prefix, StringComparison.Ordinal)
            || normalizedPath.StartsWith(ObsoletePrefix, StringComparison.Ordinal);
    }

    public static bool IsRejectedPath(string path)
        => TextUtility.TrimOrEmpty(path).StartsWith(RejectedPrefix, StringComparison.Ordinal);

    public static string CreateRejected(long resourceScopeId, string requestedPath)
    {
        if (resourceScopeId < 0)
        {
            return string.Empty;
        }

        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(requestedPath), hash);
        return $"{RejectedPrefix}{resourceScopeId.ToString(CultureInfo.InvariantCulture)}{Separator}{Convert.ToHexString(hash).ToLowerInvariant()}";
    }

    public static string Create(long resourceScopeId, string path)
    {
        if (resourceScopeId < 0)
        {
            return string.Empty;
        }

        string normalizedPath = NormalizeActualPath(path);

        if (resourceScopeId == 0 || normalizedPath.Length == 0)
        {
            return normalizedPath;
        }

        return FitsScopedNativePath(resourceScopeId, normalizedPath)
            ? $"{Prefix}{resourceScopeId.ToString(CultureInfo.InvariantCulture)}{Separator}{normalizedPath}"
            : string.Empty;
    }

    public static string Strip(string path)
        => TryParse(path, out ObjectScopedResourcePath scopedPath)
            ? scopedPath.Path
            : path;

    private static bool FitsScopedNativePath(long resourceScopeId, string path)
    {
        Span<char> scopeId = stackalloc char[20];
        _ = resourceScopeId.TryFormat(scopeId, out int scopeIdLength, provider: CultureInfo.InvariantCulture);
        return ObjectResourcePathEncoding.TryGetNativePathByteCount(path, out int pathByteCount)
            && Prefix.Length + scopeIdLength + 1 + pathByteCount < ObjectResourcePathEncoding.MaxNativePathByteCount;
    }

    private static string NormalizeActualPath(string path)
    {
        string normalizedPath = GameAssetPathRules.NormalizeGamePath(path);

        if (ObjectMemoryResourcePathUtility.IsMemoryResourcePath(normalizedPath))
        {
            return ObjectMemoryResourcePathUtility.Normalize(path);
        }

        if (!ObjectLocalFilePathUtility.IsLocalFilePath(path))
        {
            return ObjectResolvedPath.FromGamePath(normalizedPath).Path;
        }

        if (IsObjectScopedPath(normalizedPath)
            || IsForeignScopedPath(normalizedPath) && !ObjectLocalFilePathUtility.IsLocalFilePath(normalizedPath))
        {
            return string.Empty;
        }

        normalizedPath = ObjectLocalFilePathUtility.NormalizeLocalFilePath(path);
        return IsObjectScopedPath(normalizedPath) || IsForeignScopedPath(normalizedPath)
            ? string.Empty : normalizedPath;
    }
}
