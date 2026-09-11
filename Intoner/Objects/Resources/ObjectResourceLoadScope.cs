using Intoner.Objects.Utils;

using Intoner.Utils;

namespace Intoner.Objects.Resources;

/// <summary> shared object resource load collection scope </summary>
internal sealed class ObjectResourceLoadScope : IDisposable
{
    private readonly IObjectResolvedCollectionStore _collectionStore;
    private readonly ThreadLocal<ObjectCollectionResolveData?> _activeCollection = new(static () => null);
    private readonly DisposalState _disposeState = new();

    public ObjectResourceLoadScope(IObjectResolvedCollectionStore collectionStore)
        => _collectionStore = collectionStore;

    public ObjectResourceLoadScopeToken EnterCollectionScope(string collectionId)
        => EnterScope(_collectionStore.AcquireCollection(collectionId));

    public ObjectResourceLoadScopeToken EnterResourceScope(long resourceScopeId)
        => EnterScope(_collectionStore.AcquireResourceScope(resourceScopeId));

    public ObjectResourceLoadScopeToken Suspend()
        => EnterScope(null);

    private ObjectResourceLoadScopeToken EnterScope(ObjectResourceCollectionLease? lease)
    {
        if (_disposeState.IsDisposing
         || !ObjectThreadLocalUtility.TryRead(_activeCollection, null, out ObjectCollectionResolveData? previousCollection)
         || !TryWriteActiveCollection(lease?.Snapshot))
        {
            lease?.Dispose();
            return default;
        }

        return new ObjectResourceLoadScopeToken(this, previousCollection, lease);
    }

    public bool TryReadActiveCollectionId(out string collectionId)
    {
        collectionId = string.Empty;
        if (_disposeState.IsDisposing)
        {
            return false;
        }

        if (!ObjectThreadLocalUtility.TryRead(_activeCollection, null, out ObjectCollectionResolveData? collection))
        {
            return false;
        }

        collectionId = collection?.CollectionId ?? string.Empty;
        return true;
    }

    public bool TryReadActiveCollection(out ObjectCollectionResolveData collection)
    {
        collection = null!;
        if (_disposeState.IsDisposing
         || !ObjectThreadLocalUtility.TryRead(_activeCollection, null, out ObjectCollectionResolveData? active)
         || active is null)
        {
            return false;
        }

        collection = active;
        return true;
    }

    public void Dispose()
    {
        if (!_disposeState.TryBeginDispose())
        {
            return;
        }

        _activeCollection.Dispose();
    }

    private bool TryWriteActiveCollection(ObjectCollectionResolveData? collection)
    {
        if (_disposeState.IsDisposing)
        {
            return false;
        }

        return ObjectThreadLocalUtility.TryWrite(_activeCollection, collection);
    }

    internal void RestoreCollectionScope(ObjectCollectionResolveData? previousCollection)
        => _ = TryWriteActiveCollection(previousCollection);
}

internal readonly struct ObjectResourceLoadScopeToken : IDisposable
{
    private readonly ObjectResourceLoadScope? _owner;
    private readonly ObjectCollectionResolveData? _previousCollection;
    private readonly ObjectResourceCollectionLease? _lease;

    public ObjectResourceLoadScopeToken(
        ObjectResourceLoadScope owner,
        ObjectCollectionResolveData? previousCollection,
        ObjectResourceCollectionLease? lease)
    {
        _owner = owner;
        _previousCollection = previousCollection;
        _lease = lease;
    }

    public ObjectCollectionResolveData? Collection
        => _lease?.Snapshot;

    public bool IsActive
        => _lease != null;

    public void Dispose()
    {
        _owner?.RestoreCollectionScope(_previousCollection);
        _lease?.Dispose();
    }
}


