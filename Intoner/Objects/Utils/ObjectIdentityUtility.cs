using System.Security.Cryptography;
using System.Text;

namespace Intoner.Objects.Utils;

internal static class ObjectIdentityUtility
{
    public static Guid CreateTemporaryObjectId(string sourceKey, Guid objectId)
    {
        var input = Encoding.UTF8.GetBytes($"{sourceKey}:{objectId:D}");
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(input, hash);
        return new Guid(hash[..16]);
    }

    public static string CreateTemporaryCollectionId(string sourceKey, string collectionId)
    {
        string normalizedSourceKey = ObjectTemporarySourceUtility.NormalizeSourceKey(sourceKey);
        string normalizedCollectionId = ObjectCollectionKeyUtility.NormalizeCollectionId(collectionId);
        if (normalizedSourceKey.Length == 0 || normalizedCollectionId.Length == 0)
        {
            return string.Empty;
        }

        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes($"{normalizedSourceKey}:{normalizedCollectionId}"), hash);
        return $"tmpcol_{Convert.ToHexString(hash[..16]).ToLowerInvariant()}";
    }
}

