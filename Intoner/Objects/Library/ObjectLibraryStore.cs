using Intoner.Objects.Filesystem;
using Intoner.Objects.Filesystem.Watching;
using Intoner.Services.Storage;
using Microsoft.Extensions.Logging;
using System.Diagnostics.CodeAnalysis;

namespace Intoner.Objects.Library;

/// <summary> persists and watches object library documents </summary>
internal interface IObjectLibraryStore : IDisposable
{
    /// <summary> raised when library files changed outside this store </summary>
    event Action FilesChanged;

    /// <summary> loads the current library content from disk </summary>
    /// <returns>the normalized library content</returns>
    ObjectLibraryContent Load();

    /// <summary> writes a complete library change </summary>
    /// <param name="before">the currently published library</param>
    /// <param name="after">the content to publish</param>
    /// <returns>true when every required file operation completed</returns>
    bool TrySave(ObjectLibrarySnapshot before, ObjectLibraryContent after);
}

internal sealed class ObjectLibraryStore : IObjectLibraryStore
{
    private const string JsonFilePattern = "*.json";

    private static readonly TimeSpan WatchDebounceDelay = TimeSpan.FromMilliseconds(650);

    private readonly ILogger<ObjectLibraryStore> _logger;
    private readonly IPluginStoragePaths _paths;
    private readonly IPluginFileSystem _files;
    private readonly IObjectFileWatchSubscription _watchSubscription;
    private readonly LocalObjectFileChangeTracker _localChanges;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, ObjectLibraryPrefabDocument> _validPrefabs =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _changedPaths = new(StringComparer.OrdinalIgnoreCase);

    private ObjectLibraryStateDocument? _validState;
    private Action? _filesChanged;
    private bool _reloadAll = true;
    private bool _disposed;

    public ObjectLibraryStore(
        ILogger<ObjectLibraryStore> logger,
        IPluginStoragePaths paths,
        IPluginFileSystem files,
        IObjectFileWatcherService fileWatcherService)
    {
        _logger = logger;
        _paths = paths;
        _files = files;
        _localChanges = new LocalObjectFileChangeTracker(files, IsLibraryJsonPath);

        _files.EnsureDirectory(_paths.ObjectLibraryRootPath);
        _files.EnsureDirectory(_paths.ObjectLibraryPrefabsPath);
        _watchSubscription = fileWatcherService.Watch(
            new ObjectFileWatchOptions(
                _paths.ObjectLibraryRootPath,
                JsonFilePattern,
                IncludeSubdirectories: true,
                DebounceDelay: WatchDebounceDelay),
            HandleFileChanges);
    }

    public event Action FilesChanged
    {
        add
        {
            lock (_lock)
            {
                if (!_disposed)
                {
                    _filesChanged += value;
                }
            }
        }
        remove
        {
            lock (_lock)
            {
                _filesChanged -= value;
            }
        }
    }

    public ObjectLibraryContent Load()
    {
        lock (_lock)
        {
            bool reloadAll = _reloadAll;
            string[] changedPaths = _changedPaths.ToArray();
            ObjectLibraryStateDocument state = reloadAll
                || changedPaths.Any(path => ObjectFilePathUtility.PathsMatch(path, _paths.ObjectLibraryStatePath))
                    ? LoadState()
                    : _validState ?? ObjectLibraryDocuments.EmptyState;
            IReadOnlyList<ObjectLibraryPrefabFile> prefabFiles = LoadPrefabs(reloadAll, changedPaths);
            ObjectLibraryContent content = ObjectLibraryDocuments.CreateContent(
                state,
                prefabFiles,
                out IReadOnlyList<ObjectLibraryPrefabConflict> conflicts);
            foreach (ObjectLibraryPrefabConflict conflict in conflicts)
            {
                _logger.LogWarning(
                    "ignoring object library prefab {Path} because {Reason} is already in use",
                    conflict.Path,
                    conflict.Reason);
            }

            _reloadAll = false;
            _changedPaths.Clear();
            return content;
        }
    }

