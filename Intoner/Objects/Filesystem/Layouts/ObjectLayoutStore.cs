using Intoner.Objects.Api;
using Intoner.Objects.Filesystem;
using Intoner.Objects.Filesystem.Storage;
using Intoner.Objects.Filesystem.Watching;
using Intoner.Objects.Models;
using Microsoft.Extensions.Logging;

namespace Intoner.Objects.Filesystem.Layouts;

/// <summary> loads, saves, and watches persisted object layout json files </summary>
internal interface IObjectLayoutStore : IDisposable
{
    /// <summary> raised when layout files changed outside the store writer </summary>
    event Action LayoutFilesChanged;

    /// <summary> loads all valid saved layout files from disk </summary>
    /// <returns>the loaded layout snapshots</returns>
    IReadOnlyList<ObjectLayoutSnapshot> LoadLayouts();

    /// <summary> writes one saved layout json file </summary>
    /// <param name="layout">the layout snapshot to write</param>
    void SaveLayout(ObjectLayoutSnapshot layout);

    /// <summary> deletes all saved layout files matching one layout id </summary>
    /// <param name="layoutId">the layout id to delete</param>
    void DeleteLayout(Guid layoutId);
}

internal sealed class ObjectLayoutStore : IObjectLayoutStore
{
    private const string LayoutFileSearchPattern = "*.json";

    private static readonly TimeSpan WatchDebounceDelay = TimeSpan.FromMilliseconds(650);
    private static readonly TimeSpan LocalChangeIgnoreWindow = TimeSpan.FromSeconds(2);

    private readonly ILogger<ObjectLayoutStore> _logger;
    private readonly IObjectStoragePathService _pathService;
    private readonly IObjectFileSystem _fileSystem;
    private readonly IObjectFileWatchSubscription _watchSubscription;
    private readonly Lock _localChangesLock = new();
    private readonly Dictionary<string, DateTime> _localChangePaths = new(StringComparer.OrdinalIgnoreCase);

    private Action? _layoutFilesChanged;
    private bool _disposed;

    public ObjectLayoutStore(
        ILogger<ObjectLayoutStore> logger,
        IObjectStoragePathService pathService,
        IObjectFileSystem fileSystem,
        IObjectFileWatcherService fileWatcherService)
    {
        _logger = logger;
        _pathService = pathService;
        _fileSystem = fileSystem;

        _fileSystem.EnsureDirectory(_pathService.ObjectLayoutsPath);
        _watchSubscription = fileWatcherService.Watch(
            new ObjectFileWatchOptions(
                _pathService.ObjectLayoutsPath,
                LayoutFileSearchPattern,
                IncludeSubdirectories: false,
                DebounceDelay: WatchDebounceDelay),
            HandleLayoutFileChanges);
    }

    public event Action LayoutFilesChanged
    {
        add => _layoutFilesChanged += value;
        remove => _layoutFilesChanged -= value;
    }

