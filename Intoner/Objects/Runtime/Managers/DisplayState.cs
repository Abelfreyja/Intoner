using Dalamud.Plugin.Services;
using Intoner.Objects.Runtime;
using Intoner.Objects.Utils;
using Intoner.Scene;
using Intoner.Services.Gpu;
using Microsoft.Extensions.Logging;
using System.Numerics;

namespace Intoner.Displays;

/// <summary> owns persisted display state, revisions, and storage </summary>
internal sealed class DisplayState : IDisposable
{
    private const long SaveDelayMilliseconds = 500;

    private readonly ILogger<DisplayState> _logger;
    private readonly IFramework _framework;
    private readonly ISceneLocationService _locationService;
    private readonly ISceneIdentityRegistry _identityRegistry;
    private readonly DisplayPersistence _persistence;
    private readonly Lock _stateLock;
    private readonly Lock _saveLock = new();
    private readonly Dictionary<Guid, DisplaySnapshot> _snapshots = [];

    private long _revision;
    private long _saveAfterMilliseconds;
    private bool _savePending;
    private bool _disposed;

    public DisplayState(
        ILogger<DisplayState> logger,
        IFramework framework,
        ObjectStateLock stateLock,
        ISceneLocationService locationService,
        ISceneIdentityRegistry identityRegistry,
        DisplayPersistence persistence)
    {
        _logger = logger;
        _framework = framework;
        _stateLock = stateLock.Value;
        _locationService = locationService;
        _identityRegistry = identityRegistry;
        _persistence = persistence;

        LoadOwnedSnapshots();
        _framework.Update += HandleFrameworkUpdate;
    }

    public long Revision
        => Interlocked.Read(ref _revision);

    public IReadOnlyList<DisplaySnapshot> GetSnapshots()
        => GetSnapshots(out _);

    public IReadOnlyList<DisplaySnapshot> GetSnapshots(out long revision)
    {
        lock (_stateLock)
        {
            revision = Revision;
            return _snapshots.Values
                .OrderBy(static snapshot => snapshot.CreatedAtUtc)
                .ToList();
        }
    }

    public bool TryCreate(
        DisplaySettings settings,
        SceneTransform transform,
        bool visible,
        out DisplaySnapshot snapshot)
    {
        SceneCreationContext createdIn = _locationService.GetCurrentCreationContext();
        lock (_stateLock)
        {
            snapshot = Sanitize(new DisplaySnapshot
            {
                Id = Guid.NewGuid(),
                Name = NextNameLocked(),
                Transform = transform,
                Settings = settings,
                Visible = visible,
                CreatedAtUtc = DateTime.UtcNow,
                CreatedIn = createdIn,
            });
            return TryRestoreLocked(snapshot);
        }
    }

    public bool TryRestore(DisplaySnapshot snapshot)
    {
        snapshot = Sanitize(snapshot);
        lock (_stateLock)
        {
            return TryRestoreLocked(snapshot);
        }
    }

    public bool TryUpdate(DisplaySnapshot snapshot, out DisplaySnapshot appliedSnapshot)
    {
        appliedSnapshot = Sanitize(snapshot);
        lock (_stateLock)
        {
            if (!_snapshots.TryGetValue(appliedSnapshot.Id, out DisplaySnapshot? current))
            {
                appliedSnapshot = null!;
                return false;
            }

            if (Equals(current, appliedSnapshot))
            {
                appliedSnapshot = current;
                return true;
            }

            _snapshots[appliedSnapshot.Id] = appliedSnapshot;
            MarkChangedLocked();
            return true;
        }
    }

    public bool TryRemove(Guid id, out DisplaySnapshot removedSnapshot)
    {
        lock (_stateLock)
        {
            if (!_snapshots.TryGetValue(id, out removedSnapshot!))
            {
                removedSnapshot = null!;
                return false;
            }

            Guid[] nextIds = _snapshots.Keys.Where(candidate => candidate != id).ToArray();
            if (!TryReserveIdentities(nextIds))
            {
                removedSnapshot = null!;
                return false;
            }

            _ = _snapshots.Remove(id);
            MarkChangedLocked();
            return true;
        }
    }

