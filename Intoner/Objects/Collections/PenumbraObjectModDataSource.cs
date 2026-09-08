using Dalamud.Plugin;
using Intoner.Objects.Assets;
using Intoner.Objects.Filesystem.Watching;
using Intoner.Objects.Resources;
using Intoner.Objects.Utils;
using Intoner.Services.Dependencies;
using Microsoft.Extensions.Logging;
using Penumbra.Api.IpcSubscribers;
using Penumbra.Api.Wrappers;
using System.Collections.Immutable;

namespace Intoner.Objects.Collections;

internal enum ObjectModDataChangeKind
{
    AvailabilityChanged,
    ModDirectoryChanged,
    ModRootChanged,
    ModContentChanged,
}

internal readonly record struct ObjectModDataChange(
    ObjectModDataChangeKind Kind,
    IReadOnlySet<string> AffectedModDirectories)
{
    public bool AffectsAllCollections
        => Kind is ObjectModDataChangeKind.AvailabilityChanged
               or ObjectModDataChangeKind.ModRootChanged;
}

/// <summary> exposes Penumbra mod inventory and file data for object collections </summary>
internal interface IObjectModDataSource : IDisposable
{
    /// <summary> raised when Penumbra availability or relevant installed mod inventory changes </summary>
    event Action<ObjectModDataChange>? StateChanged;

    /// <summary> gets the current installed Penumbra mod inventory snapshot </summary>
    IReadOnlyList<ObjectAvailableMod> GetInstalledMods();

    /// <summary> gets editable Penumbra option groups for one assigned mod </summary>
    /// <param name="entry">the assigned mod settings entry</param>
    /// <returns>a settings view containing only group types object collections can apply</returns>
    ObjectCollectionModSettingsView GetModSettings(ObjectCollectionModSettings entry);

    /// <summary> resolves effective object redirects for requested paths from assigned Penumbra mods without mutating Penumbra collections </summary>
    /// <param name="collection">the authored object collection to resolve</param>
    /// <param name="requestedPaths">the normalized requested game paths to resolve</param>
    /// <param name="cancellationToken">the cancellation token for the operation</param>
    /// <returns>the resolve state and resolved redirection map</returns>
    Task<ObjectModResolveResult> ResolvePathsAsync(
        ObjectCollection collection,
        IReadOnlySet<string> requestedPaths,
        CancellationToken cancellationToken);
}

internal sealed class PenumbraObjectModDataSource : IObjectModDataSource
{
    private const int InventoryRetryDelayMilliseconds = 2_000;
    private const int RequiredModManagerMajorVersion = 1;
    private static readonly TimeSpan ModDirectoryWatchDebounceDelay = TimeSpan.FromMilliseconds(650);
    private readonly record struct PenumbraModRootEntry(
        string RootPath,
        int ModIndex);

    private sealed record PenumbraModRootInventory(
        IReadOnlyDictionary<string, PenumbraModRootEntry> RootsByDirectory,
        IReadOnlyDictionary<string, string> InvalidRootPathsByDirectory);

    private sealed record ModSettingsViewCacheEntry(
        IReadOnlyDictionary<string, List<string>> Settings,
        ObjectCollectionModSettingsView View);

    private readonly IPenumbraDependency _penumbra;
    private readonly ILogger<PenumbraObjectModDataSource> _logger;
    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly ModManagerWrapper _modManager = new();
    private readonly Luna.EventSubscriber<string> _modAdded;
    private readonly Luna.EventSubscriber<string> _modDeleted;
    private readonly Luna.EventSubscriber<string, string> _modMoved;
    private readonly IObjectFileWatcherService _fileWatcherService;
    private readonly PenumbraModMetadataReader _metadataReader = new();
    private readonly Lock _modManagerLock = new();
    private readonly Lock _stateLock = new();

    private IReadOnlyList<ObjectAvailableMod> _installedMods = [];
    private bool _inventoryLoaded;
    private bool _inventoryLoading;
    private bool _isDisposed;
    private long _inventoryVersion;
    private long _settingsViewVersion;
    private long _nextInventoryLoadAttempt;
    private readonly Dictionary<string, string> _resolvedModRootsByDirectory = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ModSettingsViewCacheEntry> _settingsViewsByModDirectory = new(StringComparer.OrdinalIgnoreCase);
    private IObjectFileWatchSubscription? _modDirectoryWatchSubscription;
    private string _watchedModDirectoryRoot = string.Empty;