    public bool TrySave(ObjectLibrarySnapshot before, ObjectLibraryContent after)
    {
        lock (_lock)
        {
            Dictionary<string, string?> originalContents = new(StringComparer.OrdinalIgnoreCase);
            List<string> appliedPaths = [];

            try
            {
                ObjectLibrarySavePlan plan = ObjectLibraryDocuments.CreateSavePlan(
                    before,
                    after,
                    _paths.ObjectLibraryStatePath,
                    _paths.ObjectLibraryPrefabsPath,
                    _files.FileExists(_paths.ObjectLibraryStatePath));
                originalContents = CaptureContents(plan.Changes.Select(static change => change.Path));

                foreach (ObjectLibraryDocumentChange change in plan.Changes)
                {
                    if (change.Contents is { } contents)
                    {
                        if (originalContents.TryGetValue(change.Path, out string? original)
                         && string.Equals(original, contents, StringComparison.Ordinal))
                        {
                            continue;
                        }

                        appliedPaths.Add(change.Path);
                        _localChanges.TrackWrite(change.Path, contents);
                        _files.WriteAllTextAtomic(change.Path, contents);
                        continue;
                    }

                    if (!_files.FileExists(change.Path))
                    {
                        continue;
                    }

                    appliedPaths.Add(change.Path);
                    _localChanges.TrackDelete(change.Path);
                    _files.DeleteFile(change.Path);
                }

                UpdateValidDocuments(plan);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "failed to save object library documents");
                RestoreFiles(appliedPaths, originalContents);
                return false;
            }
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
            _filesChanged = null;
        }

