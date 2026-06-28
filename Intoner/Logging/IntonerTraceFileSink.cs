using System.Text;

namespace Intoner.Logging;

internal sealed class IntonerTraceFileSink : IDisposable
{
    private const string FilePrefix = "intoner-trace";
    private static readonly Encoding FileEncoding = new UTF8Encoding(false);

    private readonly Lock _lock = new();
    private readonly IntonerLogOptions _options;
    private readonly string _sessionStamp = DateTime.Now.ToString("yyyy-MM-dd-HH-mm-ss");

    private StreamWriter? _writer;
    private string? _currentPath;
    private long _currentFileBytes;
    private int _fileIndex;
    private bool _disposed;

    public IntonerTraceFileSink(IntonerLogOptions options)
    {
        _options = options;
        Directory.CreateDirectory(_options.TraceDirectory);
        CleanupOldTraceFiles();
    }

    public string? CurrentPath => _currentPath;

    public void WriteLine(string line)
    {
        if (_disposed)
        {
            return;
        }

        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            EnsureWriter();

            var bytes = FileEncoding.GetByteCount(line) + FileEncoding.GetByteCount(Environment.NewLine);
            if (_currentFileBytes > 0 && _currentFileBytes + bytes > _options.TraceFileSizeLimitBytes)
            {
                OpenNextWriter();
            }

            _writer!.WriteLine(line);
            _writer.Flush();
            _currentFileBytes += bytes;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _writer?.Dispose();
            _writer = null;
        }
    }

    private void EnsureWriter()
    {
        if (_writer is null)
        {
            OpenNextWriter();
        }
    }

    private void OpenNextWriter()
    {
        _writer?.Dispose();

        _currentPath = BuildPath(_fileIndex++);
        var stream = new FileStream(_currentPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        _writer = new StreamWriter(stream, FileEncoding);
        _currentFileBytes = stream.Length;
        CleanupOldTraceFiles();
    }

    private string BuildPath(int index)
    {
        var suffix = index == 0 ? string.Empty : $"-{index:D3}";
        return Path.Combine(_options.TraceDirectory, $"{FilePrefix}-{_sessionStamp}{suffix}.log");
    }

    private void CleanupOldTraceFiles()
    {
        if (_options.TraceFileRetentionCount <= 0)
        {
            return;
        }

        try
        {
            foreach (var file in Directory.EnumerateFiles(_options.TraceDirectory, $"{FilePrefix}-*.log")
                         .Select(static path => new FileInfo(path))
                         .OrderByDescending(static file => file.LastWriteTimeUtc)
                         .Skip(_options.TraceFileRetentionCount))
            {
                TryDelete(file);
            }
        }
        catch
        {
            // logging must not break plugin startup
        }
    }

    private static void TryDelete(FileInfo file)
    {
        try
        {
            file.Delete();
        }
        catch
        {
            // best effort retention cleanup
        }
    }
}
