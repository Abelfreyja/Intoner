using System.Security.Cryptography;

namespace Intoner.Objects.Assets;

internal static class ObjectAssetHashUtility
{
    public static string ComputeSha256Hex(ReadOnlySpan<byte> value)
        => Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
}