    public bool TryApplyChanges(IReadOnlyList<SceneItemSnapshotChange> changes)
    {
        if (changes.Count == 0)
        {
            return true;
        }

        lock (_stateLock)
        {
            var nextSnapshots = new Dictionary<Guid, DisplaySnapshot>(_snapshots);
            bool identitiesChanged = false;
            bool stateChanged = false;
            foreach (SceneItemSnapshotChange change in changes)
            {
                if (!change.HasChange)
                {
                    continue;
                }

                if (!change.TryGetSnapshots(out DisplaySnapshot? before, out DisplaySnapshot? after))
                {
                    return false;
                }

                after = after is null ? null : Sanitize(after);
                if (Equals(before, after))
                {
                    continue;
                }

                DisplaySnapshot candidate = after ?? before!;
                if (before is null)
                {
                    if (!nextSnapshots.TryAdd(candidate.Id, after!))
                    {
                        return false;
                    }

                    identitiesChanged = true;
                }
                else if (!nextSnapshots.TryGetValue(candidate.Id, out DisplaySnapshot? current)
                         || !Equals(current, before))
                {
                    return false;
                }
                else if (after is null)
                {
                    _ = nextSnapshots.Remove(candidate.Id);
                    identitiesChanged = true;
                }
                else
                {
                    nextSnapshots[candidate.Id] = after;
                }

                stateChanged = true;
            }

            if (identitiesChanged
                && !TryReserveIdentities(nextSnapshots.Keys))
            {
                return false;
            }

            if (!stateChanged)
            {
                return true;
            }

            _snapshots.Clear();
            foreach ((Guid id, DisplaySnapshot snapshot) in nextSnapshots)
            {
                _snapshots.Add(id, snapshot);
            }

            MarkChangedLocked();
            return true;
        }
    }

