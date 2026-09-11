using Intoner.Objects.Utils;
using Microsoft.Extensions.Logging;

namespace Intoner.Objects.Resources;

/// <summary> one tracked object resource handle scope </summary>
internal readonly record struct ObjectResourceScope
{
    internal ObjectResourceScope(string resourceCollectionId, string resolvedPath, long resourceScopeId = 0)
    {
        ResourceCollectionId = ObjectCollectionKeyUtility.NormalizeCollectionId(resourceCollectionId);
        ResolvedPath = ObjectResourcePathUtility.NormalizeTrackedPath(resolvedPath);
        ResourceScopeId = resourceScopeId;
    }

    public string ResourceCollectionId { get; } = string.Empty;
    public string ResolvedPath { get; } = string.Empty;
    public long ResourceScopeId { get; }
}

internal enum ObjectResourceRegistrationKind
{
    RootHandle,
    RootInstance,
}

/// <summary> tracked native object root resource registration </summary>
internal struct ObjectResourceRegistration
{
    private nint _address;
    private Guid _ownerId;
    private ObjectResourceScope _scope;
    private ObjectResourceRegistrationKind _kind;

    public bool IsRegistered
        => _address != nint.Zero;

    public void UpdateRootHandle(ObjectResourceTracker tracker, nint handle, ObjectResourceScope scope)
        => Update(tracker, ObjectResourceRegistrationKind.RootHandle, handle, scope);

    public void UpdateRootInstance(ObjectResourceTracker tracker, nint instance, ObjectResourceScope scope)
        => Update(tracker, ObjectResourceRegistrationKind.RootInstance, instance, scope);

    public void Clear(ObjectResourceTracker tracker)
    {
        if (_address == nint.Zero)
        {
            return;
        }

        switch (_kind)
        {
            case ObjectResourceRegistrationKind.RootInstance:
                tracker.RemoveRootInstance(_address, _ownerId);
                break;
            default:
                tracker.RemoveRootHandle(_address, _ownerId);
                break;
        }

        _address = nint.Zero;
        _scope = default;
        _kind = default;
    }

    private void Update(
        ObjectResourceTracker tracker,
        ObjectResourceRegistrationKind kind,
        nint address,
        ObjectResourceScope scope)
    {
        if (address == nint.Zero || scope.ResourceCollectionId.Length == 0 || scope.ResolvedPath.Length == 0)
        {
            Clear(tracker);
            return;
        }

        _ownerId = _ownerId == Guid.Empty ? Guid.NewGuid() : _ownerId;

        if (_address == address && _kind == kind && _scope == scope)
        {
            return;
        }

        if (_address != address || _kind != kind)
        {
            Clear(tracker);
        }

        if (kind == ObjectResourceRegistrationKind.RootInstance)
        {
            tracker.RegisterOrUpdateRootInstance(address, _ownerId, scope);
        }
        else
        {
            tracker.RegisterOrUpdateRootHandle(address, _ownerId, scope);
        }

        _address = address;
        _kind = kind;
        _scope = scope;
    }
}

/// <summary> retains collection generations for native roots and dependent resource loads </summary>
internal sealed class ObjectResourceTracker : IDisposable
{
    private sealed class TrackedResourceScopes : IDisposable
    {
        public readonly Dictionary<Guid, ObjectResourceScope> RootScopes = [];
        public readonly HashSet<ObjectResourceScope> RedirectedScopes = [];
        public readonly Dictionary<long, ObjectResourceCollectionLease> Leases = [];

        public void Dispose()
        {
            foreach (ObjectResourceCollectionLease lease in Leases.Values)
            {
                lease.Dispose();
            }
        }
    }

    private readonly ILogger<ObjectResourceTracker> _logger;
    private readonly IObjectResolvedCollectionStore _collectionStore;
    private readonly ObjectResourceLoadScope _loadScope;

    private readonly Lock _lock = new();
    private readonly Dictionary<nint, TrackedResourceScopes> _handleScopes = [];
    private readonly Dictionary<nint, TrackedResourceScopes> _instanceScopes = [];
    private bool _disposed;

    public ObjectResourceTracker(
        ILogger<ObjectResourceTracker> logger,
        IObjectResolvedCollectionStore collectionStore,
        ObjectResourceLoadScope loadScope)
    {
        _logger = logger;
        _collectionStore = collectionStore;
        _loadScope = loadScope;
    }

    public void RegisterOrUpdateRootHandle(nint resourceHandleAddress, Guid ownerId, ObjectResourceScope handleScope)
        => RegisterOrUpdateRootScope(_handleScopes, resourceHandleAddress, ownerId, handleScope);