    public PenumbraObjectModDataSource(
        ILogger<PenumbraObjectModDataSource> logger,
        IDalamudPluginInterface pluginInterface,
        IPenumbraDependency penumbra,
        IObjectFileWatcherService fileWatcherService)
    {
        _logger = logger;
        _pluginInterface = pluginInterface;
        _penumbra = penumbra;
        _fileWatcherService = fileWatcherService;
        _modAdded = ModAdded.Subscriber(pluginInterface, modDirectory
            => InvalidateInventory(CreateModDirectoryInvalidation([modDirectory])));
        _modDeleted = ModDeleted.Subscriber(pluginInterface, modDirectory
            => InvalidateInventory(CreateModDirectoryInvalidation([modDirectory])));
        _modMoved = ModMoved.Subscriber(pluginInterface, (oldDirectory, newDirectory)
            => InvalidateInventory(CreateModDirectoryInvalidation([oldDirectory, newDirectory])));

        _penumbra.StatusChanged += HandlePenumbraStatusChanged;
        _penumbra.ModDirectoryChanged += HandlePenumbraModDirectoryChanged;

        ResetModDirectoryWatcher();
    }

    public event Action<ObjectModDataChange>? StateChanged;

    public IReadOnlyList<ObjectAvailableMod> GetInstalledMods()
    {
        EnsureInventory();
        lock (_stateLock)
        {
            return _installedMods;
        }
    }

    public ObjectCollectionModSettingsView GetModSettings(ObjectCollectionModSettings entry)
    {
        if (TryResolveUnavailablePenumbra(out ObjectCollectionResolveState resolveState, out string statusText))
        {
            return CreateModSettingsView(resolveState, statusText);
        }

        string modDirectory = ObjectCollectionKeyUtility.NormalizeModDirectory(entry.ModDirectory);
        long settingsViewVersion;
        lock (_stateLock)
        {
            settingsViewVersion = _settingsViewVersion;
            if (modDirectory.Length > 0
             && _settingsViewsByModDirectory.TryGetValue(modDirectory, out ModSettingsViewCacheEntry? cached)
             && CollectionModSettingsUtility.AreEqual(cached.Settings, entry.Settings))
            {
                return cached.View;
            }
        }

        try
        {
            if (!TryGetResolvedModRootPath(modDirectory, out string modRootPath))
            {
                PenumbraModRootInventory inventory = LoadModRootInventory([entry]);
                if (!TryResolveModRootPath(
                        inventory,
                        entry.ModDirectory,
                        entry.ModName,
                        out modRootPath,
                        out bool modMissing,
                        out string failureText))
                {
                    return modMissing
                        ? CreateModSettingsView(ObjectCollectionResolveState.ModMissing, "Penumbra mod is missing")
                        : CreateModSettingsView(ObjectCollectionResolveState.ResolveFailed, failureText);
                }
            }

            PenumbraModMetadata metadata = _metadataReader.Load(modRootPath, CancellationToken.None);
            IReadOnlyList<ObjectCollectionModSettingsGroup> groups = PenumbraModResolver.BuildSettingsView(metadata, entry);
            string summary = groups.Count switch
            {
                0 => "mod has no editable collection settings",
                1 => "1 editable group",
                _ => $"{groups.Count} editable groups",
            };
            ObjectCollectionModSettingsView view = CreateModSettingsView(
                ObjectCollectionResolveState.Ready,
                summary,
                groups);
            CacheModSettingsView(modDirectory, entry.Settings, view, settingsViewVersion);
            return view;
        }
        catch (Exception ex)
        {
            return CreateModSettingsView(
                ObjectCollectionResolveState.ResolveFailed,
                $"failed to inspect mod settings: {ex.Message}");
        }
    }