    public IReadOnlyList<ObjectLayoutSnapshot> LoadLayouts()
    {
        Dictionary<Guid, ObjectLayoutSnapshot> layoutsById = [];
        foreach (string path in EnumerateLayoutFiles())
        {
            if (!TryLoadLayout(path, out ObjectLayoutSnapshot layout))
            {
                continue;
            }

            if (!layoutsById.TryGetValue(layout.Id, out ObjectLayoutSnapshot? existingLayout)
                || layout.UpdatedAtUtc >= existingLayout.UpdatedAtUtc)
            {
                layoutsById[layout.Id] = layout;
            }
        }

        return layoutsById.Values
            .OrderBy(static layout => layout.CreatedAtUtc)
            .ThenBy(static layout => layout.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public void SaveLayout(ObjectLayoutSnapshot layout)
    {
        string path = BuildLayoutPath(layout.Id);
        try
        {
            MarkLocalChange(path);
            _fileSystem.WriteAllTextAtomic(path, ObjectLayoutJsonSerializer.SerializeLayout(layout));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "failed to save object layout file {Path}", path);
        }
    }

    public void DeleteLayout(Guid layoutId)
    {
        string canonicalPath = BuildLayoutPath(layoutId);
        DeleteLayoutFile(canonicalPath, duplicate: false);

        foreach (string path in EnumerateLayoutFiles())
        {
            if (ObjectFilePathUtility.PathsMatch(path, canonicalPath)
                || !TryLoadLayout(path, out ObjectLayoutSnapshot layout)
                || layout.Id != layoutId)
            {
                continue;
            }

            DeleteLayoutFile(path, duplicate: true);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _watchSubscription.Dispose();
    }

    private bool TryLoadLayout(string path, out ObjectLayoutSnapshot layout)
    {
        layout = null!;

        try
        {
            string json = _fileSystem.ReadAllText(path);
            if (ObjectLayoutJsonSerializer.TryDeserializeLayout(json, out layout, out string errorMessage))
            {
                return true;
            }

            _logger.LogWarning("skipping object layout file {Path}: {Reason}", path, errorMessage);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "failed to load object layout file {Path}", path);
            return false;
        }
    }

    private IReadOnlyList<string> EnumerateLayoutFiles()
    {
        try
        {
            return _fileSystem
                .EnumerateFiles(_pathService.ObjectLayoutsPath, LayoutFileSearchPattern)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "failed to enumerate object layout files in {Path}", _pathService.ObjectLayoutsPath);
            return [];
        }
    }

    private void DeleteLayoutFile(string path, bool duplicate)
    {
        if (!_fileSystem.FileExists(path))
        {
            return;
        }

        MarkLocalChange(path);
        try
        {
            _fileSystem.DeleteFile(path);
        }
        catch (Exception ex)
        {
            if (duplicate)
            {
                _logger.LogWarning(ex, "failed to delete duplicate object layout file {Path}", path);
                return;
            }

            _logger.LogWarning(ex, "failed to delete object layout file {Path}", path);
        }
    }

    private void HandleLayoutFileChanges(IReadOnlyList<ObjectFileChange> changes)
    {
        if (_disposed || changes.Count == 0 || ShouldIgnoreChanges(changes))
        {
            return;
        }

        _layoutFilesChanged?.Invoke();
    }

    private bool ShouldIgnoreChanges(IReadOnlyList<ObjectFileChange> changes)
    {
        DateTime now = DateTime.UtcNow;
        lock (_localChangesLock)
        {
            RemoveExpiredLocalChanges(now);
            foreach (ObjectFileChange change in changes)
            {
                if (IsExternalChange(change))
                {
                    return false;
                }
            }

            return true;
        }
    }

    private bool IsExternalChange(ObjectFileChange change)
    {
        if (!IsLocalChange(change.Path))
        {
            return true;
        }

        return HasExternalOldPath(change);
    }

    private bool HasExternalOldPath(ObjectFileChange change)
    {
        if (string.IsNullOrWhiteSpace(change.OldPath) || !IsJsonPath(change.OldPath))
        {
            return false;
        }

        return !IsLocalChange(change.OldPath);
    }

    private bool IsLocalChange(string path)
        => _localChangePaths.ContainsKey(ObjectFilePathUtility.NormalizeFullPath(path));

    private static bool IsJsonPath(string path)
        => string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase);

    private void MarkLocalChange(string path)
    {
        DateTime now = DateTime.UtcNow;
        lock (_localChangesLock)
        {
            RemoveExpiredLocalChanges(now);
            _localChangePaths[ObjectFilePathUtility.NormalizeFullPath(path)] = now + LocalChangeIgnoreWindow;
        }
    }

    private void RemoveExpiredLocalChanges(DateTime now)
    {
        foreach (string path in _localChangePaths.Where(entry => entry.Value <= now).Select(static entry => entry.Key).ToArray())
        {
            _localChangePaths.Remove(path);
        }
    }

    private string BuildLayoutPath(Guid layoutId)
        => Path.Combine(_pathService.ObjectLayoutsPath, $"{layoutId:D}.json");
}

