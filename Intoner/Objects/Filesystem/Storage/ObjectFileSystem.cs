namespace Intoner.Objects.Filesystem.Storage;

/// <summary> provides file operations used by object services </summary>
internal interface IObjectFileSystem
{
    /// <summary> gets whether a file exists </summary>
    /// <param name="path">the file path to check</param>
    /// <returns>true when the file exists</returns>
    bool FileExists(string path);

    /// <summary> gets whether a directory exists </summary>
    /// <param name="path">the directory path to check</param>
    /// <returns>true when the directory exists</returns>
    bool DirectoryExists(string path);

    /// <summary> creates a directory when it does not exist </summary>
    /// <param name="path">the directory path to create</param>
    void EnsureDirectory(string path);

    /// <summary> enumerates files in one directory </summary>
    /// <param name="path">the directory path to scan</param>
    /// <param name="pattern">the file name pattern to match </param>
    /// <returns>matched file paths</returns>
    IReadOnlyList<string> EnumerateFiles(string path, string pattern);

    /// <summary> reads all bytes from a file </summary>
    /// <param name="path">the file path to read</param>
    /// <returns>the file contents</returns>
    byte[] ReadAllBytes(string path);

    /// <summary> reads a byte range from a file </summary>
    /// <param name="path">the file path to read</param>
    /// <param name="offset">the byte offset to start reading from</param>
    /// <param name="length">the number of bytes to read</param>
    /// <returns>the requested bytes</returns>
    byte[] ReadBytes(string path, long offset, int length);

    /// <summary> reads all text from a file </summary>
    /// <param name="path">the file path to read</param>
    /// <returns>the file contents</returns>
    string ReadAllText(string path);

    /// <summary> gets the current file length </summary>
    /// <param name="path">the file path to inspect</param>
    /// <returns>the file length in bytes</returns>
    long GetFileLength(string path);

    /// <summary> writes bytes to a file with an atomic replace step </summary>
    /// <param name="path">the final file path</param>
    /// <param name="contents">the bytes to write</param>
    void WriteAllBytesAtomic(string path, ReadOnlyMemory<byte> contents);

    /// <summary> writes text to a file with an atomic replace step </summary>
    /// <param name="path">the final file path</param>
    /// <param name="contents">the text to write</param>
    void WriteAllTextAtomic(string path, string contents);

    /// <summary> removes one file when it exists </summary>
    /// <param name="path"> the file path to remove </param>
    void DeleteFile(string path);

    /// <summary> removes one directory and all nested files when it exists </summary>
    /// <param name="path">the directory path to remove</param>
    void DeleteDirectoryRecursive(string path);
}

internal sealed class ObjectFileSystem : IObjectFileSystem
{
    public bool FileExists(string path)
        => File.Exists(path);

    public bool DirectoryExists(string path)
        => Directory.Exists(path);

    public void EnsureDirectory(string path)
        => Directory.CreateDirectory(path);

    public IReadOnlyList<string> EnumerateFiles(string path, string pattern)
        => Directory.Exists(path)
            ? Directory.EnumerateFiles(path, pattern, SearchOption.TopDirectoryOnly).ToList()
            : [];

    public byte[] ReadAllBytes(string path)
        => File.ReadAllBytes(path);

    public byte[] ReadBytes(string path, long offset, int length)
    {
        byte[] buffer = new byte[length];
        using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            4096,
            FileOptions.SequentialScan);
        stream.Position = offset;

        int bytesRead = 0;
        while (bytesRead < length)
        {
            int read = stream.Read(buffer, bytesRead, length - bytesRead);
            if (read == 0)
            {
                throw new EndOfStreamException($"failed to read {length} bytes from {path} at offset {offset}");
            }

            bytesRead += read;
        }

        return buffer;
    }

    public string ReadAllText(string path)
        => File.ReadAllText(path);

    public long GetFileLength(string path)
        => new FileInfo(path).Length;

    public void WriteAllBytesAtomic(string path, ReadOnlyMemory<byte> contents)
    {
        EnsureParentDirectory(path);
        string tempPath = $"{path}.tmp";

        using (FileStream stream = new(
                   tempPath,
                   FileMode.Create,
                   FileAccess.Write,
                   FileShare.None,
                   4096,
                   FileOptions.SequentialScan))
        {
            stream.Write(contents.Span);
            stream.Flush(flushToDisk: true);
        }

        File.Move(tempPath, path, overwrite: true);
    }

    public void WriteAllTextAtomic(string path, string contents)
    {
        EnsureParentDirectory(path);
        string tempPath = $"{path}.tmp";
        File.WriteAllText(tempPath, contents);
        File.Move(tempPath, path, overwrite: true);
    }

    public void DeleteFile(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    public void DeleteDirectoryRecursive(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private static void EnsureParentDirectory(string path)
    {
        string? directoryPath = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directoryPath))
        {
            Directory.CreateDirectory(directoryPath);
        }
    }
}

