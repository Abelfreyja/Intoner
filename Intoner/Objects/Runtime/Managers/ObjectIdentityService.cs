using Intoner.Objects.Models;
using Intoner.Objects.Utils;
using Intoner.Scene;

namespace Intoner.Objects.Runtime;

/// <summary> validates object identities across persistent and temporary object sources </summary>
internal interface IObjectIdentityService
{
    /// <summary> validates identities before adding one saved layout </summary>
    bool TryValidateSavedLayoutAddition(
        IReadOnlyList<ObjectSnapshot> snapshots,
        out Guid conflictingId);

    /// <summary> validates identities for a complete saved layout set replacement </summary>
    bool TryValidateSavedLayoutSet(
        IReadOnlyList<ObjectLayoutSnapshot> layouts,
        out Guid conflictingId);

    /// <summary> validates the persistent scene produced by selecting one saved layout </summary>
    bool TryValidatePersistentSceneSelection(
        IReadOnlyList<ObjectSnapshot> selectedLayoutSnapshots,
        out Guid conflictingId);

    /// <summary> validates identities for a complete current persistent scene replacement </summary>
    bool TryValidatePersistentSceneReplacement(
        IReadOnlyList<ObjectSnapshot> snapshots,
        Guid? replacedLayoutId,
        out Guid conflictingId);

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

    public bool TryValidateSavedLayoutAddition(
        IReadOnlyList<ObjectSnapshot> snapshots,
        out Guid conflictingId)
    {
        lock (_stateLock)
        {
            return TryValidate(
                _source.GetStandaloneSnapshots(),
                _source.GetLayouts().Select(static layout => layout.Objects).Append(snapshots),
                GetTemporaryObjectSets(),
                out conflictingId);
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
                out conflictingId);
        }
    }

    public bool TryValidatePersistentSceneSelection(
        IReadOnlyList<ObjectSnapshot> selectedLayoutSnapshots,
        out Guid conflictingId)
    {
        lock (_stateLock)
        {
            IEnumerable<IReadOnlyList<ObjectSnapshot>> selectedLayout = selectedLayoutSnapshots.Count == 0
                ? []
                : [selectedLayoutSnapshots];
            return TryValidate(
                _source.GetStandaloneSnapshots(),
                selectedLayout,
                GetTemporaryObjectSets(),
                out conflictingId);
        }
    }

    public bool TryValidatePersistentSceneReplacement(
        IReadOnlyList<ObjectSnapshot> snapshots,
        Guid? replacedLayoutId,
        out Guid conflictingId)
    {
        lock (_stateLock)
        {
            return TryValidate(
                snapshots,
                _source.GetLayouts()
                    .Where(layout => layout.Id != replacedLayoutId)
                    .Select(static layout => layout.Objects),
                GetTemporaryObjectSets(),
                out conflictingId);
        }
    }

    public bool TryValidatePersistentObject(Guid objectId, out Guid conflictingId)
    {
        lock (_stateLock)
        {
            IEnumerable<IEnumerable<Guid>> itemSets = GetObjectItemIdSets(
                    _source.GetStandaloneSnapshots(),
                    _source.GetLayouts().Select(static layout => layout.Objects),
                    GetTemporaryObjectSets())
                .Append([objectId]);
            return _registry.TryValidateReplacement(_source, itemSets, out conflictingId);
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
            IEnumerable<IReadOnlyList<ObjectSnapshot>> temporarySources = _source.GetTemporarySources()
                .Where(source => !string.Equals(source.SourceKey, normalizedSourceKey, StringComparison.OrdinalIgnoreCase))
                .Select(static source => source.RuntimeObjects)
                .Append(runtimeSnapshots);
            return TryValidate(
                _source.GetStandaloneSnapshots(),
                _source.GetLayouts().Select(static layout => layout.Objects),
                temporarySources,
                out conflictingId);
        }
    }

    private bool TryValidate(
        IReadOnlyList<ObjectSnapshot> standaloneSnapshots,
        IEnumerable<IReadOnlyList<ObjectSnapshot>> layoutSnapshots,
        IEnumerable<IReadOnlyList<ObjectSnapshot>> temporarySnapshots,
        out Guid conflictingId)
        => _registry.TryValidateReplacement(
            _source,
            GetObjectItemIdSets(standaloneSnapshots, layoutSnapshots, temporarySnapshots),
            out conflictingId);

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
    private readonly IObjectLayoutManager _layoutManager;
    private readonly ITemporarySourceStore _temporarySourceStore;

    public ObjectIdentitySource(
        IObjectPersistenceState persistenceState,
        IObjectLayoutManager layoutManager,
        ITemporarySourceStore temporarySourceStore)
    {
        _persistenceState = persistenceState;
        _layoutManager = layoutManager;
        _temporarySourceStore = temporarySourceStore;
    }

    public IReadOnlyList<ObjectSnapshot> GetStandaloneSnapshots()
        => _persistenceState.GetStandaloneSnapshots();

    public IReadOnlyList<ObjectLayoutSnapshot> GetLayouts()
        => _layoutManager.GetLayouts();

    public IReadOnlyList<ObjectTemporarySourceSnapshot> GetTemporarySources()
        => _temporarySourceStore.GetSources();

    public IEnumerable<IEnumerable<Guid>> GetOwnedItemIds()
    {
        yield return GetStandaloneSnapshots().Select(static snapshot => snapshot.Id);
        foreach (ObjectLayoutSnapshot layout in GetLayouts())
        {
            yield return layout.Objects.Select(static snapshot => snapshot.Id);
        }

        foreach (ObjectTemporarySourceSnapshot source in GetTemporarySources())
        {
            yield return source.RuntimeObjects.Select(static snapshot => snapshot.Id);
        }
    }
}