        _watchSubscription.Dispose();
    }

    private ObjectLibraryStateDocument LoadState()
    {
        if (!_files.FileExists(_paths.ObjectLibraryStatePath))
        {
            _validState = ObjectLibraryDocuments.EmptyState;
            return _validState;
        }

        if (TryReadDocument(
                _paths.ObjectLibraryStatePath,
                ObjectLibraryDocuments.DeserializeState,
                ObjectLibraryDocuments.IsValid,
                out ObjectLibraryStateDocument? document))
        {
            _validState = document;
        }
        else
        {
            _logger.LogWarning(
                _validState is null
                    ? "ignoring an invalid object library index"
                    : "keeping the last valid object library index after an invalid file update");
        }

        return _validState ?? ObjectLibraryDocuments.EmptyState;
    }

    private IReadOnlyList<ObjectLibraryPrefabFile> LoadPrefabs(
        bool reloadAll,
        IReadOnlyList<string> changedPaths)
    {
        if (reloadAll)
        {
            ReloadAllPrefabs();
        }
        else
        {
            foreach (string path in changedPaths.Where(IsPrefabJsonPath))
            {
                if (_files.FileExists(path))
                {
                    ReloadPrefab(path);
                }
                else
                {
                    _validPrefabs.Remove(ObjectFilePathUtility.NormalizeFullPath(path));
                }
            }
        }

        return _validPrefabs
            .OrderBy(static entry => entry.Key, StringComparer.OrdinalIgnoreCase)
            .Select(static entry => new ObjectLibraryPrefabFile(entry.Key, entry.Value))
            .ToArray();
    }

    private void ReloadAllPrefabs()
    {
        IReadOnlyList<string> paths;
        try
        {
            paths = _files.EnumerateFiles(_paths.ObjectLibraryPrefabsPath, JsonFilePattern);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "failed to enumerate object library prefab files");
            return;
        }

        HashSet<string> existingPaths = paths
            .Select(ObjectFilePathUtility.NormalizeFullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string deletedPath in _validPrefabs.Keys
                     .Except(existingPaths, StringComparer.OrdinalIgnoreCase)
                     .ToArray())
        {
            _validPrefabs.Remove(deletedPath);
        }
        foreach (string path in existingPaths.Order(StringComparer.OrdinalIgnoreCase))
        {
            ReloadPrefab(path);
        }
    }

    private void ReloadPrefab(string path)
    {
        string normalizedPath = ObjectFilePathUtility.NormalizeFullPath(path);
        if (TryReadDocument(
                normalizedPath,
                ObjectLibraryDocuments.DeserializePrefab,
                document => IsCanonicalPrefabPath(normalizedPath, document.Id)
                    && ObjectLibraryDocuments.IsValid(document),
                out ObjectLibraryPrefabDocument? document))
        {
            _validPrefabs[normalizedPath] = document;
            return;
        }

        _logger.LogWarning(
            _validPrefabs.ContainsKey(normalizedPath)
                ? "keeping the last valid object library prefab after an invalid update to {Path}"
                : "ignoring invalid object library prefab {Path}",
            normalizedPath);
    }

    private bool TryReadDocument<TDocument>(
        string path,
        Func<string, TDocument?> deserialize,
        Func<TDocument, bool> validate,
        [NotNullWhen(true)] out TDocument? document)
        where TDocument : class
    {
        try
        {
            document = deserialize(_files.ReadAllText(path));
            return document is not null && validate(document);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "failed to read object library document {Path}", path);
            document = null;
            return false;
        }
    }

    private Dictionary<string, string?> CaptureContents(IEnumerable<string> paths)
    {
        Dictionary<string, string?> contents = new(StringComparer.OrdinalIgnoreCase);
        foreach (string path in paths)
        {
            contents[path] = _files.FileExists(path) ? _files.ReadAllText(path) : null;
        }

        return contents;
    }

    private void RestoreFiles(IReadOnlyList<string> appliedPaths, IReadOnlyDictionary<string, string?> originalContents)
    {
        for (int index = appliedPaths.Count - 1; index >= 0; --index)
        {
            string path = appliedPaths[index];
            try
            {
                if (originalContents[path] is { } contents)
                {
                    _localChanges.TrackWrite(path, contents);
                    _files.WriteAllTextAtomic(path, contents);
                }
                else
                {
                    _localChanges.TrackDelete(path);
                    _files.DeleteFile(path);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "failed to restore object library document {Path}", path);
            }
        }
    }

    private void UpdateValidDocuments(ObjectLibrarySavePlan plan)
    {
        _validState = plan.State;
        foreach (ObjectLibraryDocumentChange change in plan.Changes)
        {
            if (change.Prefab is { } prefab)
            {
                _validPrefabs[change.Path] = prefab;
            }
            else if (IsPrefabJsonPath(change.Path))
            {
                _validPrefabs.Remove(change.Path);
            }
        }
    }

    private void HandleFileChanges(IReadOnlyList<ObjectFileChange> changes)
    {
        if (changes.Count == 0 || !_localChanges.HasExternalChanges(changes))
        {
            return;
        }

        Action? filesChanged;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            foreach (ObjectFileChange change in changes)
            {
                if (change.Kind == ObjectFileChangeKind.Error)
                {
                    _reloadAll = true;
                }
                else
                {
                    _ = _changedPaths.Add(ObjectFilePathUtility.NormalizeFullPath(change.Path));
                    if (!string.IsNullOrWhiteSpace(change.OldPath))
                    {
                        _ = _changedPaths.Add(ObjectFilePathUtility.NormalizeFullPath(change.OldPath));
                    }
                }
            }

            filesChanged = _filesChanged;
        }

        filesChanged?.Invoke();
    }

    private bool IsLibraryJsonPath(string path)
        => ObjectFilePathUtility.IsPathWithin(path, _paths.ObjectLibraryRootPath)
        && string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase);

    private bool IsPrefabJsonPath(string path)
        => ObjectFilePathUtility.IsPathWithin(path, _paths.ObjectLibraryPrefabsPath)
        && string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase);

    private bool IsCanonicalPrefabPath(string path, Guid prefabId)
        => ObjectFilePathUtility.PathsMatch(
            path,
            ObjectLibraryDocuments.GetPrefabPath(_paths.ObjectLibraryPrefabsPath, prefabId));
}
