using Intoner.Objects.Utils;
using Lumina.Data;
using System.Buffers;

namespace Intoner.Objects.Assets;

internal static class ObjectAssetFileUtility
{
    public static readonly TimeSpan AssetReadRetryDelay = TimeSpan.FromSeconds(45);

    public static bool IsReadFailure(Exception exception)
        => exception is IOException or InvalidDataException or UnauthorizedAccessException or SqPackInflateException;

    public static ObjectAssetFileData ReadGameFile(IObjectAssetGameData gameData, string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        bool exists = gameData.FileExists(path);
        cancellationToken.ThrowIfCancellationRequested();

        if (!exists)
        {
            return new(null, true);
        }

        FileResource? file = gameData.GetFile(path);
        cancellationToken.ThrowIfCancellationRequested();
        return new(file?.Data, file is not null);
    }

    public static FileStream OpenSharedRead(string path)
        => new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            4096,
            FileOptions.SequentialScan);

    public static byte[]? TryReadLocalFileBytes(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

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
            byte[] readBuffer = ArrayPool<byte>.Shared.Rent(81920);
            try
            {
                int bytesRead;
                while ((bytesRead = stream.Read(readBuffer)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    buffer.Write(readBuffer, 0, bytesRead);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(readBuffer);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return buffer.ToArray();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (IsReadFailure(ex))
        {
            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }
    }
}

internal readonly record struct ObjectAssetFileData(byte[]? Data, bool IsComplete);
