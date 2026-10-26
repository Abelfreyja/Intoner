using Intoner.Objects.Runtime;

namespace Intoner.Scene;

/// <summary> provides current scene item identities owned outside the registry </summary>
internal interface ISceneIdentitySource
{
    /// <summary> gets the version of the item sets returned by this source </summary>
    long Revision { get; }

    /// <summary> gets each independently owned scene item id set </summary>
    IEnumerable<IEnumerable<Guid>> GetOwnedItemIds();
}

/// <summary> validates and reserves process wide scene item identities </summary>
internal interface ISceneIdentityRegistry
{
    /// <summary> gets the combined version of all registered identity owners </summary>
    long Revision { get; }

    /// <summary> checks new identities against all registered owners in one batch </summary>
    bool TryValidateNewItems(IReadOnlyCollection<Guid> itemIds, out Guid conflictingId);

    /// <summary> validates a complete replacement for one registered identity source </summary>
    bool TryValidateReplacement(
        ISceneIdentitySource source,
        IEnumerable<IEnumerable<Guid>> replacementSets,
        out Guid conflictingId);

    /// <summary> atomically replaces the ids owned directly by one scene item owner </summary>
    bool TryReplaceOwnedItems(
        object owner,
        IReadOnlyCollection<Guid> itemIds,
        out Guid conflictingId);

    /// <summary> removes all ids owned directly by one scene item owner </summary>
    void RemoveOwnedItems(object owner);
}

internal sealed class SceneIdentityRegistry : ISceneIdentityRegistry
{
    private readonly Lock _stateLock;
    private long _revision;
    private readonly IReadOnlyList<ISceneIdentitySource> _sources;
    private readonly Dictionary<object, HashSet<Guid>> _ownedItemIds = new(ReferenceEqualityComparer.Instance);

    public SceneIdentityRegistry(
        ObjectStateLock stateLock,
        IEnumerable<ISceneIdentitySource> sources)
    {
        _stateLock = stateLock.Value;
        _sources = sources.ToList();
    }

    public long Revision
    {
        get
        {
            lock (_stateLock)
            {
                long revision = _revision;
                for (int index = 0; index < _sources.Count; ++index)
                {
                    revision = unchecked(revision + _sources[index].Revision);
                }

                return revision;
            }
        }
    }

    public bool TryValidateNewItems(IReadOnlyCollection<Guid> itemIds, out Guid conflictingId)
    {
        lock (_stateLock)
        {
            return TryValidate(excludedSource: null, excludedOwner: null, [itemIds], out conflictingId);
        }
    }

    public bool TryValidateReplacement(
        ISceneIdentitySource source,
        IEnumerable<IEnumerable<Guid>> replacementSets,
        out Guid conflictingId)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(replacementSets);
        lock (_stateLock)
        {
            return TryValidate(source, excludedOwner: null, replacementSets, out conflictingId);
        }
    }

    public bool TryReplaceOwnedItems(
        object owner,
        IReadOnlyCollection<Guid> itemIds,
        out Guid conflictingId)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(itemIds);
        lock (_stateLock)
        {
            if (!TryValidate(excludedSource: null, owner, [itemIds], out conflictingId))
            {
                return false;
            }

            if (_ownedItemIds.TryGetValue(owner, out HashSet<Guid>? current) && current.SetEquals(itemIds))
            {
                return true;
            }

            if (itemIds.Count == 0)
            {
                if (_ownedItemIds.Remove(owner))
                {
                    ++_revision;
                }
            }
            else
            {
                _ownedItemIds[owner] = [.. itemIds];
                ++_revision;
            }

            return true;
        }
    }

    public void RemoveOwnedItems(object owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        lock (_stateLock)
        {
            if (_ownedItemIds.Remove(owner))
            {
                ++_revision;
            }
        }
    }

    private bool TryValidate(
        ISceneIdentitySource? excludedSource,
        object? excludedOwner,
        IEnumerable<IEnumerable<Guid>> replacementSets,
        out Guid conflictingId)
    {
        var ownedIds = new HashSet<Guid>();
        foreach (ISceneIdentitySource source in _sources)
        {
            if (ReferenceEquals(source, excludedSource))
            {
                continue;
            }

            foreach (IEnumerable<Guid> itemIds in source.GetOwnedItemIds())
            {
                if (!SceneIdentityValidation.TryAdd(itemIds, ownedIds, out conflictingId))
                {
                    return false;
                }
            }
        }

        foreach ((object owner, HashSet<Guid> itemIds) in _ownedItemIds)
        {
            if (!ReferenceEquals(owner, excludedOwner)
                && !SceneIdentityValidation.TryAdd(itemIds, ownedIds, out conflictingId))
            {
                return false;
            }
        }

        foreach (IEnumerable<Guid> itemIds in replacementSets)
        {
            if (!SceneIdentityValidation.TryAdd(itemIds, ownedIds, out conflictingId))
            {
                return false;
            }
        }

        conflictingId = Guid.Empty;
        return true;
    }

}

internal static class SceneIdentityValidation
{
    public static bool TryValidate(
        IEnumerable<IEnumerable<Guid>> itemSets,
        out Guid conflictingId)
    {
        var ownedIds = new HashSet<Guid>();
        foreach (IEnumerable<Guid> itemIds in itemSets)
        {
            if (!TryAdd(itemIds, ownedIds, out conflictingId))
            {
                return false;
            }
        }

        conflictingId = Guid.Empty;
        return true;
    }

    public static bool TryAdd(
        IEnumerable<Guid> itemIds,
        HashSet<Guid> ownedIds,
        out Guid conflictingId)
    {
        foreach (Guid itemId in itemIds)
        {
            if (itemId == Guid.Empty || !ownedIds.Add(itemId))
            {
                conflictingId = itemId;
                return false;
            }
        }

        conflictingId = Guid.Empty;
        return true;
    }
}
