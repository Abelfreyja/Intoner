using Intoner.Objects.Utils;
using Intoner.Utils;

namespace Intoner.Objects.Resources;

/// <summary> shared object resource load collection scope </summary>
internal sealed class ObjectResourceLoadScope : IDisposable
{
    internal readonly record struct LoadContext(ObjectCollectionResolveData? Collection, RootCacheIsolation? RootCache = null, CollisionRequest? Collision = null);

    internal sealed class RootCacheIsolation(ObjectResourceLoadScope owner, string path, long loadId, RootCacheIsolation? previousScope) : IDisposable
    {
        private int _threadScopeActive = 1;

        public string Path { get; } = path;
        public long LoadId { get; } = loadId;
        public bool IsActive => Volatile.Read(ref _threadScopeActive) != 0;

        public void RestoreThreadScope()
        {
            if (Interlocked.Exchange(ref _threadScopeActive, 0) == 0
             || !owner.TryReadContext(out LoadContext context))
            {
                return;
            }

            owner.TryWriteContext(context with { RootCache = previousScope });
        }

        public void Dispose()
            => RestoreThreadScope();
    }

    internal sealed class CollisionRequest(
        ObjectResourceLoadScope owner, string path, ObjectCollectionResolveData? collection, CollisionRequest? previous) : IDisposable
    {
        private int _state;

        public ObjectCollectionResolveData? Collection { get; } = collection;

        public bool TryConsume(string requestedPath)
            => path.Length > 0 && string.Equals(path, requestedPath, StringComparison.OrdinalIgnoreCase)
                && Interlocked.CompareExchange(ref _state, 1, 0) == 0;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _state, 2) != 2
             && owner.TryReadContext(out LoadContext context)
             && ReferenceEquals(context.Collision, this))
            {
                owner.TryWriteContext(context with { Collision = previous });
            }
        }
    }

    public CollisionRequest? EnterCollisionRequest(string path)
    {
        if (!TryReadContext(out LoadContext context))
        {
            return null;
        }

        if (context.Collision is null && (path.Length == 0 || context.Collection is null))
        {
            return null;
        }

        CollisionRequest request = new(this, path, context.Collection, context.Collision);
        return TryWriteContext(context with { Collision = request }) ? request : null;
    }

    public bool TryConsumeCollisionRequest(string path, out ObjectCollectionResolveData? collection)
    {
        collection = null;
        if (!TryReadContext(out LoadContext context) || context.Collision is not { } request || !request.TryConsume(path))
        {
            return false;
        }

        collection = request.Collection;
        return true;
    }

    private readonly IObjectResolvedCollectionStore _collectionStore;
    private readonly ThreadLocal<LoadContext> _activeContext = new(static () => default);
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
        if (!TryReadContext(out LoadContext previous)
         || !TryWriteContext(new LoadContext(lease?.Snapshot)))
        {
            lease?.Dispose();
            return default;
        }

        return new ObjectResourceLoadScopeToken(this, previous, lease);
    }

    public RootCacheIsolation? EnterRootCacheIsolation(string path, long loadId)
    {
        if (!TryReadContext(out LoadContext previous))
        {
            return null;
        }

        RootCacheIsolation isolation = new(this, path, loadId, previous.RootCache);

        if (TryWriteContext(previous with { RootCache = isolation }))
        {
            return isolation;
        }

        isolation.Dispose();
        return null;
    }

    public bool TryConsumeRootCacheIsolation(string path, out long loadId)
    {
        loadId = 0;

        if (!TryReadContext(out LoadContext context)
         || context.RootCache is not { IsActive: true } isolation
         || !string.Equals(path, isolation.Path, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        loadId = isolation.LoadId;
        isolation.RestoreThreadScope();
        return true;
    }

    public bool TryReadActiveCollectionId(out string collectionId)
    {
        collectionId = string.Empty;
        if (!TryReadContext(out LoadContext context))
        {
            return false;
        }

        collectionId = context.Collection?.CollectionId ?? string.Empty;
        return true;
    }

    public bool TryReadActiveCollection(out ObjectCollectionResolveData collection)
    {
        collection = null!;
        if (!TryReadContext(out LoadContext context) || context.Collection is null)
        {
            return false;
        }

        collection = context.Collection;
        return true;
    }

    public void Dispose()
    {
        if (!_disposeState.TryBeginDispose())
        {
            return;
        }

        _activeContext.Dispose();
    }

    private bool TryReadContext(out LoadContext context)
        => ObjectThreadLocalUtility.TryRead(_activeContext, default, out context) && !_disposeState.IsDisposing;

    private bool TryWriteContext(LoadContext context)
        => !_disposeState.IsDisposing && ObjectThreadLocalUtility.TryWrite(_activeContext, context);

    internal void RestoreContext(LoadContext previous)
        => _ = TryWriteContext(previous);
}

internal readonly struct ObjectResourceLoadScopeToken : IDisposable
{
    private readonly ObjectResourceLoadScope? _owner;
    private readonly ObjectResourceLoadScope.LoadContext _previousContext;
    private readonly ObjectResourceCollectionLease? _lease;

    public ObjectResourceLoadScopeToken(
        ObjectResourceLoadScope owner,
        ObjectResourceLoadScope.LoadContext previousContext,
        ObjectResourceCollectionLease? lease)
    {
        _owner = owner;
        _previousContext = previousContext;
        _lease = lease;
    }

    public ObjectCollectionResolveData? Collection
        => _lease?.Snapshot;

    public bool IsActive
        => _lease != null;

    public void Dispose()
    {
        _owner?.RestoreContext(_previousContext);
        _lease?.Dispose();
    }
}
