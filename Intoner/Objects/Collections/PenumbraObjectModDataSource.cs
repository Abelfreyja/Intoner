using Dalamud.Plugin;
using Intoner.Ipc;
using Intoner.Objects.Interop.Ipc;
using Intoner.Objects.Assets;
using Intoner.Objects.Filesystem.Watching;
using Intoner.Objects.Resources;
using Intoner.Objects.Utils;
using Microsoft.Extensions.Logging;
using Penumbra.Api.Helpers;
using Penumbra.Api.IpcSubscribers;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;

namespace Intoner.Objects.Collections;

internal enum ObjectModDataChangeKind
{
    AvailabilityChanged,
    ModDirectoryChanged,
    ModRootChanged,
    ModContentChanged,
}

internal enum PenumbraModGroupType
{
    Unknown,
    Single,
    Multi,
    Combining,
    Imc,
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
    private const int PenumbraOptionMaskBitCount = sizeof(ulong) * 8;
    private const int PenumbraMaxMultiOptions = 32;
    private const int PenumbraMaxCombiningOptions = 8;
    private const int PenumbraOwnedFileReadRetryCount = 3;
    private const int PenumbraOwnedFileReadRetryDelayMs = 25;
    private static readonly TimeSpan ModDirectoryWatchDebounceDelay = TimeSpan.FromMilliseconds(650);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    private sealed record PenumbraModDataJsonModel(
        Dictionary<string, string?>? Files,
        Dictionary<string, string?>? FileSwaps);

    private sealed record PenumbraModOptionJsonModel(
        string? Name,
        int Priority,
        Dictionary<string, string?>? Files,
        Dictionary<string, string?>? FileSwaps);

    private sealed record PenumbraModContainerJsonModel(
        ulong AssociationMask,
        ulong AssociationValue,
        Dictionary<string, string?>? Files,
        Dictionary<string, string?>? FileSwaps);

    private sealed record PenumbraModGroupJsonModel(
        string? Name,
        string? Type,
        int Priority,
        ulong DefaultSettings,
        List<PenumbraModOptionJsonModel>? Options,
        List<PenumbraModContainerJsonModel>? Containers,
        Dictionary<string, string?>? Files,
        Dictionary<string, string?>? FileSwaps);

    private class PenumbraModDataJson
    {
        protected PenumbraModDataJson(
            IReadOnlyDictionary<string, string> normalizedFiles,
            IReadOnlyDictionary<string, string> normalizedFileSwaps)
        {
            NormalizedFiles = normalizedFiles;
            NormalizedFileSwaps = normalizedFileSwaps;
        }

        public IReadOnlyDictionary<string, string> NormalizedFiles { get; }

        public IReadOnlyDictionary<string, string> NormalizedFileSwaps { get; }

        public static PenumbraModDataJson FromJsonModel(PenumbraModDataJsonModel jsonModel)
            => new(
                NormalizeDataMap(jsonModel.Files),
                NormalizeDataMap(jsonModel.FileSwaps));