    public Task<ObjectModResolveResult> ResolvePathsAsync(
        ObjectCollection collection,
        IReadOnlySet<string> requestedPaths,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        HashSet<string> normalizedRequestedPaths = CreateSupportedResourceSet(requestedPaths);
        if (normalizedRequestedPaths.Count == 0)
        {
            return CompleteResolve(
                ObjectCollectionResolveState.Inactive,
                "collection has no requested object resource paths");
        }

        List<ObjectCollectionModSettings> enabledEntries = collection.Entries
            .Where(static entry => entry.Enabled)
            .ToList();
        if (enabledEntries.Count == 0)
        {
            return CompleteResolve(
                ObjectCollectionResolveState.Inactive,
                "collection has no enabled Penumbra mods");
        }

        if (TryResolveUnavailablePenumbra(out ObjectCollectionResolveState resolveState, out string statusText))
        {
            return CompleteResolve(
                resolveState,
                statusText,
                keepLastGoodSnapshot: resolveState is ObjectCollectionResolveState.Inactive
                                             or ObjectCollectionResolveState.WaitingForPenumbra);
        }

        try
        {
            Dictionary<string, ObjectResolvedPath> resolvedPaths = new(StringComparer.OrdinalIgnoreCase);
            List<string> resolveWarnings = [];
            PenumbraModRootInventory modRootInventory = LoadModRootInventory(enabledEntries);
            List<ObjectCollectionModSettings> orderedEntries = OrderEntriesForPriorityConflictResolution(
                enabledEntries,
                modRootInventory);
            int usableModCount = 0;
            int missingModCount = 0;
            int failedModCount = 0;
            foreach (ObjectCollectionModSettings entry in orderedEntries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!TryResolveModRootPath(modRootInventory, entry.ModDirectory, entry.ModName, out string modRootPath, out bool modMissing, out string failureText))
                {
                    if (modMissing)
                    {
                        ++missingModCount;
                        resolveWarnings.Add($"Penumbra mod '{entry.ModDirectory}' is missing");
                    }
                    else
                    {
                        ++failedModCount;
                        resolveWarnings.Add(failureText);
                    }

                    continue;
                }

                IReadOnlyList<ObjectPathRedirection> modRedirections;
                try
                {
                    modRedirections = ResolveModRedirections(
                        modRootPath,
                        entry,
                        normalizedRequestedPaths,
                        resolveWarnings,
                        cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    ++failedModCount;
                    resolveWarnings.Add($"Penumbra mod '{entry.ModDirectory}' failed to resolve: {ex.Message}");
                    continue;
                }

                ++usableModCount;
                foreach (ObjectPathRedirection redirect in modRedirections)
                {
                    resolvedPaths.TryAdd(redirect.RequestedPath, redirect.ResolvedPath);
                }
            }

            IReadOnlyList<string> warnings = ObjectCollectionDiagnosticUtility.NormalizeWarnings(resolveWarnings);
            if (usableModCount == 0)
            {
                ObjectCollectionResolveState failureState = failedModCount == 0 && missingModCount > 0
                    ? ObjectCollectionResolveState.ModMissing
                    : ObjectCollectionResolveState.ResolveFailed;
                string failureText = failedModCount == 0 && missingModCount > 0
                    ? "all assigned Penumbra mods are missing"
                    : "no assigned Penumbra mods could be resolved";
                return CompleteResolve(
                    failureState,
                    failureText,
                    warnings: warnings);
            }

            IReadOnlyDictionary<string, ObjectResolvedPath> snapshot
                = resolvedPaths.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase);
            return CompleteResolve(
                ObjectCollectionResolveState.Ready,
                snapshot.Count == 0
                    ? $"assigned Penumbra mods expose no redirects for {normalizedRequestedPaths.Count} requested object resource paths"
                    : $"resolved {snapshot.Count} redirected object resource paths from {usableModCount} Penumbra mods",
                warnings: warnings,
                resolvedPaths: snapshot,
                isComplete: missingModCount == 0 && failedModCount == 0);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "failed to resolve object collection {CollectionId} from Penumbra mod files", collection.CollectionId);
            return CompleteResolve(
                ObjectCollectionResolveState.ResolveFailed,
                $"Penumbra resolve failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        DisposeModDirectoryWatcher();
        _penumbra.ModDirectoryChanged -= HandlePenumbraModDirectoryChanged;
        _penumbra.StatusChanged -= HandlePenumbraStatusChanged;
        _modMoved.Dispose();
        _modDeleted.Dispose();
        _modAdded.Dispose();
        lock (_modManagerLock)
        {
            _isDisposed = true;
            _modManager.Dispose();
        }
    }

    private void CacheModSettingsView(
        string modDirectory,
        IReadOnlyDictionary<string, List<string>> settings,
        ObjectCollectionModSettingsView view,
        long version)
    {
        if (modDirectory.Length == 0)
        {
            return;
        }

        lock (_stateLock)
        {
            if (version == _settingsViewVersion)
            {
                _settingsViewsByModDirectory[modDirectory] = new ModSettingsViewCacheEntry(
                    CollectionModSettingsUtility.CloneSettings(settings),
                    view);
            }
        }
    }

