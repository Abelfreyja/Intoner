using Intoner.Objects.Utils;

namespace Intoner.Objects.Assets;

internal static class ObjectAssetFileUtility
{
    public static FileStream OpenSharedRead(string path)
        => new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            4096,
            FileOptions.SequentialScan);

    public static byte[]? TryReadLocalFileBytes(string path)
    {
        try
        {
            string normalizedPath = ObjectLocalFilePathUtility.NormalizeLocalFilePath(path);
            if (normalizedPath.Length == 0)
            {
                return null;
            }

            string fileSystemPath = ObjectLocalFilePathUtility.ToFileSystemPath(normalizedPath);
            if (!File.Exists(fileSystemPath))
            {
                return null;
            }

            using FileStream stream = OpenSharedRead(fileSystemPath);
            using MemoryStream buffer = new();
            stream.CopyTo(buffer);
            return buffer.ToArray();
        }
        catch
        {
            return null;
        }
    }
}