        protected static IReadOnlyDictionary<string, string> NormalizeDataMap(IReadOnlyDictionary<string, string?>? data)
        {
            if (data is null || data.Count == 0)
            {
                return ImmutableDictionary<string, string>.Empty;
            }

            Dictionary<string, string> normalizedData = new(StringComparer.OrdinalIgnoreCase);
            foreach ((string requestedPath, string? mappedPath) in data)
            {
                if (!GameAssetPathRules.TryNormalizeGamePath(requestedPath, out string normalizedRequestedPath))
                {
                    continue;
                }

                string normalizedMappedPath = ObjectStringUtility.TrimOrEmpty(mappedPath);
                if (normalizedMappedPath.Length == 0)
                {
                    continue;
                }

                normalizedData.TryAdd(normalizedRequestedPath, normalizedMappedPath);
            }

            return normalizedData.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase);
        }
    }

    private sealed class PenumbraModOptionJson : PenumbraModDataJson
    {
        private PenumbraModOptionJson(
            string name,
            int priority,
            IReadOnlyDictionary<string, string> normalizedFiles,
            IReadOnlyDictionary<string, string> normalizedFileSwaps)
            : base(normalizedFiles, normalizedFileSwaps)
        {
            Name = name;
            Priority = priority;
        }

        public string Name { get; }

        public int Priority { get; }

        public static PenumbraModOptionJson FromJsonModel(PenumbraModOptionJsonModel jsonModel)
            => new(
                ObjectStringUtility.TrimOrEmpty(jsonModel.Name),
                jsonModel.Priority,
                NormalizeDataMap(jsonModel.Files),
                NormalizeDataMap(jsonModel.FileSwaps));
    }

    private sealed class PenumbraModContainerJson : PenumbraModDataJson
    {
        private PenumbraModContainerJson(
            ulong associationMask,
            ulong associationValue,
            IReadOnlyDictionary<string, string> normalizedFiles,
            IReadOnlyDictionary<string, string> normalizedFileSwaps)
            : base(normalizedFiles, normalizedFileSwaps)
        {
            AssociationMask = associationMask;
            AssociationValue = associationValue;
        }

        public ulong AssociationMask { get; }

        public ulong AssociationValue { get; }

        public static PenumbraModContainerJson FromJsonModel(PenumbraModContainerJsonModel jsonModel)
            => new(
                jsonModel.AssociationMask,
                jsonModel.AssociationValue,
                NormalizeDataMap(jsonModel.Files),
                NormalizeDataMap(jsonModel.FileSwaps));
    }

    private sealed class PenumbraModGroupJson : PenumbraModDataJson
    {
        private PenumbraModGroupJson(
            string name,
            string type,
            int priority,
            ulong defaultSettings,
            IReadOnlyList<PenumbraModOptionJson> options,
            IReadOnlyList<PenumbraModContainerJson> containers,
            IReadOnlyDictionary<string, string> normalizedFiles,
            IReadOnlyDictionary<string, string> normalizedFileSwaps)
            : base(normalizedFiles, normalizedFileSwaps)
        {
            Name = name;
            Type = type;
            Priority = priority;
            DefaultSettings = defaultSettings;
            Options = options;
            Containers = containers;
        }

        public string Name { get; }

        public string Type { get; }

        public int Priority { get; }

        public ulong DefaultSettings { get; }

        public IReadOnlyList<PenumbraModOptionJson> Options { get; }

        public IReadOnlyList<PenumbraModContainerJson> Containers { get; }

        public static PenumbraModGroupJson FromJsonModel(PenumbraModGroupJsonModel jsonModel)
            => new(
                ObjectStringUtility.TrimOrEmpty(jsonModel.Name),
                ObjectStringUtility.TrimOrEmpty(jsonModel.Type),
                jsonModel.Priority,
                jsonModel.DefaultSettings,
                jsonModel.Options?.Select(PenumbraModOptionJson.FromJsonModel).ToArray() ?? [],
                jsonModel.Containers?.Select(PenumbraModContainerJson.FromJsonModel).ToArray() ?? [],
                NormalizeDataMap(jsonModel.Files),
                NormalizeDataMap(jsonModel.FileSwaps));
    }

    private readonly record struct SelectedPenumbraModOption(
        int Index,
        PenumbraModOptionJson Option);

    private readonly record struct SelectedOptionResolution(
        IReadOnlyList<SelectedPenumbraModOption> SelectedOptions,
        IReadOnlyList<string> MissingOptionNames);

    private readonly record struct ResolvedGroupSelection(
        bool IsValid,
        ulong SettingValue,
        IReadOnlyList<SelectedPenumbraModOption> SelectedOptions);

    private sealed record CachedModJson(
        string Signature,
        IReadOnlyList<PenumbraModGroupJson> Groups,
        PenumbraModDataJson? DefaultData);

    private readonly record struct PenumbraModRootEntry(
        string RootPath,
        int ModIndex);

    private sealed record PenumbraModRootInventory(
        IReadOnlyDictionary<string, PenumbraModRootEntry> RootsByDirectory,
        IReadOnlyDictionary<string, string> InvalidRootPathsByDirectory);

    private readonly IObjectPenumbraIpc _penumbra;
    private readonly ILogger<PenumbraObjectModDataSource> _logger;
    private readonly GetModList _getModList;
    private readonly GetModListAdapter _getModListAdapter;
    private readonly EventSubscriber<string> _modAdded;
    private readonly EventSubscriber<string> _modDeleted;
    private readonly EventSubscriber<string, string> _modMoved;
    private readonly IObjectFileWatcherService _fileWatcherService;
    private readonly Lock _stateLock = new();

    private IReadOnlyList<ObjectAvailableMod> _installedMods = [];
    private bool _inventoryLoaded;
    private readonly Dictionary<string, CachedModJson> _modJsonCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _resolvedModRootsByDirectory = new(StringComparer.OrdinalIgnoreCase);
    private IObjectFileWatchSubscription? _modDirectoryWatchSubscription;
    private string _watchedModDirectoryRoot = string.Empty;

    public PenumbraObjectModDataSource(
        ILogger<PenumbraObjectModDataSource> logger,
        IDalamudPluginInterface pluginInterface,
        IObjectPenumbraIpc penumbra,
        IObjectFileWatcherService fileWatcherService)
    {
        _logger = logger;
        _penumbra = penumbra;
        _fileWatcherService = fileWatcherService;
        _getModList = new GetModList(pluginInterface);
        _getModListAdapter = new GetModListAdapter(pluginInterface);
        _modAdded = ModAdded.Subscriber(pluginInterface, modDirectory
            => InvalidateInventory(CreateModDirectoryInvalidation([modDirectory])));
        _modDeleted = ModDeleted.Subscriber(pluginInterface, modDirectory
            => InvalidateInventory(CreateModDirectoryInvalidation([modDirectory])));
        _modMoved = ModMoved.Subscriber(pluginInterface, (oldDirectory, newDirectory)
            => InvalidateInventory(CreateModDirectoryInvalidation([oldDirectory, newDirectory])));

        _penumbra.AvailabilityChanged += HandlePenumbraAvailabilityChanged;
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
        switch (_penumbra.State)
        {
            case IpcConnectionState.Available:
                break;
            case IpcConnectionState.NotReady:
                return CreateModSettingsView(ObjectCollectionResolveState.WaitingForPenumbra, "Penumbra is not ready yet");
            case IpcConnectionState.MissingPlugin:
            case IpcConnectionState.PluginDisabled:
            case IpcConnectionState.VersionMismatch:
                return CreateModSettingsView(ObjectCollectionResolveState.Inactive, "Penumbra is not available");
            default:
                return CreateModSettingsView(
                    ObjectCollectionResolveState.ResolveFailed,
                    $"Penumbra IPC is in the {_penumbra.State} state");
        }

        try
        {
            PenumbraModRootInventory inventory = LoadModRootInventory([entry]);
            if (!TryResolveModRootPath(
                    inventory,
                    entry.ModDirectory,
                    entry.ModName,
                    out string modRootPath,
                    out bool modMissing,
                    out string failureText))
            {
                return modMissing
                    ? CreateModSettingsView(ObjectCollectionResolveState.ModMissing, "Penumbra mod is missing")
                    : CreateModSettingsView(ObjectCollectionResolveState.ResolveFailed, failureText);
            }

            CachedModJson modJson = LoadModJson(modRootPath, CancellationToken.None);
            IReadOnlyList<ObjectCollectionModSettingsGroup> groups = BuildEditableModSettingsGroups(modJson.Groups, entry);
            return CreateModSettingsView(
                ObjectCollectionResolveState.Ready,
                groups.Count == 0
                    ? "mod has no editable collection settings"
                    : $"{groups.Count} editable {(groups.Count == 1 ? "group" : "groups")}",
                groups);
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

        switch (_penumbra.State)
        {
            case IpcConnectionState.Available:
                break;
            case IpcConnectionState.NotReady:
                return CompleteResolve(
                    ObjectCollectionResolveState.WaitingForPenumbra,
                    "Penumbra is not ready yet",
                    keepLastGoodSnapshot: true);
            case IpcConnectionState.MissingPlugin:
            case IpcConnectionState.PluginDisabled:
            case IpcConnectionState.VersionMismatch:
                return CompleteResolve(
                    ObjectCollectionResolveState.Inactive,
                    "Penumbra is not available for object collection resolution",
                    keepLastGoodSnapshot: true);
            default:
                return CompleteResolve(
                    ObjectCollectionResolveState.ResolveFailed,
                    $"Penumbra IPC is in the {_penumbra.State} state");
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
                ObjectCollectionResolveState resolveState = failedModCount == 0 && missingModCount > 0
                    ? ObjectCollectionResolveState.ModMissing
                    : ObjectCollectionResolveState.ResolveFailed;
                string statusText = failedModCount == 0 && missingModCount > 0
                    ? "all assigned Penumbra mods are missing"
                    : "no assigned Penumbra mods could be resolved";
                return CompleteResolve(
                    resolveState,
                    statusText,
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
                resolvedPaths: snapshot);
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
        _penumbra.AvailabilityChanged -= HandlePenumbraAvailabilityChanged;
        _modMoved.Dispose();
        _modDeleted.Dispose();
        _modAdded.Dispose();
    }

    private void EnsureInventory()
    {
        if (_penumbra.State != IpcConnectionState.Available)
        {
            lock (_stateLock)
            {
                _installedMods = [];
                _inventoryLoaded = true;
            }
            return;
        }

        lock (_stateLock)
        {
            if (_inventoryLoaded)
            {
                return;
            }
        }

        IReadOnlyList<ObjectAvailableMod> installedMods = [];
        try
        {
            Dictionary<string, string> modList = _getModList.Invoke();
            installedMods = modList
                .Select(static kvp => new ObjectAvailableMod(kvp.Key, kvp.Value))
                .OrderBy(static entry => entry.ModName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static entry => entry.ModDirectory, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "failed to load Penumbra mod inventory for object collections");
        }

        lock (_stateLock)
        {
            _installedMods = installedMods;
            _inventoryLoaded = true;
        }
    }

    private IReadOnlyList<ObjectPathRedirection> ResolveModRedirections(
        string modRootPath,
        ObjectCollectionModSettings entry,
        IReadOnlySet<string> requestedPaths,
        ICollection<string> warnings,
        CancellationToken cancellationToken)
    {
        Dictionary<string, ObjectResolvedPath> redirects = new(StringComparer.OrdinalIgnoreCase);
        CachedModJson modJson = LoadModJson(modRootPath, cancellationToken);
        string normalizedModRootPath = NormalizeModRootPath(modRootPath);
        AddMissingGroupWarnings(modJson.Groups, entry, warnings);
        foreach ((PenumbraModGroupJson group, int _) in modJson.Groups
                     .Select(static (group, index) => (group, index))
                     .OrderByDescending(static groupData => groupData.group.Priority)
                     .ThenByDescending(static groupData => groupData.index))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ApplyGroupRedirects(group, entry, requestedPaths, normalizedModRootPath, redirects, warnings);
        }

        PenumbraModDataJson? defaultData = modJson.DefaultData;
        if (defaultData is not null)
        {
            ApplyDataJson(defaultData, requestedPaths, normalizedModRootPath, redirects);
        }

        return ObjectPathRedirectionUtility.CreateStableList(
            redirects.Select(static pair =>
                new ObjectPathRedirection(pair.Key, pair.Value)));
    }

    private static void ApplyGroupRedirects(
        PenumbraModGroupJson group,
        ObjectCollectionModSettings entry,
        IReadOnlySet<string> requestedPaths,
        string normalizedModRootPath,
        Dictionary<string, ObjectResolvedPath> redirects,
        ICollection<string> warnings)
    {
        PenumbraModGroupType groupType = ParseGroupType(group.Type);
        switch (groupType)
        {
            case PenumbraModGroupType.Multi:
            case PenumbraModGroupType.Single:
                ApplySimpleGroupRedirects(groupType, group, entry, requestedPaths, normalizedModRootPath, redirects, warnings);
                return;
            case PenumbraModGroupType.Combining:
                ApplyContainerGroupRedirects(groupType, group, entry, requestedPaths, normalizedModRootPath, redirects, warnings);
                return;
            case PenumbraModGroupType.Imc:
                warnings.Add(
                    $"Penumbra mod '{entry.ModDirectory}' group '{group.Name}' uses IMC manipulations, which object collections do not apply");
                return;
            default:
                warnings.Add(
                    $"Penumbra mod '{entry.ModDirectory}' group '{group.Name}' uses unsupported group type '{group.Type}'");
                return;
        }
    }

    private static void ApplySimpleGroupRedirects(
        PenumbraModGroupType groupType,
        PenumbraModGroupJson group,
        ObjectCollectionModSettings entry,
        IReadOnlySet<string> requestedPaths,
        string normalizedModRootPath,
        Dictionary<string, ObjectResolvedPath> redirects,
        ICollection<string> warnings)
    {
        IReadOnlyList<SelectedPenumbraModOption> selectedOptions = ResolveGroupSelection(groupType, group, entry, warnings).SelectedOptions;
        if (selectedOptions.Count == 0)
        {
            return;
        }

        if (groupType == PenumbraModGroupType.Multi)
        {
            foreach (SelectedPenumbraModOption selectedOption in selectedOptions
                         .OrderByDescending(static option => option.Option.Priority)
                         .ThenBy(static option => option.Index))
            {
                ApplyDataJson(selectedOption.Option, requestedPaths, normalizedModRootPath, redirects);
            }

            return;
        }

        ApplyDataJson(selectedOptions[0].Option, requestedPaths, normalizedModRootPath, redirects);
    }

    private static void ApplyContainerGroupRedirects(
        PenumbraModGroupType groupType,
        PenumbraModGroupJson group,
        ObjectCollectionModSettings entry,
        IReadOnlySet<string> requestedPaths,
        string normalizedModRootPath,
        Dictionary<string, ObjectResolvedPath> redirects,
        ICollection<string> warnings)
    {
        ResolvedGroupSelection selection = ResolveGroupSelection(groupType, group, entry, warnings);
        if (!selection.IsValid)
        {
            return;
        }

        if (groupType == PenumbraModGroupType.Combining)
        {
            int selectedContainerIndex = (int)selection.SettingValue;
            if (selectedContainerIndex >= GetPenumbraLoadedCombiningContainerCount(group))
            {
                return;
            }

            ApplyDataJson(group.Containers[selectedContainerIndex], requestedPaths, normalizedModRootPath, redirects);
            return;
        }

        ulong associationMaskLimit = BuildOptionMask(GetPenumbraLoadedOptionCount(groupType, group));
        foreach (PenumbraModContainerJson container in group.Containers)
        {
            ulong associationMask = container.AssociationMask & associationMaskLimit;
            ulong associationValue = container.AssociationValue & associationMask;
            if ((selection.SettingValue & associationMask) != associationValue)
            {
                continue;
            }

            ApplyDataJson(container, requestedPaths, normalizedModRootPath, redirects);
        }
    }

    private static ResolvedGroupSelection ResolveGroupSelection(
        PenumbraModGroupType groupType,
        PenumbraModGroupJson group,
        ObjectCollectionModSettings entry,
        ICollection<string> warnings)
    {
        if (TryGetSavedGroupOptionNames(entry, group.Name, out List<string>? optionNames))
        {
            return ResolveExplicitGroupSelection(groupType, group, entry, optionNames, warnings);
        }

        return ResolveDefaultGroupSelection(groupType, group);
    }

    private static ResolvedGroupSelection ResolveExplicitGroupSelection(
        PenumbraModGroupType groupType,
        PenumbraModGroupJson group,
        ObjectCollectionModSettings entry,
        IReadOnlyList<string> optionNames,
        ICollection<string> warnings)
    {
        SelectedOptionResolution explicitSelection = ResolveExplicitSelectedOptions(groupType, group, optionNames);
        if (explicitSelection.MissingOptionNames.Count > 0)
        {
            warnings.Add(BuildMissingOptionWarning(entry, group.Name, explicitSelection.MissingOptionNames));
        }

        if (explicitSelection.SelectedOptions.Count > 0)
        {
            return CreateResolvedGroupSelection(groupType, explicitSelection.SelectedOptions);
        }

        if (AllowsEmptyExplicitSelection(groupType))
        {
            return new ResolvedGroupSelection(true, 0, []);
        }

        warnings.Add(
            $"Penumbra mod '{entry.ModDirectory}' group '{group.Name}' has no valid saved option selections; default group selection was ignored");
        return new ResolvedGroupSelection(false, 0, []);
    }

    private static SelectedOptionResolution ResolveExplicitSelectedOptions(
        PenumbraModGroupType groupType,
        PenumbraModGroupJson group,
        IReadOnlyList<string> optionNames)
    {
        if (optionNames.Count == 0)
        {
            return new SelectedOptionResolution([], []);
        }

        int penumbraLoadedOptionCount = GetPenumbraLoadedOptionCount(groupType, group);
        HashSet<string> unmatchedOptionNames = new(optionNames, StringComparer.Ordinal);
        if (UsesBitmaskSelection(groupType))
        {
            Dictionary<string, SelectedPenumbraModOption> selectedOptions = new(StringComparer.Ordinal);
            for (var optionIndex = 0; optionIndex < penumbraLoadedOptionCount; ++optionIndex)
            {
                PenumbraModOptionJson option = group.Options[optionIndex];
                if (unmatchedOptionNames.Remove(option.Name))
                {
                    selectedOptions[option.Name] = new SelectedPenumbraModOption(optionIndex, option);
                }
            }

            return new SelectedOptionResolution(selectedOptions.Values.ToArray(), unmatchedOptionNames.ToArray());
        }

        for (int optionIndex = penumbraLoadedOptionCount - 1; optionIndex >= 0; --optionIndex)
        {
            PenumbraModOptionJson option = group.Options[optionIndex];
            if (unmatchedOptionNames.Remove(option.Name))
            {
                return new SelectedOptionResolution(
                    [new SelectedPenumbraModOption(optionIndex, option)],
                    unmatchedOptionNames.ToArray());
            }
        }

        return new SelectedOptionResolution([], unmatchedOptionNames.ToArray());
    }

    private static ResolvedGroupSelection ResolveDefaultGroupSelection(
        PenumbraModGroupType groupType,
        PenumbraModGroupJson group)
    {
        int penumbraLoadedOptionCount = GetPenumbraLoadedOptionCount(groupType, group);
        if (groupType == PenumbraModGroupType.Combining)
        {
            ulong settingValue = Math.Min(group.DefaultSettings, BuildOptionMask(penumbraLoadedOptionCount));
            List<SelectedPenumbraModOption> selectedOptions = CollectSelectedOptionsFromBitmask(group, penumbraLoadedOptionCount, settingValue);
            return new ResolvedGroupSelection(true, settingValue, selectedOptions);
        }

        if (UsesBitmaskSelection(groupType))
        {
            ulong settingValue = group.DefaultSettings & BuildOptionMask(penumbraLoadedOptionCount);
            List<SelectedPenumbraModOption> selectedOptions = CollectSelectedOptionsFromBitmask(group, penumbraLoadedOptionCount, settingValue);
            return new ResolvedGroupSelection(true, settingValue, selectedOptions);
        }

        if (penumbraLoadedOptionCount == 0)
        {
            return new ResolvedGroupSelection(true, 0, []);
        }

        int defaultIndex = (int)Math.Min(group.DefaultSettings, (ulong)(penumbraLoadedOptionCount - 1));
        SelectedPenumbraModOption selectedOption = new(defaultIndex, group.Options[defaultIndex]);
        return new ResolvedGroupSelection(true, (ulong)defaultIndex, [selectedOption]);
    }

    private CachedModJson LoadModJson(string modRootPath, CancellationToken cancellationToken)
    {
        string normalizedModRootPath = NormalizeModRootPath(modRootPath);
        Exception? lastTransientException = null;
        for (var attempt = 1; attempt <= PenumbraOwnedFileReadRetryCount; ++attempt)
        {
            try
            {
                string signature = BuildModJsonSignature(normalizedModRootPath, out string[] groupJsonPaths, out string? defaultJsonPath);

                lock (_stateLock)
                {
                    if (_modJsonCache.TryGetValue(normalizedModRootPath, out CachedModJson? cachedJson)
                     && string.Equals(cachedJson.Signature, signature, StringComparison.Ordinal))
                    {
                        return cachedJson;
                    }
                }

                cancellationToken.ThrowIfCancellationRequested();

                List<PenumbraModGroupJson> groups = new(groupJsonPaths.Length);
                foreach (string groupJsonPath in groupJsonPaths)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    groups.Add(LoadRequiredJsonModel<PenumbraModGroupJsonModel, PenumbraModGroupJson>(
                        groupJsonPath,
                        PenumbraModGroupJson.FromJsonModel,
                        cancellationToken));
                }

                PenumbraModDataJson? defaultData = defaultJsonPath is null
                    ? null
                    : LoadRequiredJsonModel<PenumbraModDataJsonModel, PenumbraModDataJson>(
                        defaultJsonPath,
                        PenumbraModDataJson.FromJsonModel,
                        cancellationToken);
                string parsedSignature = BuildModJsonSignature(normalizedModRootPath, out _, out _);
                if (!string.Equals(signature, parsedSignature, StringComparison.Ordinal))
                {
                    lastTransientException = new IOException($"Penumbra mod files changed while reading '{normalizedModRootPath}'");
                    DelayBeforePenumbraOwnedFileRetry(attempt, cancellationToken);
                    continue;
                }

                CachedModJson parsedJson = new(
                    parsedSignature,
                    groups,
                    defaultData);

                lock (_stateLock)
                {
                    _modJsonCache[normalizedModRootPath] = parsedJson;
                }

                return parsedJson;
            }
            catch (IOException ex) when (attempt < PenumbraOwnedFileReadRetryCount)
            {
                lastTransientException = ex;
                DelayBeforePenumbraOwnedFileRetry(attempt, cancellationToken);
            }
        }

        throw new IOException($"failed to read Penumbra mod files from '{normalizedModRootPath}'", lastTransientException);
    }

    private static T LoadRequiredJsonModel<TJsonModel, T>(
        string path,
        Func<TJsonModel, T> modelFactory,
        CancellationToken cancellationToken)
        where TJsonModel : class
        where T : class
    {
        Exception? lastTransientException = null;
        for (var attempt = 1; attempt <= PenumbraOwnedFileReadRetryCount; ++attempt)
        {
            try
            {
                using FileStream stream = ObjectAssetFileUtility.OpenSharedRead(path);
                TJsonModel jsonModel = JsonSerializer.Deserialize<TJsonModel>(stream, JsonOptions)
                    ?? throw new InvalidDataException($"'{Path.GetFileName(path)}' did not contain a valid JSON document");
                return modelFactory(jsonModel);
            }
            catch (IOException ex) when (attempt < PenumbraOwnedFileReadRetryCount)
            {
                lastTransientException = ex;
                DelayBeforePenumbraOwnedFileRetry(attempt, cancellationToken);
            }
            catch (JsonException ex) when (attempt < PenumbraOwnedFileReadRetryCount)
            {
                lastTransientException = ex;
                DelayBeforePenumbraOwnedFileRetry(attempt, cancellationToken);
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException($"failed to parse '{Path.GetFileName(path)}': {ex.Message}", ex);
            }
            catch (IOException ex)
            {
                throw new IOException($"failed to read '{Path.GetFileName(path)}'", ex);
            }
        }

        throw new IOException($"failed to read '{Path.GetFileName(path)}'", lastTransientException);
    }

    private static void DelayBeforePenumbraOwnedFileRetry(int attempt, CancellationToken cancellationToken)
    {
        if (attempt >= PenumbraOwnedFileReadRetryCount)
        {
            return;
        }

        if (cancellationToken.WaitHandle.WaitOne(PenumbraOwnedFileReadRetryDelayMs))
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private static string BuildModJsonSignature(
        string modRootPath,
        out string[] groupJsonPaths,
        out string? defaultJsonPath)
    {
        defaultJsonPath = Path.Combine(modRootPath, "default_mod.json");
        if (!File.Exists(defaultJsonPath))
        {
            defaultJsonPath = null;
        }

        groupJsonPaths = Directory.EnumerateFiles(modRootPath, "group_*.json", SearchOption.TopDirectoryOnly)
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        StringBuilder signatureBuilder = new();
        AppendJsonSignature(signatureBuilder, defaultJsonPath);
        foreach (string groupJsonPath in groupJsonPaths)
        {
            AppendJsonSignature(signatureBuilder, groupJsonPath);
        }

        return signatureBuilder.ToString();
    }

    private static void AppendJsonSignature(StringBuilder signatureBuilder, string? path)
    {
        if (path is null)
        {
            signatureBuilder.Append("<missing>").Append('\n');
            return;
        }

        FileInfo fileInfo = new(path);
        signatureBuilder
            .Append(path)
            .Append('|')
            .Append(fileInfo.Length)
            .Append('|')
            .Append(fileInfo.LastWriteTimeUtc.Ticks)
            .Append('\n');
    }

    private static void AddMissingGroupWarnings(
        IReadOnlyList<PenumbraModGroupJson> groups,
        ObjectCollectionModSettings entry,
        ICollection<string> warnings)
    {
        if (entry.Settings.Count == 0)
        {
            return;
        }

        HashSet<string> groupNames = groups
            .Select(static group => group.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string groupName in entry.Settings.Keys)
        {
            if (!groupNames.Contains(groupName))
            {
                warnings.Add($"Penumbra mod '{entry.ModDirectory}' group '{groupName}' no longer exists");
            }
        }
    }

    private static string BuildMissingOptionWarning(
        ObjectCollectionModSettings entry,
        string groupName,
        IReadOnlyList<string> missingOptionNames)
    {
        string missingOptions = string.Join(", ", missingOptionNames.OrderBy(static optionName => optionName, StringComparer.Ordinal));
        return $"Penumbra mod '{entry.ModDirectory}' group '{groupName}' has saved options that no longer exist: {missingOptions}";
    }

    private static IReadOnlyList<ObjectCollectionModSettingsGroup> BuildEditableModSettingsGroups(
        IReadOnlyList<PenumbraModGroupJson> groups,
        ObjectCollectionModSettings entry)
    {
        List<ObjectCollectionModSettingsGroup> editableGroups = [];
        foreach (PenumbraModGroupJson group in groups)
        {
            if (group.Name.Length == 0)
            {
                continue;
            }

            PenumbraModGroupType groupType = ParseGroupType(group.Type);
            if (!TryMapEditableGroupKind(groupType, out ObjectCollectionModSettingsGroupKind editableKind))
            {
                continue;
            }

            int optionCount = GetPenumbraLoadedOptionCount(groupType, group);
            if (optionCount == 0)
            {
                continue;
            }

            ResolvedGroupSelection defaultSelection = ResolveDefaultGroupSelection(groupType, group);
            bool hasOverride = TryGetSavedGroupOptionNames(entry, group.Name, out List<string>? savedOptionNames);
            ResolvedGroupSelection currentSelection = defaultSelection;
            if (hasOverride && savedOptionNames is not null)
            {
                List<string> ignoredWarnings = [];
                currentSelection = ResolveExplicitGroupSelection(groupType, group, entry, savedOptionNames, ignoredWarnings);
            }

            HashSet<int> defaultIndexes = defaultSelection.SelectedOptions
                .Select(static option => option.Index)
                .ToHashSet();
            HashSet<int> selectedIndexes = currentSelection.SelectedOptions
                .Select(static option => option.Index)
                .ToHashSet();

            List<ObjectCollectionModSettingsOption> options = [];
            for (var optionIndex = 0; optionIndex < optionCount; ++optionIndex)
            {
                PenumbraModOptionJson option = group.Options[optionIndex];
                if (option.Name.Length == 0)
                {
                    continue;
                }

                options.Add(new ObjectCollectionModSettingsOption(
                    option.Name,
                    option.Priority,
                    defaultIndexes.Contains(optionIndex),
                    selectedIndexes.Contains(optionIndex)));
            }

            if (options.Count == 0)
            {
                continue;
            }

            editableGroups.Add(new ObjectCollectionModSettingsGroup
            {
                Name = group.Name,
                Kind = editableKind,
                HasOverride = hasOverride,
                Options = options,
            });
        }

        return editableGroups;
    }

    private static bool TryGetSavedGroupOptionNames(
        ObjectCollectionModSettings entry,
        string groupName,
        [NotNullWhen(true)] out List<string>? optionNames)
    {
        string normalizedGroupName = CollectionModSettingsUtility.NormalizeGroupName(groupName);
        if (normalizedGroupName.Length == 0)
        {
            optionNames = null;
            return false;
        }

        if (entry.Settings.TryGetValue(normalizedGroupName, out List<string>? exactOptionNames) && exactOptionNames is not null)
        {
            optionNames = exactOptionNames;
            return true;
        }

        foreach ((string savedGroupName, List<string> savedOptionNames) in entry.Settings)
        {
            if (string.Equals(savedGroupName, normalizedGroupName, StringComparison.OrdinalIgnoreCase))
            {
                optionNames = savedOptionNames;
                return true;
            }
        }

        optionNames = null;
        return false;
    }

    private static void ApplyDataJson(
        PenumbraModDataJson data,
        IReadOnlySet<string> normalizedRequestedPaths,
        string normalizedModRootPath,
        Dictionary<string, ObjectResolvedPath> redirects)
    {
        foreach (string requestedPath in normalizedRequestedPaths)
        {
            if (TryCreateLocalFileRedirect(data, requestedPath, normalizedModRootPath, out ObjectResolvedPath localFilePath))
            {
                redirects.TryAdd(requestedPath, localFilePath);
            }

            if (TryCreateFileSwapRedirect(data, requestedPath, out ObjectResolvedPath redirectedPath))
            {
                redirects.TryAdd(requestedPath, redirectedPath);
            }
        }
    }

    private static bool TryCreateLocalFileRedirect(
        PenumbraModDataJson data,
        string requestedPath,
        string normalizedModRootPath,
        out ObjectResolvedPath resolvedPath)
    {
        resolvedPath = default;
        if (!data.NormalizedFiles.TryGetValue(requestedPath, out string? relativePath))
        {
            return false;
        }

        string normalizedRelativePath = relativePath.Replace('/', Path.DirectorySeparatorChar);
        if (normalizedRelativePath.Length == 0)
        {
            return false;
        }

        string localFilePath;
        try
        {
            localFilePath = Path.GetFullPath(Path.Combine(normalizedModRootPath, normalizedRelativePath));
        }
        catch
        {
            return false;
        }

        if (!IsPathInsideRoot(localFilePath, normalizedModRootPath) || !File.Exists(localFilePath))
        {
            return false;
        }

        resolvedPath = ObjectResolvedPath.FromLocalFile(localFilePath);
        return ObjectResourcePathUtility.IsSupportedRedirection(requestedPath, resolvedPath);
    }

    private static bool TryCreateFileSwapRedirect(
        PenumbraModDataJson data,
        string requestedPath,
        out ObjectResolvedPath resolvedPath)
    {
        resolvedPath = default;
        if (!data.NormalizedFileSwaps.TryGetValue(requestedPath, out string? redirectedPath)
         || !ObjectResolvedPath.TryCreate(redirectedPath, out resolvedPath))
        {
            return false;
        }

        return ObjectResourcePathUtility.IsSupportedRedirection(requestedPath, resolvedPath);
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

        using ModListWrapper mods = _getModListAdapter.Invoke();
        foreach (ModWrapper modWrapper in mods)
        {
            using ModWrapper mod = modWrapper;
            string modDirectory = ObjectCollectionKeyUtility.NormalizeModDirectory(mod.Identifier);
            if (!requestedDirectories.Contains(modDirectory))
            {
                continue;
            }

            string resolvedPath = mod.ModPath.FullName;
            if (resolvedPath.Length == 0 || !Directory.Exists(resolvedPath))
            {
                invalidRootPathsByDirectory[modDirectory] = resolvedPath;
                if (rootsByDirectory.Count + invalidRootPathsByDirectory.Count == requestedDirectories.Count)
                {
                    break;
                }

                continue;
            }

            string modRootPath = NormalizeModRootPath(resolvedPath);
            rootsByDirectory[modDirectory] = new PenumbraModRootEntry(modRootPath, mod.Index);
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
        string normalizedModName = ObjectStringUtility.TrimOrEmpty(modName);
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

    private static PenumbraModGroupType ParseGroupType(string type)
    {
        string normalizedType = ObjectStringUtility.TrimOrEmpty(type);
        if (normalizedType.Length == 0)
        {
            return PenumbraModGroupType.Unknown;
        }

        return normalizedType.ToLowerInvariant() switch
        {
            "single" => PenumbraModGroupType.Single,
            "multi" => PenumbraModGroupType.Multi,
            "combining" => PenumbraModGroupType.Combining,
            "imc" => PenumbraModGroupType.Imc,
            _ => PenumbraModGroupType.Unknown,
        };
    }

    private static bool UsesBitmaskSelection(PenumbraModGroupType groupType)
        => groupType is PenumbraModGroupType.Multi
            or PenumbraModGroupType.Combining
            or PenumbraModGroupType.Imc;

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

    private static bool AllowsEmptyExplicitSelection(PenumbraModGroupType groupType)
        => groupType is PenumbraModGroupType.Multi
            or PenumbraModGroupType.Combining;

    private static bool TryMapEditableGroupKind(
        PenumbraModGroupType groupType,
        out ObjectCollectionModSettingsGroupKind editableKind)
    {
        editableKind = groupType switch
        {
            PenumbraModGroupType.Single => ObjectCollectionModSettingsGroupKind.Single,
            PenumbraModGroupType.Multi => ObjectCollectionModSettingsGroupKind.Multi,
            PenumbraModGroupType.Combining => ObjectCollectionModSettingsGroupKind.Combining,
            _ => default,
        };

        return groupType is PenumbraModGroupType.Single
            or PenumbraModGroupType.Multi
            or PenumbraModGroupType.Combining;
    }

    private static ResolvedGroupSelection CreateResolvedGroupSelection(
        PenumbraModGroupType groupType,
        IReadOnlyList<SelectedPenumbraModOption> selectedOptions)
    {
        ulong settingValue = UsesBitmaskSelection(groupType)
            ? BuildSelectedOptionMask(selectedOptions)
            : (ulong)selectedOptions[0].Index;
        return new ResolvedGroupSelection(true, settingValue, selectedOptions);
    }

    private static ulong BuildSelectedOptionMask(IEnumerable<SelectedPenumbraModOption> selectedOptions)
    {
        ulong mask = 0;
        foreach (SelectedPenumbraModOption selectedOption in selectedOptions)
        {
            if ((uint)selectedOption.Index < PenumbraOptionMaskBitCount)
            {
                mask |= 1UL << selectedOption.Index;
            }
        }

        return mask;
    }

    private static ulong BuildOptionMask(int optionCount)
        => optionCount >= PenumbraOptionMaskBitCount
            ? ulong.MaxValue
            : (1UL << optionCount) - 1;

    private static List<SelectedPenumbraModOption> CollectSelectedOptionsFromBitmask(
        PenumbraModGroupJson group,
        int penumbraLoadedOptionCount,
        ulong settingValue)
    {
        List<SelectedPenumbraModOption> selectedOptions = [];
        int representedOptionCount = Math.Min(penumbraLoadedOptionCount, PenumbraOptionMaskBitCount);
        for (var optionIndex = 0; optionIndex < representedOptionCount; ++optionIndex)
        {
            if ((settingValue & (1UL << optionIndex)) != 0)
            {
                selectedOptions.Add(new SelectedPenumbraModOption(optionIndex, group.Options[optionIndex]));
            }
        }

        return selectedOptions;
    }

    private static int GetPenumbraLoadedOptionCount(PenumbraModGroupType groupType, PenumbraModGroupJson group)
        => groupType switch
        {
            PenumbraModGroupType.Multi => Math.Min(group.Options.Count, PenumbraMaxMultiOptions),
            PenumbraModGroupType.Combining => Math.Min(group.Options.Count, PenumbraMaxCombiningOptions),
            PenumbraModGroupType.Imc => Math.Min(group.Options.Count, PenumbraMaxMultiOptions),
            _ => group.Options.Count,
        };

    private static int GetPenumbraLoadedCombiningContainerCount(PenumbraModGroupJson group)
    {
        int penumbraLoadedOptionCount = GetPenumbraLoadedOptionCount(PenumbraModGroupType.Combining, group);
        int expectedContainerCount = 1 << penumbraLoadedOptionCount;
        return Math.Min(group.Containers.Count, expectedContainerCount);
    }

    private static string NormalizeModRootPath(string modRootPath)
    {
        string normalizedModRootPath = Path.GetFullPath(modRootPath);
        if (!normalizedModRootPath.EndsWith(Path.DirectorySeparatorChar))
        {
            normalizedModRootPath += Path.DirectorySeparatorChar;
        }

        return normalizedModRootPath;
    }

    private static Task<ObjectModResolveResult> CompleteResolve(
        ObjectCollectionResolveState resolveState,
        string statusText,
        bool keepLastGoodSnapshot = false,
        IReadOnlyList<string>? warnings = null,
        IReadOnlyDictionary<string, ObjectResolvedPath>? resolvedPaths = null)
        => Task.FromResult(new ObjectModResolveResult
        {
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

    private void InvalidateInventory(ObjectModDataChange invalidation)
    {
        lock (_stateLock)
        {
            if (invalidation.Kind is ObjectModDataChangeKind.AvailabilityChanged
                or ObjectModDataChangeKind.ModDirectoryChanged
                or ObjectModDataChangeKind.ModRootChanged)
            {
                _inventoryLoaded = false;
                _installedMods = [];
            }

            if (invalidation.AffectsAllCollections || invalidation.AffectedModDirectories.Count == 0)
            {
                _modJsonCache.Clear();
                if (invalidation.Kind is not ObjectModDataChangeKind.ModContentChanged)
                {
                    _resolvedModRootsByDirectory.Clear();
                }
            }
            else
            {
                foreach (string modDirectory in invalidation.AffectedModDirectories)
                {
                    if (_resolvedModRootsByDirectory.TryGetValue(modDirectory, out string? resolvedModRoot))
                    {
                        _modJsonCache.Remove(resolvedModRoot);
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

    private void HandlePenumbraAvailabilityChanged()
        => InvalidateInventory(new ObjectModDataChange(
            ObjectModDataChangeKind.AvailabilityChanged,
            ImmutableHashSet<string>.Empty));

    private void HandlePenumbraModDirectoryChanged()
    {
        ResetModDirectoryWatcher();
        InvalidateInventory(new ObjectModDataChange(
            ObjectModDataChangeKind.ModRootChanged,
            ImmutableHashSet<string>.Empty));
    }

    private void ResetModDirectoryWatcher()
    {
        string nextRoot = string.Empty;
        if (_penumbra.State == IpcConnectionState.Available && !string.IsNullOrWhiteSpace(_penumbra.ModDirectory))
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
            if (string.Equals(_watchedModDirectoryRoot, nextRoot, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            previousWatcher = _modDirectoryWatchSubscription;
            if (nextRoot.Length > 0)
            {
                nextWatcher = CreateModDirectoryWatcher(nextRoot);
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
            ResetModDirectoryWatcher();
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
             || !IsPathInsideRoot(normalizedFullPath, modRootPath))
            {
                continue;
            }

            matchedModDirectory = modDirectory;
            matchedRootLength = modRootPath.Length;
        }

        return matchedModDirectory;
    }

    private static bool IsPathInsideRoot(string fullPath, string normalizedRootPath)
    {
        string trimmedFullPath = Path.TrimEndingDirectorySeparator(fullPath);
        string trimmedRootPath = Path.TrimEndingDirectorySeparator(normalizedRootPath);
        return string.Equals(trimmedFullPath, trimmedRootPath, StringComparison.OrdinalIgnoreCase)
            || fullPath.StartsWith(normalizedRootPath, StringComparison.OrdinalIgnoreCase);
    }

}