    private bool TryGetResolvedModRootPath(string modDirectory, out string modRootPath)
    {
        lock (_stateLock)
        {
            return _resolvedModRootsByDirectory.TryGetValue(modDirectory, out modRootPath!);
        }
    }

    private void EnsureInventory()
    {
        if (!_penumbra.Status.IsAvailable)
        {
            lock (_stateLock)
            {
                _installedMods = [];
                _inventoryLoaded = true;
                _nextInventoryLoadAttempt = 0;
            }
            return;
        }

        long inventoryVersion;
        lock (_stateLock)
        {
            if (_inventoryLoaded
             || _inventoryLoading
             || Environment.TickCount64 < _nextInventoryLoadAttempt)
            {
                return;
            }

            _inventoryLoading = true;
            inventoryVersion = _inventoryVersion;
        }

        IReadOnlyList<ObjectAvailableMod> installedMods = [];
        bool loaded = false;
        try
        {
            installedMods = UseModManager(static manager => manager.EnumerateNames()
                .Select(static mod => new ObjectAvailableMod(mod.Identifier, mod.Name))
                .OrderBy(static entry => entry.ModName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static entry => entry.ModDirectory, StringComparer.OrdinalIgnoreCase)
                .ToList());
            loaded = true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "failed to load Penumbra mod inventory for object collections");
        }

