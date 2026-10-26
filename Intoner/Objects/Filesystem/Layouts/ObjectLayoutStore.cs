using Intoner.Objects.Api;
using Intoner.Objects.Filesystem.Watching;
using Intoner.Objects.Models;
using Intoner.Services.Serialization;
using Intoner.Services.Storage;
using Microsoft.Extensions.Logging;

namespace Intoner.Objects.Filesystem.Layouts;

/// <summary> loads, saves, and watches persisted object layout json files </summary>
internal interface IObjectLayoutStore : IDisposable
{
    /// <summary> raised when layout files changed outside the store writer </summary>
    event Action LayoutFilesChanged;

    /// <summary> loads all valid saved layout files from disk </summary>
    /// <returns>the loaded layout snapshots</returns>
    /// <exception cref="IOException"> a saved layout or its directory could not be read </exception>
    /// <exception cref="UnauthorizedAccessException"> access to saved layouts was denied </exception>
    IReadOnlyList<ObjectLayoutSnapshot> LoadLayouts();

    /// <summary> writes one saved layout json file </summary>
    /// <param name="layout">the layout snapshot to write</param>
    /// <returns>true when the file was written</returns>
    bool TrySaveLayout(ObjectLayoutSnapshot layout);

    /// <summary> deletes all saved layout files matching one layout id </summary>
    /// <param name="layoutId">the layout id to delete</param>
    /// <returns>true when every matching file was deleted</returns>
    bool TryDeleteLayout(Guid layoutId);
}

internal sealed class ObjectLayoutStore : IObjectLayoutStore
{
    private const string LayoutFileSearchPattern = "*.json";

    private static readonly TimeSpan WatchDebounceDelay = TimeSpan.FromMilliseconds(650);

    private readonly ILogger<ObjectLayoutStore> _logger;
    private readonly IPluginStoragePaths _pathService;
    private readonly IPluginFileSystem _fileSystem;
    private readonly IObjectFileWatchSubscription _watchSubscription;
    private readonly LocalObjectFileChangeTracker _localChanges;

    private Action? _layoutFilesChanged;
    private bool _disposed;

    public ObjectLayoutStore(
        ILogger<ObjectLayoutStore> logger,
        IPluginStoragePaths pathService,
        IPluginFileSystem fileSystem,
        IObjectFileWatcherService fileWatcherService)
    {
        _logger = logger;
        _pathService = pathService;
        _fileSystem = fileSystem;
        _localChanges = new LocalObjectFileChangeTracker(fileSystem, IsJsonPath);

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
                || layout.Revision > existingLayout.Revision
                || layout.Revision == existingLayout.Revision && layout.UpdatedAtUtc >= existingLayout.UpdatedAtUtc)
            {
                layoutsById[layout.Id] = layout;
            }
        }

        return layoutsById.Values
            .OrderBy(static layout => layout.CreatedAtUtc)
            .ThenBy(static layout => layout.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public bool TrySaveLayout(ObjectLayoutSnapshot layout)
    {
        string path = BuildLayoutPath(layout.Id);
        try
        {
            string contents = ObjectLayoutJsonSerializer.SerializeLayout(layout);
            _localChanges.TrackWrite(path, contents);
            JsonDocumentUtility.WriteFile(_fileSystem, path, contents);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "failed to save object layout file {Path}", path);
            _localChanges.Forget(path);
            return false;
        }
    }

    public bool TryDeleteLayout(Guid layoutId)
    {
        string canonicalPath = BuildLayoutPath(layoutId);
        List<string> duplicatePaths = [];
        foreach (string path in EnumerateLayoutFiles())
        {
            if (!ObjectFilePathUtility.PathsMatch(path, canonicalPath)
                && TryLoadLayout(path, out ObjectLayoutSnapshot layout)
                && layout.Id == layoutId)
            {
                duplicatePaths.Add(path);
            }
        }

        foreach (string path in duplicatePaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!TryDeleteLayoutFile(path))
            {
                return false;
            }
        }

        return TryDeleteLayoutFile(canonicalPath);
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

        string json;
        try
        {
            json = JsonDocumentUtility.ReadFile(_fileSystem, path);
        }
        catch (InvalidDataException ex)
        {
            _logger.LogWarning("skipping object layout file {Path}: {Reason}", path, ex.Message);
            return false;
        }
        if (ObjectLayoutJsonSerializer.TryDeserializeLayout(json, out layout, out string errorMessage))
        {
            return true;
        }

        _logger.LogWarning("skipping object layout file {Path}: {Reason}", path, errorMessage);
        return false;
    }

    private IReadOnlyList<string> EnumerateLayoutFiles()
        => _fileSystem
            .EnumerateFiles(_pathService.ObjectLayoutsPath, LayoutFileSearchPattern)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private bool TryDeleteLayoutFile(string path)
    {
        if (!_fileSystem.FileExists(path))
        {
            return true;
        }

        _localChanges.TrackDelete(path);
        try
        {
            _fileSystem.DeleteFile(path);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "failed to delete object layout file {Path}", path);
            _localChanges.Forget(path);
            return false;
        }
    }

    private void HandleLayoutFileChanges(IReadOnlyList<ObjectFileChange> changes)
    {
        if (_disposed || changes.Count == 0 || !_localChanges.HasExternalChanges(changes))
        {
            return;
        }

        _layoutFilesChanged?.Invoke();
    }

    private static bool IsJsonPath(string path)
        => string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase);

    private string BuildLayoutPath(Guid layoutId)
        => Path.Combine(_pathService.ObjectLayoutsPath, $"{layoutId:D}.json");
}
