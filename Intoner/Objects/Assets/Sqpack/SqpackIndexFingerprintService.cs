using Microsoft.Extensions.Logging;
using System.Text;

namespace Intoner.Objects.Assets;

/// <summary> resolves a local sqpack index fingerprint for object asset cache invalidation (helps with things like installing (uninstalling as well) new expansions) </summary>
internal interface ISqpackIndexFingerprintService
{
    /// <summary> gets the current sqpack index fingerprint when it can be resolved </summary>
    /// <returns>the current sqpack index fingerprint, or an empty string when unavailable</returns>
    string GetCurrentSqpackIndexFingerprint();
}

internal sealed class SqpackIndexFingerprintService : ISqpackIndexFingerprintService
{
    private const int HeaderBytesToHash = 0x1000;

    private readonly ILogger<SqpackIndexFingerprintService> _logger;
    private readonly Lock _lock = new();
    private string? _sqpackIndexFingerprint;

    public SqpackIndexFingerprintService(ILogger<SqpackIndexFingerprintService> logger)
    {
        _logger = logger;
    }

    public string GetCurrentSqpackIndexFingerprint()
    {
        lock (_lock)
        {
            if (_sqpackIndexFingerprint is not null)
            {
                return _sqpackIndexFingerprint;
            }

            _sqpackIndexFingerprint = BuildSqpackIndexFingerprint();
            return _sqpackIndexFingerprint;
        }
    }

    private string BuildSqpackIndexFingerprint()
    {
        try
        {
            if (!SqpackIndexFileSystem.TryResolveSqpackRoot(out string sqpackRoot))
            {
                return string.Empty;
            }

            string[] indexFilePaths = SqpackIndexFileSystem.EnumerateIndexFiles(sqpackRoot).ToArray();
            if (indexFilePaths.Length == 0)
            {
                return string.Empty;
            }

            using MemoryStream stream = new();
            using BinaryWriter writer = new(stream, Encoding.UTF8, leaveOpen: true);
            writer.Write7BitEncodedInt(indexFilePaths.Length);
            foreach (string indexFilePath in indexFilePaths)
            {
                if (!TryWriteIndexFileFingerprint(writer, sqpackRoot, indexFilePath))
                {
                    return string.Empty;
                }
            }

            writer.Flush();
            return ObjectAssetHashUtility.ComputeSha256Hex(stream.ToArray());
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "failed to build sqpack index fingerprint for object assets");
            return string.Empty;
        }
    }

    private bool TryWriteIndexFileFingerprint(BinaryWriter writer, string sqpackRoot, string indexFilePath)
    {
        try
        {
            FileInfo fileInfo = new(indexFilePath);
            if (!fileInfo.Exists)
            {
                return false;
            }

            writer.Write(SqpackIndexFileSystem.GetRelativeIndexPath(sqpackRoot, indexFilePath));
            writer.Write(fileInfo.Length);
            writer.Write(fileInfo.LastWriteTimeUtc.Ticks);
            writer.Write(ComputeIndexHeaderHash(indexFilePath, fileInfo.Length));
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "failed to fingerprint sqpack index file {IndexFilePath}", indexFilePath);
            return false;
        }
    }

    private static string ComputeIndexHeaderHash(string indexFilePath, long fileLength)
    {
        int headerLength = (int)Math.Min(HeaderBytesToHash, Math.Max(fileLength, 0));
        if (headerLength == 0)
        {
            return ObjectAssetHashUtility.ComputeSha256Hex(ReadOnlySpan<byte>.Empty);
        }

        byte[] header = new byte[headerLength];
        using FileStream stream = ObjectAssetFileUtility.OpenSharedRead(indexFilePath);
        int bytesRead = stream.Read(header, 0, header.Length);
        return ObjectAssetHashUtility.ComputeSha256Hex(header.AsSpan(0, bytesRead));
    }
}

