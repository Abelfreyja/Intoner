using Intoner.Objects.Models;
using Intoner.Objects.Utils;
using Intoner.Scene;

namespace Intoner.Objects.Runtime;

/// <summary> validates object identities across persistent and temporary object sources </summary>
internal interface IObjectIdentityService
{
    /// <summary> gets the combined version of all scene identity owners </summary>
    long Revision { get; }

    /// <summary> validates the complete staged persistent identity set against all scene domains </summary>
    bool TryValidatePersistentState(out Guid conflictingId);

    /// <summary> validates identities before adding one saved layout </summary>
    bool TryValidateSavedLayoutAddition(
        IReadOnlyList<ObjectSnapshot> snapshots,
        out Guid conflictingId,
        IReadOnlyList<SceneGroupSnapshot>? groups = null);

    /// <summary> validates identities for a complete saved layout set replacement </summary>
    bool TryValidateSavedLayoutSet(
        IReadOnlyList<ObjectLayoutSnapshot> layouts,
        out Guid conflictingId);

    /// <summary> validates identities for a complete current persistent scene replacement </summary>
    bool TryValidatePersistentSceneReplacement(
        IReadOnlyList<ObjectSnapshot> snapshots,
        IReadOnlySet<Guid> replacedLayoutIds,
        out Guid conflictingId,
        IReadOnlyList<SceneGroupSnapshot>? groups = null);

    /// <summary> validates one identity before adding a persistent object </summary>
    bool TryValidatePersistentObject(Guid objectId, out Guid conflictingId);

    /// <summary> validates identities for one complete temporary source replacement </summary>
    bool TryValidateTemporarySourceReplacement(
        string sourceKey,
        IReadOnlyList<ObjectSnapshot> runtimeSnapshots,
        out Guid conflictingId);
}

internal sealed class ObjectIdentityService : IObjectIdentityService
{
    private readonly Lock _stateLock;
    private readonly ISceneIdentityRegistry _registry;
    private readonly ObjectIdentitySource _source;

    public ObjectIdentityService(
        ObjectStateLock stateLock,
        ISceneIdentityRegistry registry,
        ObjectIdentitySource source)
    {
        _stateLock = stateLock.Value;
        _registry = registry;
        _source = source;
    }

    public long Revision => _registry.Revision;

    public bool TryValidatePersistentState(out Guid conflictingId)
    {
        lock (_stateLock)
            return _registry.TryValidateReplacement(_source, _source.GetOwnedItemIds(), out conflictingId);
    }

    public bool TryValidateSavedLayoutAddition(
        IReadOnlyList<ObjectSnapshot> snapshots,
        out Guid conflictingId,
        IReadOnlyList<SceneGroupSnapshot>? groups = null)
    {
        lock (_stateLock)
        {
            IReadOnlyList<ObjectLayoutSnapshot> layouts = _source.GetLayouts();
            return TryValidate(
                _source.GetStandaloneSnapshots(),
                layouts.Select(static layout => layout.Objects).Append(snapshots),
                GetTemporaryObjectSets(),
                out conflictingId,
                GetGroupIds(layouts).Concat((groups ?? []).Select(static group => group.Id)));
        }
    }

    public bool TryValidateSavedLayoutSet(
        IReadOnlyList<ObjectLayoutSnapshot> layouts,
        out Guid conflictingId)
    {
        lock (_stateLock)
        {
            return TryValidate(
                _source.GetStandaloneSnapshots(),
                layouts.Select(static layout => layout.Objects),
                GetTemporaryObjectSets(),
                out conflictingId,
                GetGroupIds(layouts));
        }
    }

    public bool TryValidatePersistentSceneReplacement(
        IReadOnlyList<ObjectSnapshot> snapshots,
        IReadOnlySet<Guid> replacedLayoutIds,
        out Guid conflictingId,
        IReadOnlyList<SceneGroupSnapshot>? groups = null)
    {
        lock (_stateLock)
        {
            ObjectLayoutSnapshot[] retainedLayouts = _source.GetLayouts()
                .Where(layout => !replacedLayoutIds.Contains(layout.Id)).ToArray();
            return TryValidate(
                snapshots,
                retainedLayouts.Select(static layout => layout.Objects),
                GetTemporaryObjectSets(),
                out conflictingId,
                (groups ?? []).Select(static group => group.Id).Concat(retainedLayouts.SelectMany(static layout => layout.Groups)
                    .Select(static group => group.Id)));
        }
    }

    public bool TryValidatePersistentObject(Guid objectId, out Guid conflictingId)
    {
        lock (_stateLock)
        {
            return _registry.TryValidateReplacement(_source, _source.GetOwnedItemIds().Append([objectId]), out conflictingId);
        }
    }