    public bool TryCreateDuplicates(
        IReadOnlyList<DisplaySnapshot> snapshots,
        out IReadOnlyList<DisplaySnapshot> duplicates)
    {
        duplicates = [];
        lock (_stateLock)
        {
            var sources = new List<DisplaySnapshot>(snapshots.Count);
            var sourceIds = new HashSet<Guid>();
            foreach (Guid id in snapshots.Select(static snapshot => snapshot.Id))
            {
                if (!sourceIds.Add(id)
                    || !_snapshots.TryGetValue(id, out DisplaySnapshot? source))
                {
                    return false;
                }

                sources.Add(source);
            }

            duplicates = sources.Select(CreateDuplicate).ToArray();
            return true;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _framework.Update -= HandleFrameworkUpdate;
        SaveNow();
        _identityRegistry.RemoveOwnedItems(this);
    }

    private void HandleFrameworkUpdate(IFramework framework)
    {
        bool shouldSave;
        lock (_stateLock)
        {
            shouldSave = _savePending && Environment.TickCount64 >= _saveAfterMilliseconds;
        }

        if (shouldSave)
        {
            SaveNow();
        }
    }

    private void MarkChangedLocked()
    {
        Interlocked.Increment(ref _revision);
        _savePending = true;
        _saveAfterMilliseconds = Environment.TickCount64 + SaveDelayMilliseconds;
    }

    private bool TryReserveIdentities(IReadOnlyCollection<Guid> ids)
    {
        if (_identityRegistry.TryReplaceOwnedItems(this, ids, out Guid conflictingId))
        {
            return true;
        }

        _logger.LogError("display state conflicts with scene item {ItemId}", conflictingId);
        return false;
    }

    private void SaveNow()
    {
        lock (_saveLock)
        {
            IReadOnlyList<DisplaySnapshot> snapshots;
            long revision;
            lock (_stateLock)
            {
                snapshots = _snapshots.Values
                    .OrderBy(static snapshot => snapshot.CreatedAtUtc)
                    .ToList();
                revision = Revision;
            }

            bool saved = _persistence.TrySave(snapshots);
            lock (_stateLock)
            {
                if (saved && revision == Revision)
                {
                    _savePending = false;
                    return;
                }

                _savePending = true;
                _saveAfterMilliseconds = Environment.TickCount64 + SaveDelayMilliseconds;
            }
        }
    }

    private bool TryRestoreLocked(DisplaySnapshot snapshot)
    {
        if (_snapshots.ContainsKey(snapshot.Id)
            || !TryReserveIdentities(_snapshots.Keys.Append(snapshot.Id).ToArray()))
        {
            return false;
        }

        _snapshots.Add(snapshot.Id, snapshot);
        MarkChangedLocked();
        return true;
    }

    private string NextNameLocked()
    {
        int index = 1;
        var names = _snapshots.Values
            .Select(static snapshot => snapshot.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        while (names.Contains($"Display {index:00}"))
        {
            ++index;
        }

        return $"Display {index:00}";
    }

    private void LoadOwnedSnapshots()
    {
        DisplaySnapshot[] snapshots = _persistence.Load()
            .Select(Sanitize)
            .OrderBy(static entry => entry.CreatedAtUtc)
            .ToArray();
        for (int index = 0; index < snapshots.Length; ++index)
        {
            DisplaySnapshot snapshot = snapshots[index];
            if (!_snapshots.TryAdd(snapshot.Id, snapshot))
            {
                _logger.LogWarning("ignored duplicate persisted display {ItemId}", snapshot.Id);
            }
        }

        while (_snapshots.Count > 0
               && !_identityRegistry.TryReplaceOwnedItems(this, _snapshots.Keys.ToArray(), out Guid conflictingId))
        {
            if (conflictingId != Guid.Empty && _snapshots.Remove(conflictingId))
            {
                _logger.LogWarning("ignored persisted display with conflicting scene item id {ItemId}", conflictingId);
                continue;
            }

            _logger.LogError("could not register persisted display identities");
            _snapshots.Clear();
        }
    }

    private static DisplaySnapshot Sanitize(DisplaySnapshot snapshot)
    {
        DisplaySettings settings = snapshot.Settings;
        return snapshot with
        {
            Id = snapshot.Id == Guid.Empty ? Guid.NewGuid() : snapshot.Id,
            Name = string.IsNullOrWhiteSpace(snapshot.Name) ? "Display" : snapshot.Name.Trim(),
            Transform = snapshot.Transform with
            {
                Position = NumericsUtility.IsFinite(snapshot.Transform.Position)
                    ? snapshot.Transform.Position
                    : Vector3.Zero,
                RotationDegrees = NumericsUtility.IsFinite(snapshot.Transform.RotationDegrees)
                    ? snapshot.Transform.RotationDegrees
                    : Vector3.Zero,
                Scale = ClampScale(snapshot.Transform.Scale),
            },
            Settings = settings with
            {
                Target = settings.Target?.Normalize() ?? new WindowsCaptureTargetDescriptor(),
                Size = NumericsUtility.IsFinite(settings.Size)
                    ? Vector2.Clamp(settings.Size, new Vector2(0.1f), new Vector2(100f))
                    : DisplaySettings.DefaultSize,
                Tint = NumericsUtility.IsFinite(settings.Tint)
                    ? ColorUtility.ClampNormalizedColor(settings.Tint)
                    : Vector4.One,
                UvRect = NumericsUtility.IsFinite(settings.UvRect)
                    ? Vector4.Clamp(settings.UvRect, Vector4.Zero, Vector4.One)
                    : DisplaySettings.DefaultUvRect,
            },
        };
    }

    private static DisplaySnapshot CreateDuplicate(DisplaySnapshot source)
        => source with
        {
            Id = Guid.NewGuid(),
            Name = $"{source.Name} Copy",
            CreatedAtUtc = DateTime.UtcNow,
        };

    private static Vector3 ClampScale(Vector3 scale)
        => NumericsUtility.IsFinite(scale)
            ? Vector3.Max(scale, new Vector3(0.01f))
            : Vector3.One;
}
