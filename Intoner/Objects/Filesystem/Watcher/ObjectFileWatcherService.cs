using Intoner.Objects.Filesystem;
using Microsoft.Extensions.Logging;

namespace Intoner.Objects.Filesystem.Watching;

internal enum ObjectFileChangeKind
{
    Created,
    Changed,
    Deleted,
    Renamed,
    Error,
}

internal sealed record ObjectFileWatchOptions(
    string DirectoryPath,
    string Filter,
    bool IncludeSubdirectories,
    TimeSpan DebounceDelay,
    NotifyFilters NotifyFilter = NotifyFilters.FileName
                                | NotifyFilters.LastWrite
                                | NotifyFilters.CreationTime
                                | NotifyFilters.Size);

internal readonly record struct ObjectFileChange(
    ObjectFileChangeKind Kind,
    string Path,
    string? OldPath = null);

/// <summary> owns one active file watch subscription </summary>
internal interface IObjectFileWatchSubscription : IDisposable
{
}

/// <summary> creates reusable debounced file watchers for object subsystem storage </summary>
internal interface IObjectFileWatcherService
{
    /// <summary> watches one directory and reports debounced file change batches </summary>
    /// <param name="options">directory and filter options for the watcher</param>
    /// <param name="onChanged">callback invoked with a debounced change batch</param>
    /// <returns>the active subscription to dispose when watching should stop</returns>
    IObjectFileWatchSubscription Watch(ObjectFileWatchOptions options, Action<IReadOnlyList<ObjectFileChange>> onChanged);
}

internal sealed class ObjectFileWatcherService : IObjectFileWatcherService
{
    private readonly ILogger<ObjectFileWatcherService> _logger;

    public ObjectFileWatcherService(ILogger<ObjectFileWatcherService> logger)
    {
        _logger = logger;
    }

    public IObjectFileWatchSubscription Watch(ObjectFileWatchOptions options, Action<IReadOnlyList<ObjectFileChange>> onChanged)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(onChanged);

        return new ObjectFileWatchSubscription(_logger, options, onChanged);
    }

    private sealed class ObjectFileWatchSubscription : IObjectFileWatchSubscription
    {
        private readonly ILogger _logger;
        private readonly ObjectFileWatchOptions _options;
        private readonly Action<IReadOnlyList<ObjectFileChange>> _onChanged;
        private readonly Lock _lock = new();
        private readonly Dictionary<string, ObjectFileChange> _pendingChanges = new(StringComparer.OrdinalIgnoreCase);
        private readonly FileSystemWatcher _watcher;
        private readonly Timer _debounceTimer;

        private bool _disposed;

        public ObjectFileWatchSubscription(
            ILogger logger,
            ObjectFileWatchOptions options,
            Action<IReadOnlyList<ObjectFileChange>> onChanged)
        {
            _logger = logger;
            _options = options;
            _onChanged = onChanged;

            Directory.CreateDirectory(_options.DirectoryPath);
            _debounceTimer = new Timer(FlushChanges);
            _watcher = new FileSystemWatcher(_options.DirectoryPath, _options.Filter)
            {
                IncludeSubdirectories = _options.IncludeSubdirectories,
                NotifyFilter = _options.NotifyFilter,
            };

            _watcher.Created += HandleCreated;
            _watcher.Changed += HandleChanged;
            _watcher.Deleted += HandleDeleted;
            _watcher.Renamed += HandleRenamed;
            _watcher.Error += HandleError;
            _watcher.EnableRaisingEvents = true;
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
            }

            _watcher.EnableRaisingEvents = false;
            _watcher.Created -= HandleCreated;
            _watcher.Changed -= HandleChanged;
            _watcher.Deleted -= HandleDeleted;
            _watcher.Renamed -= HandleRenamed;
            _watcher.Error -= HandleError;
            _watcher.Dispose();
            _debounceTimer.Dispose();
        }

        private void HandleCreated(object sender, FileSystemEventArgs args)
            => QueueChange(new ObjectFileChange(ObjectFileChangeKind.Created, ObjectFilePathUtility.NormalizeFullPath(args.FullPath)));

        private void HandleChanged(object sender, FileSystemEventArgs args)
            => QueueChange(new ObjectFileChange(ObjectFileChangeKind.Changed, ObjectFilePathUtility.NormalizeFullPath(args.FullPath)));

        private void HandleDeleted(object sender, FileSystemEventArgs args)
            => QueueChange(new ObjectFileChange(ObjectFileChangeKind.Deleted, ObjectFilePathUtility.NormalizeFullPath(args.FullPath)));

        private void HandleRenamed(object sender, RenamedEventArgs args)
            => QueueChange(new ObjectFileChange(
                ObjectFileChangeKind.Renamed,
                ObjectFilePathUtility.NormalizeFullPath(args.FullPath),
                ObjectFilePathUtility.NormalizeFullPath(args.OldFullPath)));

        private void HandleError(object sender, ErrorEventArgs args)
        {
            _logger.LogWarning(args.GetException(), "object file watcher failed for {Directory}", _options.DirectoryPath);
            QueueChange(new ObjectFileChange(ObjectFileChangeKind.Error, ObjectFilePathUtility.NormalizeFullPath(_options.DirectoryPath)));
        }

        private void QueueChange(ObjectFileChange change)
        {
            lock (_lock)
            {
                if (_disposed)
                {
                    return;
                }

                _pendingChanges[change.Path] = change;
                if (!string.IsNullOrWhiteSpace(change.OldPath))
                {
                    _pendingChanges[change.OldPath] = new ObjectFileChange(ObjectFileChangeKind.Deleted, change.OldPath);
                }

                _debounceTimer.Change(_options.DebounceDelay, Timeout.InfiniteTimeSpan);
            }
        }

        private void FlushChanges(object? state)
        {
            ObjectFileChange[] changes;
            lock (_lock)
            {
                if (_disposed || _pendingChanges.Count == 0)
                {
                    return;
                }

                changes = _pendingChanges.Values.ToArray();
                _pendingChanges.Clear();
            }

            try
            {
                _onChanged(changes);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "object file watcher callback failed for {Directory}", _options.DirectoryPath);
            }
        }

    }
}