    public bool TryValidateTemporarySourceReplacement(
        string sourceKey,
        IReadOnlyList<ObjectSnapshot> runtimeSnapshots,
        out Guid conflictingId)
    {
        string normalizedSourceKey = TemporarySourceUtility.NormalizeKey(sourceKey);
        if (normalizedSourceKey.Length == 0)
        {
            conflictingId = Guid.Empty;
            return false;
        }

        lock (_stateLock)
        {
            IReadOnlyList<ObjectLayoutSnapshot> layouts = _source.GetLayouts();
            IEnumerable<IReadOnlyList<ObjectSnapshot>> temporarySources = _source.GetTemporarySources()
                .Where(source => !string.Equals(source.SourceKey, normalizedSourceKey, StringComparison.OrdinalIgnoreCase))
                .Select(static source => source.RuntimeObjects)
                .Append(runtimeSnapshots);
            return TryValidate(
                _source.GetStandaloneSnapshots(),
                layouts.Select(static layout => layout.Objects),
                temporarySources,
                out conflictingId,
                GetGroupIds(layouts));
        }
    }

    private bool TryValidate(
        IReadOnlyList<ObjectSnapshot> standaloneSnapshots,
        IEnumerable<IReadOnlyList<ObjectSnapshot>> layoutSnapshots,
        IEnumerable<IReadOnlyList<ObjectSnapshot>> temporarySnapshots,
        out Guid conflictingId,
        IEnumerable<Guid> groupIds)
        => _registry.TryValidateReplacement(
            _source,
            GetObjectItemIdSets(standaloneSnapshots, layoutSnapshots, temporarySnapshots).Append(groupIds),
            out conflictingId);

    private IEnumerable<Guid> GetGroupIds(IEnumerable<ObjectLayoutSnapshot> layouts)
        => _source.GetStandaloneGroups().Select(static group => group.Id)
            .Concat(layouts.SelectMany(static layout => layout.Groups).Select(static group => group.Id));

    private IEnumerable<IReadOnlyList<ObjectSnapshot>> GetTemporaryObjectSets()
        => _source.GetTemporarySources().Select(static source => source.RuntimeObjects);

    private static IEnumerable<IEnumerable<Guid>> GetObjectItemIdSets(
        IReadOnlyList<ObjectSnapshot> standaloneSnapshots,
        IEnumerable<IReadOnlyList<ObjectSnapshot>> layoutSnapshots,
        IEnumerable<IReadOnlyList<ObjectSnapshot>> temporarySnapshots)
    {
        yield return standaloneSnapshots.Select(static snapshot => snapshot.Id);
        foreach (IReadOnlyList<ObjectSnapshot> snapshots in layoutSnapshots)
        {
            yield return snapshots.Select(static snapshot => snapshot.Id);
        }

        foreach (IReadOnlyList<ObjectSnapshot> snapshots in temporarySnapshots)
        {
            yield return snapshots.Select(static snapshot => snapshot.Id);
        }
    }
}

/// <summary> exposes the current object owned identities to the scene registry </summary>
internal sealed class ObjectIdentitySource : ISceneIdentitySource
{
    private readonly IObjectPersistenceState _persistenceState;
    private readonly ITemporarySourceStore _temporarySourceStore;

    public ObjectIdentitySource(
        IObjectPersistenceState persistenceState,
        ITemporarySourceStore temporarySourceStore)
    {
        _persistenceState = persistenceState;
        _temporarySourceStore = temporarySourceStore;
    }

    public long Revision => unchecked(_persistenceState.StoredRevision + _temporarySourceStore.Revision);

    public IReadOnlyList<ObjectSnapshot> GetStandaloneSnapshots()
        => _persistenceState.GetStandaloneSnapshots();

    public IReadOnlyList<ObjectLayoutSnapshot> GetLayouts()
        => _persistenceState.GetStoredLayouts();

    public IReadOnlyList<SceneGroupSnapshot> GetStandaloneGroups()
        => _persistenceState.GetStandaloneGroups();

    public IReadOnlyList<ObjectTemporarySourceSnapshot> GetTemporarySources()
        => _temporarySourceStore.GetSources();

    public IEnumerable<IEnumerable<Guid>> GetOwnedItemIds()
    {
        yield return GetStandaloneSnapshots().Select(static snapshot => snapshot.Id);
        yield return GetStandaloneGroups().Select(static group => group.Id);
        foreach (ObjectLayoutSnapshot layout in GetLayouts())
        {
            yield return layout.Objects.Select(static snapshot => snapshot.Id).Concat(layout.Groups.Select(static group => group.Id));
        }

        foreach (ObjectTemporarySourceSnapshot source in GetTemporarySources())
        {
            yield return source.RuntimeObjects.Select(static snapshot => snapshot.Id);
        }
    }
}
