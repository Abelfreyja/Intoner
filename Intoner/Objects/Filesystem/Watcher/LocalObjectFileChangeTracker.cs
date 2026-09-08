using Intoner.Services.Storage;

namespace Intoner.Objects.Filesystem.Watching;

/// <summary> distinguishes expected local file writes from external watcher events </summary>
internal sealed class LocalObjectFileChangeTracker
{
    private static readonly TimeSpan DefaultTrackingWindow = TimeSpan.FromSeconds(2);

    private readonly IPluginFileSystem _files;
    private readonly Func<string, bool> _isTrackedPath;
    private readonly TimeSpan _trackingWindow;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, LocalFileState> _localChanges = new(StringComparer.OrdinalIgnoreCase);

    public LocalObjectFileChangeTracker(
        IPluginFileSystem files,
        Func<string, bool> isTrackedPath,
        TimeSpan? trackingWindow = null)
    {
        _files = files;
        _isTrackedPath = isTrackedPath;
        _trackingWindow = trackingWindow ?? DefaultTrackingWindow;
    }

    public void TrackWrite(string path, string contents)
        => Track(path, contents);

    public void TrackDelete(string path)
        => Track(path, contents: null);

    public void Forget(string path)
    {
        lock (_lock)
        {
            _localChanges.Remove(ObjectFilePathUtility.NormalizeFullPath(path));
        }
    }

    public bool HasExternalChanges(IReadOnlyList<ObjectFileChange> changes)
    {
        DateTime now = DateTime.UtcNow;
        lock (_lock)
        {
            RemoveExpired(now);
            return changes.Any(IsExternalChange);
        }
    }

    private void Track(string path, string? contents)
    {
        DateTime now = DateTime.UtcNow;
        lock (_lock)
        {
            RemoveExpired(now);
            _localChanges[ObjectFilePathUtility.NormalizeFullPath(path)] = new(
                now + _trackingWindow,
                contents);
        }
    }

    private bool IsExternalChange(ObjectFileChange change)
    {
        if (!IsExpectedState(change.Path))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(change.OldPath)
            && _isTrackedPath(change.OldPath)
            && !IsExpectedState(change.OldPath);
    }

    private bool IsExpectedState(string path)
    {
        string normalizedPath = ObjectFilePathUtility.NormalizeFullPath(path);
        if (!_localChanges.TryGetValue(normalizedPath, out LocalFileState expected))
        {
            return false;
        }

        try
        {
            if (expected.Contents is null)
            {
                return !_files.FileExists(normalizedPath);
            }

            return _files.FileExists(normalizedPath)
                && string.Equals(_files.ReadAllText(normalizedPath), expected.Contents, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private void RemoveExpired(DateTime now)
    {
        foreach (string path in _localChanges
                     .Where(entry => entry.Value.ExpiresAtUtc <= now)
                     .Select(static entry => entry.Key)
                     .ToArray())
        {
            _localChanges.Remove(path);
        }
    }

    private readonly record struct LocalFileState(DateTime ExpiresAtUtc, string? Contents);
}