    public void RegisterOrUpdateRootInstance(nint instanceAddress, Guid ownerId, ObjectResourceScope instanceScope)
        => RegisterOrUpdateRootScope(_instanceScopes, instanceAddress, ownerId, instanceScope);

    public ObjectResourceLoadScopeToken EnterHandleScope(nint resourceHandleAddress, ObjectScopedResourcePath path)
    {
        ObjectResourceLoadScopeToken scope = _loadScope.EnterResourceScope(path.ResourceScopeId);
        try
        {
            if (scope.Collection is { } collection)
            {
                RegisterOrUpdateHandleScope(resourceHandleAddress, new ObjectResourceScope(collection.CollectionId, path.Path, path.ResourceScopeId));
            }

            return scope;
        }
        catch
        {
            scope.Dispose();
            throw;
        }
    }

    public void RegisterOrUpdateHandleScope(nint resourceHandleAddress, ObjectResourceScope handleScope)
    {
        ValidateRegistration(resourceHandleAddress, handleScope);

        using ObjectResourceLog.WriteScope logWrites = ObjectResourceLog.DeferWrites();
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            TrackedResourceScopes trackedScopes = GetOrAddTrackedScopes(_handleScopes, resourceHandleAddress);
            if (!TryRetainScope(trackedScopes, handleScope, out ObjectResourceScope retainedScope))
            {
                RemoveAddressIfEmpty(_handleScopes, resourceHandleAddress, trackedScopes);
                return;
            }

            if (trackedScopes.RedirectedScopes.Add(retainedScope))
            {
                LogRegistration("registered", "handle", resourceHandleAddress, Guid.Empty, retainedScope);
                LogScopeConflict(resourceHandleAddress, trackedScopes);
            }
        }
    }

    public bool TryGetHandleScope(nint resourceHandleAddress, out ObjectResourceScope handleScope)
        => TryGetScope(_handleScopes, resourceHandleAddress, out handleScope, out _);

    public bool TryGetHandleScope(nint resourceHandleAddress, out ObjectResourceScope handleScope, out bool isTracked)
        => TryGetScope(_handleScopes, resourceHandleAddress, out handleScope, out isTracked);

    public bool TryGetInstanceScope(nint instanceAddress, out ObjectResourceScope instanceScope)
        => TryGetScope(_instanceScopes, instanceAddress, out instanceScope, out _);

    public bool TryGetInstanceScope(nint instanceAddress, out ObjectResourceScope instanceScope, out bool isTracked)
        => TryGetScope(_instanceScopes, instanceAddress, out instanceScope, out isTracked);

    public bool RemoveRootHandle(nint resourceHandleAddress, Guid ownerId)
        => RemoveRootScope(_handleScopes, resourceHandleAddress, ownerId);

    public bool RemoveRootInstance(nint instanceAddress, Guid ownerId)
        => RemoveRootScope(_instanceScopes, instanceAddress, ownerId);

    public bool RemoveTrackedHandle(nint resourceHandleAddress, string reason = "removed")
        => RemoveTrackedScope(_handleScopes, resourceHandleAddress, reason);

    public bool RemoveTrackedInstance(nint instanceAddress, string reason = "removed")
        => RemoveTrackedScope(_instanceScopes, instanceAddress, reason);

    public void Dispose()
    {
        using ObjectResourceLog.WriteScope logWrites = ObjectResourceLog.DeferWrites();
        lock (_lock)
        {
            if (!_disposed)
            {
                int handleCount = _handleScopes.Count;
                int instanceCount = _instanceScopes.Count;
                ObjectResourceLog.Write(() => _logger.LogDebug("object resource tracker disposed; handles={HandleCount}; instances={InstanceCount}",
                    handleCount, instanceCount));
            }

            _disposed = true;
            foreach (TrackedResourceScopes scopes in _handleScopes.Values.Concat(_instanceScopes.Values))
            {
                scopes.Dispose();
            }

            _handleScopes.Clear();
            _instanceScopes.Clear();
        }
    }

    private void RegisterOrUpdateRootScope(
        Dictionary<nint, TrackedResourceScopes> scopesByAddress,
        nint address,
        Guid ownerId,
        ObjectResourceScope scope)
    {
        ValidateRegistration(address, scope);
        if (ownerId == Guid.Empty)
        {
            throw new ArgumentException("root scope owner id must not be empty", nameof(ownerId));
        }

        using ObjectResourceLog.WriteScope logWrites = ObjectResourceLog.DeferWrites();
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            TrackedResourceScopes trackedScopes = GetOrAddTrackedScopes(scopesByAddress, address);
            string kind = ReferenceEquals(scopesByAddress, _handleScopes) ? "root handle" : "root instance";
            bool hadScope = trackedScopes.RootScopes.TryGetValue(ownerId, out ObjectResourceScope previousScope);
            bool changed;
            if (TryRetainScope(trackedScopes, scope, out ObjectResourceScope retainedScope))
            {
                trackedScopes.RootScopes[ownerId] = retainedScope;
                changed = !hadScope || previousScope != retainedScope;
                if (changed)
                {
                    if (hadScope)
                    {
                        LogRegistration("replaced", kind, address, ownerId, previousScope);
                    }

                    LogRegistration("registered", kind, address, ownerId, retainedScope);
                }
            }
            else
            {
                changed = trackedScopes.RootScopes.Remove(ownerId);
                ObjectResourceLog.Write(() => _logger.LogWarning("object resource registration rejected; reason=collection unavailable; address=0x{Address:X}; owner={OwnerId}; collection={CollectionId}; generation={ResourceScopeId}; path={Path}",
                    (ulong)address, ownerId, scope.ResourceCollectionId, scope.ResourceScopeId, scope.ResolvedPath));
                if (changed)
                {
                    LogRegistration("removed", kind, address, ownerId, previousScope);
                }
            }

            ReleaseUnusedScopes(trackedScopes);
            RemoveAddressIfEmpty(scopesByAddress, address, trackedScopes);
            if (changed)
            {
                LogScopeConflict(address, trackedScopes);
            }
        }
    }

    private bool TryRetainScope(TrackedResourceScopes trackedScopes, ObjectResourceScope scope, out ObjectResourceScope retainedScope)
    {
        retainedScope = default;
        long resourceScopeId = scope.ResourceScopeId;
        if (resourceScopeId == 0)
        {
            // root registration follows the generation already returned by the native loader
            resourceScopeId = trackedScopes.RedirectedScopes.FirstOrDefault(redirectedScope
                => string.Equals(redirectedScope.ResourceCollectionId, scope.ResourceCollectionId, StringComparison.Ordinal)
                && string.Equals(redirectedScope.ResolvedPath, scope.ResolvedPath, StringComparison.OrdinalIgnoreCase)).ResourceScopeId;
        }

        if (resourceScopeId == 0
         && _loadScope.TryReadActiveCollection(out ObjectCollectionResolveData activeCollection)
         && string.Equals(activeCollection.CollectionId, scope.ResourceCollectionId, StringComparison.Ordinal))
        {
            resourceScopeId = activeCollection.ResourceScopeId;
        }

        if (!trackedScopes.Leases.TryGetValue(resourceScopeId, out ObjectResourceCollectionLease? lease))
        {
            lease = resourceScopeId == 0
                ? _collectionStore.AcquireCollection(scope.ResourceCollectionId)
                : _collectionStore.AcquireResourceScope(resourceScopeId);
            if (lease is null)
            {
                return false;
            }

            resourceScopeId = lease.Snapshot.ResourceScopeId;
            if (!trackedScopes.Leases.TryAdd(resourceScopeId, lease))
            {
                lease.Dispose();
            }
        }

        retainedScope = new ObjectResourceScope(scope.ResourceCollectionId, scope.ResolvedPath, resourceScopeId);
        return true;
    }

    private static void ReleaseUnusedScopes(TrackedResourceScopes trackedScopes)
    {
        foreach (long scopeId in trackedScopes.Leases.Keys.ToArray())
        {
            if (trackedScopes.RootScopes.Values.Any(scope => scope.ResourceScopeId == scopeId)
             || trackedScopes.RedirectedScopes.Any(scope => scope.ResourceScopeId == scopeId))
            {
                continue;
            }

            trackedScopes.Leases.Remove(scopeId, out ObjectResourceCollectionLease? lease);
            lease!.Dispose();
        }
    }

    private bool TryGetScope(
        Dictionary<nint, TrackedResourceScopes> scopesByAddress,
        nint address,
        out ObjectResourceScope scope,
        out bool isTracked)
    {
        scope = default;
        lock (_lock)
        {
            isTracked = scopesByAddress.TryGetValue(address, out TrackedResourceScopes? trackedScopes);
            if (!isTracked)
            {
                return false;
            }

            return TryResolveSingleScope(trackedScopes!, out scope);
        }
    }

    private bool RemoveRootScope(Dictionary<nint, TrackedResourceScopes> scopesByAddress, nint address, Guid ownerId)
    {
        if (address == nint.Zero || ownerId == Guid.Empty)
        {
            return false;
        }

        using ObjectResourceLog.WriteScope logWrites = ObjectResourceLog.DeferWrites();
        lock (_lock)
        {
            if (!scopesByAddress.TryGetValue(address, out TrackedResourceScopes? trackedScopes))
            {
                return false;
            }

            bool removed = trackedScopes.RootScopes.Remove(ownerId, out ObjectResourceScope scope);
            if (removed)
            {
                LogRegistration("removed", ReferenceEquals(scopesByAddress, _handleScopes) ? "root handle" : "root instance", address, ownerId, scope);
            }

            ReleaseUnusedScopes(trackedScopes);
            RemoveAddressIfEmpty(scopesByAddress, address, trackedScopes);
            return removed;
        }
    }

    private bool RemoveTrackedScope(Dictionary<nint, TrackedResourceScopes> scopesByAddress, nint address, string reason)
    {
        if (address == nint.Zero)
        {
            return false;
        }

        using ObjectResourceLog.WriteScope logWrites = ObjectResourceLog.DeferWrites();
        lock (_lock)
        {
            if (!scopesByAddress.Remove(address, out TrackedResourceScopes? trackedScopes))
            {
                return false;
            }

            string kind = ReferenceEquals(scopesByAddress, _handleScopes) ? "handle" : "instance";
            int rootCount = trackedScopes.RootScopes.Count;
            int redirectCount = trackedScopes.RedirectedScopes.Count;
            string generations = string.Join(",", trackedScopes.Leases.Keys);
            ObjectResourceLog.Write(() => _logger.LogDebug("object resource tracking removed; reason={Reason}; kind={Kind}; address=0x{Address:X}; roots={RootCount}; redirects={RedirectCount}; generations={Generations}",
                reason, kind, (ulong)address, rootCount, redirectCount, generations));
            trackedScopes.Dispose();
            return true;
        }
    }

    private static void RemoveAddressIfEmpty(
        Dictionary<nint, TrackedResourceScopes> scopesByAddress,
        nint address,
        TrackedResourceScopes trackedScopes)
    {
        if (trackedScopes.RootScopes.Count == 0 && trackedScopes.RedirectedScopes.Count == 0)
        {
            scopesByAddress.Remove(address);
        }
    }

    private static TrackedResourceScopes GetOrAddTrackedScopes(
        Dictionary<nint, TrackedResourceScopes> scopesByAddress,
        nint address)
    {
        if (scopesByAddress.TryGetValue(address, out TrackedResourceScopes? trackedScopes))
        {
            return trackedScopes;
        }

        trackedScopes = new TrackedResourceScopes();
        scopesByAddress.Add(address, trackedScopes);
        return trackedScopes;
    }

    private static void ValidateRegistration(nint address, ObjectResourceScope scope)
    {
        if (address == nint.Zero)
        {
            throw new ArgumentException("resource scope address must not be zero", nameof(address));
        }

        if (scope.ResourceCollectionId.Length == 0 || scope.ResolvedPath.Length == 0)
        {
            throw new ArgumentException("tracked resource scope must include collection id and resolved path", nameof(scope));
        }
    }

    private static bool TryResolveSingleScope(
        TrackedResourceScopes trackedScopes,
        out ObjectResourceScope resolvedScope)
    {
        resolvedScope = default;
        bool hasScope = false;
        foreach (ObjectResourceScope scope in trackedScopes.RootScopes.Values)
        {
            if (!TryAcceptScope(scope, ref resolvedScope, ref hasScope))
            {
                return false;
            }
        }

        foreach (ObjectResourceScope scope in trackedScopes.RedirectedScopes)
        {
            if (!TryAcceptScope(scope, ref resolvedScope, ref hasScope))
            {
                return false;
            }
        }

        return hasScope;
    }

    private static bool TryAcceptScope(
        ObjectResourceScope scope,
        ref ObjectResourceScope currentScope,
        ref bool hasScope)
    {
        if (!hasScope)
        {
            currentScope = scope;
            hasScope = true;
            return true;
        }

        if (currentScope == scope)
        {
            return true;
        }

        currentScope = default;
        return false;
    }

    private void LogRegistration(string action, string kind, nint address, Guid ownerId, ObjectResourceScope scope)
        => ObjectResourceLog.Write(() => _logger.LogDebug("object resource registration {Action}; kind={Kind}; address=0x{Address:X}; owner={OwnerId}; collection={CollectionId}; generation={ResourceScopeId}; path={Path}",
            action, kind, (ulong)address, ownerId, scope.ResourceCollectionId, scope.ResourceScopeId, scope.ResolvedPath));

    private void LogScopeConflict(nint address, TrackedResourceScopes scopes)
    {
        if (!TryResolveSingleScope(scopes, out _) && scopes.RootScopes.Count + scopes.RedirectedScopes.Count > 0)
        {
            string roots = string.Join("; ", scopes.RootScopes);
            string redirects = string.Join("; ", scopes.RedirectedScopes);
            ObjectResourceLog.Write(() => _logger.LogWarning("object resource ownership conflict; address=0x{Address:X}; roots={RootScopes}; redirects={RedirectedScopes}",
                (ulong)address, roots, redirects));
        }
    }
}