        lock (_stateLock)
        {
            _inventoryLoading = false;
            if (inventoryVersion != _inventoryVersion || !_penumbra.Status.IsAvailable)
            {
                return;
            }

            if (loaded)
            {
                _installedMods = installedMods;
                _inventoryLoaded = true;
                _nextInventoryLoadAttempt = 0;
            }
            else
            {
                _nextInventoryLoadAttempt = Environment.TickCount64 + InventoryRetryDelayMilliseconds;
            }
        }
    }

    private IReadOnlyList<ObjectPathRedirection> ResolveModRedirections(
        string modRootPath,
        ObjectCollectionModSettings entry,
        IReadOnlySet<string> requestedPaths,
        ICollection<string> warnings,
        CancellationToken cancellationToken)
    {
        PenumbraModMetadata metadata = _metadataReader.Load(modRootPath, cancellationToken);
        return PenumbraModResolver.ResolveRedirections(
            metadata,
            modRootPath,
            entry,
            requestedPaths,
            warnings,
            cancellationToken);
    }

    private PenumbraModRootInventory LoadModRootInventory(IEnumerable<ObjectCollectionModSettings> entries)
    {
        HashSet<string> requestedDirectories = entries
            .Select(static entry => ObjectCollectionKeyUtility.NormalizeModDirectory(entry.ModDirectory))
            .Where(static modDirectory => modDirectory.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, PenumbraModRootEntry> rootsByDirectory = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> invalidRootPathsByDirectory = new(StringComparer.OrdinalIgnoreCase);
        if (requestedDirectories.Count == 0)
        {
            return new PenumbraModRootInventory(rootsByDirectory, invalidRootPathsByDirectory);
        }

        IReadOnlyList<(string Identifier, string RootPath, int Index)> mods = UseModManager(manager =>
        {
            List<(string Identifier, string RootPath, int Index)> matches = [];
            foreach ((string identifier, string name) in manager.EnumerateNames())
            {
                string modDirectory = ObjectCollectionKeyUtility.NormalizeModDirectory(identifier);
                if (!requestedDirectories.Contains(modDirectory))
                {
                    continue;
                }

                using ModWrapper? mod = manager.GetByName((identifier, name));
                if (mod is not null)
                {
                    matches.Add((mod.Identifier, mod.ModPath, mod.Index));
                }
            }

            return matches;
        });

        foreach ((string identifier, string rootPath, int index) in mods)
        {
            string modDirectory = ObjectCollectionKeyUtility.NormalizeModDirectory(identifier);

            if (rootPath.Length == 0 || !Directory.Exists(rootPath))
            {
                invalidRootPathsByDirectory[modDirectory] = rootPath;
                if (rootsByDirectory.Count + invalidRootPathsByDirectory.Count == requestedDirectories.Count)
                {
                    break;
                }

                continue;
            }

            string modRootPath = PenumbraModPath.NormalizeRoot(rootPath);
            rootsByDirectory[modDirectory] = new PenumbraModRootEntry(modRootPath, index);
            if (rootsByDirectory.Count + invalidRootPathsByDirectory.Count == requestedDirectories.Count)
            {
                break;
            }
        }

        lock (_stateLock)
        {
            foreach ((string modDirectory, PenumbraModRootEntry modRoot) in rootsByDirectory)
            {
                _resolvedModRootsByDirectory[modDirectory] = modRoot.RootPath;
            }
        }

        return new PenumbraModRootInventory(rootsByDirectory, invalidRootPathsByDirectory);
    }

    private static bool TryResolveModRootPath(
        PenumbraModRootInventory inventory,
        string modDirectory,
        string modName,
        out string modRootPath,
        out bool modMissing,
        out string failureText)
    {
        modRootPath = string.Empty;
        modMissing = false;
        failureText = string.Empty;

        string normalizedModDirectory = ObjectCollectionKeyUtility.NormalizeModDirectory(modDirectory);
        string normalizedModName = TextUtility.TrimOrEmpty(modName);
        string modLabel = normalizedModName.Length == 0 || string.Equals(modDirectory, modName, StringComparison.OrdinalIgnoreCase)
            ? modDirectory
            : $"{modDirectory} ({modName})";
        if (inventory.RootsByDirectory.TryGetValue(normalizedModDirectory, out PenumbraModRootEntry resolvedModRoot))
        {
            modRootPath = resolvedModRoot.RootPath;
            return true;
        }

        if (inventory.InvalidRootPathsByDirectory.TryGetValue(normalizedModDirectory, out string? invalidRootPath))
        {
            failureText = $"Penumbra mod '{modLabel}' has no valid directory at '{invalidRootPath}'";
            return false;
        }

        modMissing = true;
        return false;
    }

    private static List<ObjectCollectionModSettings> OrderEntriesForPriorityConflictResolution(
        IReadOnlyList<ObjectCollectionModSettings> entries,
        PenumbraModRootInventory inventory)
    {
        // Penumbra keeps the existing redirect on equal priority, so mod index order decides equal priority conflicts
        return entries
            .OrderByDescending(static entry => entry.Priority)
            .ThenBy(entry => ResolveModIndex(inventory, entry.ModDirectory))
            .ThenBy(static entry => entry.ModDirectory, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static entry => entry.ModName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static int ResolveModIndex(PenumbraModRootInventory inventory, string modDirectory)
    {
        string normalizedModDirectory = ObjectCollectionKeyUtility.NormalizeModDirectory(modDirectory);
        return inventory.RootsByDirectory.TryGetValue(normalizedModDirectory, out PenumbraModRootEntry rootEntry)
            ? rootEntry.ModIndex
            : int.MaxValue;
    }

    private static HashSet<string> CreateSupportedResourceSet(IEnumerable<string> paths)
    {
        HashSet<string> normalizedPaths = new(StringComparer.OrdinalIgnoreCase);
        foreach (string path in paths)
        {
            if (ObjectAssetPathRules.TryNormalizeSupportedResourcePath(path, out string normalizedPath))
            {
                normalizedPaths.Add(normalizedPath);
            }
        }

        return normalizedPaths;
    }

    private static Task<ObjectModResolveResult> CompleteResolve(
        ObjectCollectionResolveState resolveState,
        string statusText,
        bool keepLastGoodSnapshot = false,
        IReadOnlyList<string>? warnings = null,
        IReadOnlyDictionary<string, ObjectResolvedPath>? resolvedPaths = null,
        bool isComplete = true)
        => Task.FromResult(new ObjectModResolveResult
        {
            IsComplete = isComplete && !keepLastGoodSnapshot
                && resolveState is ObjectCollectionResolveState.Ready or ObjectCollectionResolveState.Inactive,
            ResolveState = resolveState,
            StatusText = statusText,
            KeepLastGoodSnapshot = keepLastGoodSnapshot,
            Warnings = warnings ?? [],
            ResolvedPaths = resolvedPaths
                ?? ImmutableDictionary<string, ObjectResolvedPath>.Empty,
        });

    private static ObjectCollectionModSettingsView CreateModSettingsView(
        ObjectCollectionResolveState resolveState,
        string statusText,
        IReadOnlyList<ObjectCollectionModSettingsGroup>? groups = null)
        => new()
        {
            ResolveState = resolveState,
            StatusText = statusText,
            Groups = groups ?? [],
        };

    private bool TryResolveUnavailablePenumbra(
        out ObjectCollectionResolveState resolveState,
        out string statusText)
    {
        DependencyStatus status = _penumbra.Status;
        statusText = status.Message;
        switch (status.State)
        {
            case DependencyState.Available:
                resolveState = ObjectCollectionResolveState.Ready;
                return false;
            case DependencyState.NotReady:
                resolveState = ObjectCollectionResolveState.WaitingForPenumbra;
                return true;
            case DependencyState.Missing:
            case DependencyState.Disabled:
            case DependencyState.FeatureDisabled:
            case DependencyState.Incompatible:
                resolveState = ObjectCollectionResolveState.Inactive;
                return true;
            default:
                resolveState = ObjectCollectionResolveState.ResolveFailed;
                return true;
        }
    }

    private void InvalidateInventory(ObjectModDataChange invalidation)
    {
        lock (_stateLock)
        {
            ++_settingsViewVersion;
            if (invalidation.Kind is ObjectModDataChangeKind.AvailabilityChanged
                or ObjectModDataChangeKind.ModDirectoryChanged
                or ObjectModDataChangeKind.ModRootChanged)
            {
                ++_inventoryVersion;
                _inventoryLoaded = false;
                _installedMods = [];
                _nextInventoryLoadAttempt = 0;
            }

            if (invalidation.AffectsAllCollections || invalidation.AffectedModDirectories.Count == 0)
            {
                _metadataReader.Clear();
                _settingsViewsByModDirectory.Clear();
                if (invalidation.Kind is not ObjectModDataChangeKind.ModContentChanged)
                {
                    _resolvedModRootsByDirectory.Clear();
                }
            }
            else
            {
                foreach (string modDirectory in invalidation.AffectedModDirectories)
                {
                    _settingsViewsByModDirectory.Remove(modDirectory);
                    if (_resolvedModRootsByDirectory.TryGetValue(modDirectory, out string? resolvedModRoot))
                    {
                        _metadataReader.Invalidate(resolvedModRoot);
                    }

                    if (invalidation.Kind is not ObjectModDataChangeKind.ModContentChanged)
                    {
                        _resolvedModRootsByDirectory.Remove(modDirectory);
                    }
                }
            }
        }

        try
        {
            StateChanged?.Invoke(invalidation);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "object Penumbra mod state handler failed");
        }
    }

    private static ObjectModDataChange CreateModDirectoryInvalidation(IEnumerable<string> modDirectories)
    {
        ImmutableHashSet<string> affectedDirectories = modDirectories
            .Select(ObjectCollectionKeyUtility.NormalizeModDirectory)
            .Where(static modDirectory => modDirectory.Length > 0)
            .ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
        return new ObjectModDataChange(
            ObjectModDataChangeKind.ModDirectoryChanged,
            affectedDirectories);
    }

    private void HandlePenumbraStatusChanged()
    {
        lock (_modManagerLock)
        {
            _modManager.Disconnect();
        }

        InvalidateInventory(new ObjectModDataChange(
            ObjectModDataChangeKind.AvailabilityChanged,
            ImmutableHashSet<string>.Empty));
    }

    private T UseModManager<T>(Func<ModManagerWrapper, T> action)
    {
        lock (_modManagerLock)
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            bool retried = false;
            while (true)
            {
                try
                {
                    if (!_penumbra.Status.IsAvailable
                     || (!_modManager.HasAdapter
                         && (!_modManager.Reconnect(_pluginInterface, RequiredModManagerMajorVersion)
                             || !_modManager.HasAdapter)))
                    {
                        throw new InvalidOperationException("Penumbra mod manager IPC is not available");
                    }

                    return action(_modManager);
                }
                catch (ObjectDisposedException) when (!retried)
                {
                    // adapter disposal can precede dependency notifications, so retry the read with a fresh connection
                    retried = true;
                    _modManager.Disconnect();
                }
                catch
                {
                    _modManager.Disconnect();
                    throw;
                }
            }
        }
    }

    private void HandlePenumbraModDirectoryChanged()
    {
        ResetModDirectoryWatcher();
        InvalidateInventory(new ObjectModDataChange(
            ObjectModDataChangeKind.ModRootChanged,
            ImmutableHashSet<string>.Empty));
    }

    private void ResetModDirectoryWatcher(bool force = false)
    {
        string nextRoot = string.Empty;
        if (_penumbra.Status.IsAvailable && !string.IsNullOrWhiteSpace(_penumbra.ModDirectory))
        {
            try
            {
                string fullPath = Path.GetFullPath(_penumbra.ModDirectory);
                if (Directory.Exists(fullPath))
                {
                    nextRoot = fullPath;
                }
            }
            catch
            {
                nextRoot = string.Empty;
            }
        }

        IObjectFileWatchSubscription? previousWatcher;
        IObjectFileWatchSubscription? nextWatcher = null;
        lock (_stateLock)
        {
            if (!force && string.Equals(_watchedModDirectoryRoot, nextRoot, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            previousWatcher = _modDirectoryWatchSubscription;
            if (nextRoot.Length > 0)
            {
                try
                {
                    nextWatcher = CreateModDirectoryWatcher(nextRoot);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "failed to watch Penumbra mod directory {ModDirectory}", nextRoot);
                    nextRoot = string.Empty;
                }
            }

            _modDirectoryWatchSubscription = nextWatcher;
            _watchedModDirectoryRoot = nextRoot;
        }

        previousWatcher?.Dispose();
    }

    private IObjectFileWatchSubscription CreateModDirectoryWatcher(string modDirectoryRoot)
        => _fileWatcherService.Watch(
            new ObjectFileWatchOptions(
                modDirectoryRoot,
                "*",
                IncludeSubdirectories: true,
                DebounceDelay: ModDirectoryWatchDebounceDelay,
                NotifyFilter: NotifyFilters.FileName
                            | NotifyFilters.DirectoryName
                            | NotifyFilters.LastWrite
                            | NotifyFilters.Size),
            OnWatchedModDirectoryChanged);

    private void DisposeModDirectoryWatcher()
    {
        IObjectFileWatchSubscription? watcher;
        lock (_stateLock)
        {
            watcher = _modDirectoryWatchSubscription;
            _modDirectoryWatchSubscription = null;
            _watchedModDirectoryRoot = string.Empty;
        }

        watcher?.Dispose();
    }

    private void OnWatchedModDirectoryChanged(IReadOnlyList<ObjectFileChange> changes)
    {
        if (changes.Any(static change => change.Kind == ObjectFileChangeKind.Error))
        {
            ResetModDirectoryWatcher(force: true);
            InvalidateInventory(new ObjectModDataChange(
                ObjectModDataChangeKind.ModRootChanged,
                ImmutableHashSet<string>.Empty));
            return;
        }

        ImmutableHashSet<string>.Builder affectedModDirectories = ImmutableHashSet.CreateBuilder<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ObjectFileChange change in changes)
        {
            AddWatchedModDirectory(affectedModDirectories, change.OldPath);
            AddWatchedModDirectory(affectedModDirectories, change.Path);
        }

        if (affectedModDirectories.Count == 0)
        {
            return;
        }

        InvalidateInventory(new ObjectModDataChange(
            ObjectModDataChangeKind.ModContentChanged,
            affectedModDirectories.ToImmutable()));
    }

    private void AddWatchedModDirectory(ImmutableHashSet<string>.Builder affectedModDirectories, string? fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
        {
            return;
        }

        string modDirectory = TryResolveWatchedModDirectory(fullPath);
        if (modDirectory.Length > 0)
        {
            affectedModDirectories.Add(modDirectory);
        }
    }

    private string TryResolveWatchedModDirectory(string fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
        {
            return string.Empty;
        }

        string normalizedFullPath;
        try
        {
            normalizedFullPath = Path.GetFullPath(fullPath);
        }
        catch
        {
            return string.Empty;
        }

        List<KeyValuePair<string, string>> resolvedModRoots;
        lock (_stateLock)
        {
            resolvedModRoots = _resolvedModRootsByDirectory.ToList();
        }

        string matchedModDirectory = string.Empty;
        int matchedRootLength = -1;
        foreach ((string modDirectory, string modRootPath) in resolvedModRoots)
        {
            if (modRootPath.Length <= matchedRootLength
             || !PenumbraModPath.Contains(modRootPath, normalizedFullPath))
            {
                continue;
            }

            matchedModDirectory = modDirectory;
            matchedRootLength = modRootPath.Length;
        }

        return matchedModDirectory;
    }

}
