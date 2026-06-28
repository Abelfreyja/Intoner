using Intoner.Objects.Utils;
using Microsoft.Extensions.Logging;

namespace Intoner.Objects.Resources;

/// <summary> one tracked object resource handle scope </summary>
internal readonly record struct ObjectResourceScope
{
    internal ObjectResourceScope(string resourceCollectionId, string resolvedPath)
    {
        ResourceCollectionId = ObjectCollectionKeyUtility.NormalizeCollectionId(resourceCollectionId);
        ResolvedPath = ObjectResourcePathUtility.NormalizeTrackedPath(resolvedPath);
    }

    public string ResourceCollectionId { get; } = string.Empty;
    public string ResolvedPath { get; } = string.Empty;
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

    public ObjectResourceRegistration(Guid ownerId)
    {
        _address = nint.Zero;
        _ownerId = ownerId;
        _scope = default;
        _kind = default;
    }

    public bool IsRegistered
        => _address != nint.Zero;

    public void UpdateRootHandle(IObjectResourceTracker tracker, nint handle, ObjectResourceScope scope)
        => Update(tracker, ObjectResourceRegistrationKind.RootHandle, handle, scope);

    public void UpdateRootInstance(IObjectResourceTracker tracker, nint instance, ObjectResourceScope scope)
        => Update(tracker, ObjectResourceRegistrationKind.RootInstance, instance, scope);

    public void Clear(IObjectResourceTracker tracker)
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
        IObjectResourceTracker tracker,
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

/// <summary>
/// Stores object owned root and redirected resource handles so dependent loads can recover collection context later.
/// </summary>
internal interface IObjectResourceTracker
{
    /// <summary>
    /// Registers or replaces one root resource handle scope.
    /// </summary>
    /// <param name="resourceHandleAddress">the native root resource handle address</param>
    /// <param name="ownerId">the local scene object registration owner</param>
    /// <param name="handleScope">the tracked collection scope for that handle</param>
    void RegisterOrUpdateRootHandle(nint resourceHandleAddress, Guid ownerId, ObjectResourceScope handleScope);

    /// <summary>
    /// Registers or replaces one root sgb instance scope.
    /// </summary>
    /// <param name="instanceAddress">the native sgb instance address</param>
    /// <param name="ownerId">the local scene object registration owner</param>
    /// <param name="instanceScope">the tracked collection scope for that instance</param>
    void RegisterOrUpdateRootInstance(nint instanceAddress, Guid ownerId, ObjectResourceScope instanceScope);

    /// <summary>
    /// Registers or replaces one redirected resource handle scope.
    /// </summary>
    /// <param name="resourceHandleAddress">the native redirected resource handle address</param>
    /// <param name="handleScope">the tracked collection scope for that handle</param>
    void RegisterOrUpdateHandleScope(nint resourceHandleAddress, ObjectResourceScope handleScope);

    /// <summary>
    /// Tries to resolve one tracked resource handle scope.
    /// </summary>
    /// <param name="resourceHandleAddress">the native tracked resource handle address</param>
    /// <param name="handleScope">the tracked scope metadata when found</param>
    /// <returns>true when the handle scope is currently registered</returns>
    bool TryGetHandleScope(nint resourceHandleAddress, out ObjectResourceScope handleScope);

    /// <summary>
    /// Tries to resolve one tracked sgb instance scope.
    /// </summary>
    /// <param name="instanceAddress">the native sgb instance address</param>
    /// <param name="instanceScope">the tracked scope metadata when found</param>
    /// <returns>true when the instance scope is currently registered</returns>
    bool TryGetInstanceScope(nint instanceAddress, out ObjectResourceScope instanceScope);

    /// <summary>
    /// Removes one registered root resource handle scope.
    /// </summary>
    /// <param name="resourceHandleAddress">the native root resource handle address</param>
    /// <param name="ownerId">the local scene object registration owner</param>
    /// <returns>true when an entry was removed</returns>
    bool RemoveRootHandle(nint resourceHandleAddress, Guid ownerId);

    /// <summary>
    /// Removes one registered root sgb instance scope.
    /// </summary>
    /// <param name="instanceAddress">the native root sgb instance address</param>
    /// <param name="ownerId">the local scene object registration owner</param>
    /// <returns>true when an entry was removed</returns>
    bool RemoveRootInstance(nint instanceAddress, Guid ownerId);

    /// <summary>
    /// Removes one tracked resource handle scope.
    /// </summary>
    /// <param name="resourceHandleAddress">the native tracked resource handle address</param>
    /// <returns>true when a tracked entry was removed</returns>
    bool RemoveTrackedHandle(nint resourceHandleAddress);

    /// <summary>
    /// Removes one tracked sgb instance scope.
    /// </summary>
    /// <param name="instanceAddress">the native tracked sgb instance address</param>
    /// <returns>true when a tracked entry was removed</returns>
    bool RemoveTrackedInstance(nint instanceAddress);
}

internal sealed class ObjectResourceTracker : IObjectResourceTracker
{
    private sealed class TrackedResourceScopes
    {
        public readonly Dictionary<Guid, ObjectResourceScope> RootScopes = [];
        public readonly HashSet<ObjectResourceScope> RedirectedScopes = [];
    }

    private readonly ILogger<ObjectResourceTracker> _logger;
    private readonly Lock _lock = new();
    private readonly Dictionary<nint, TrackedResourceScopes> _handleScopes = [];
    private readonly Dictionary<nint, TrackedResourceScopes> _instanceScopes = [];

    public ObjectResourceTracker(ILogger<ObjectResourceTracker> logger)
    {
        _logger = logger;
    }

    public void RegisterOrUpdateRootHandle(nint resourceHandleAddress, Guid ownerId, ObjectResourceScope handleScope)
        => RegisterOrUpdateRootScope(_handleScopes, resourceHandleAddress, ownerId, handleScope);

    public void RegisterOrUpdateRootInstance(nint instanceAddress, Guid ownerId, ObjectResourceScope instanceScope)
        => RegisterOrUpdateRootScope(_instanceScopes, instanceAddress, ownerId, instanceScope);

    public void RegisterOrUpdateHandleScope(nint resourceHandleAddress, ObjectResourceScope handleScope)
    {
        ValidateRegistration(resourceHandleAddress, handleScope);

        lock (_lock)
        {
            TrackedResourceScopes trackedScopes = GetOrAddTrackedScopes(_handleScopes, resourceHandleAddress);
            trackedScopes.RedirectedScopes.Add(handleScope);
            LogConflictingScopes(resourceHandleAddress, trackedScopes);
        }
    }

    public bool TryGetHandleScope(nint resourceHandleAddress, out ObjectResourceScope handleScope)
        => TryGetScope(_handleScopes, resourceHandleAddress, out handleScope);

    public bool TryGetInstanceScope(nint instanceAddress, out ObjectResourceScope instanceScope)
        => TryGetScope(_instanceScopes, instanceAddress, out instanceScope);

    public bool RemoveRootHandle(nint resourceHandleAddress, Guid ownerId)
        => RemoveRootScope(_handleScopes, resourceHandleAddress, ownerId);

    public bool RemoveRootInstance(nint instanceAddress, Guid ownerId)
        => RemoveRootScope(_instanceScopes, instanceAddress, ownerId);

    public bool RemoveTrackedHandle(nint resourceHandleAddress)
        => RemoveTrackedScope(_handleScopes, resourceHandleAddress);

    public bool RemoveTrackedInstance(nint instanceAddress)
        => RemoveTrackedScope(_instanceScopes, instanceAddress);

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

        lock (_lock)
        {
            TrackedResourceScopes trackedScopes = GetOrAddTrackedScopes(scopesByAddress, address);
            trackedScopes.RootScopes[ownerId] = scope;
            LogConflictingScopes(address, trackedScopes);
        }
    }

    private bool TryGetScope(
        Dictionary<nint, TrackedResourceScopes> scopesByAddress,
        nint address,
        out ObjectResourceScope scope)
    {
        scope = default;
        lock (_lock)
        {
            if (!scopesByAddress.TryGetValue(address, out TrackedResourceScopes? trackedScopes))
            {
                return false;
            }

            return TryResolveSingleScope(address, trackedScopes, out scope);
        }
    }

    private bool RemoveRootScope(Dictionary<nint, TrackedResourceScopes> scopesByAddress, nint address, Guid ownerId)
    {
        if (address == nint.Zero || ownerId == Guid.Empty)
        {
            return false;
        }

        lock (_lock)
        {
            if (!scopesByAddress.TryGetValue(address, out TrackedResourceScopes? trackedScopes))
            {
                return false;
            }

            bool removed = trackedScopes.RootScopes.Remove(ownerId);
            RemoveAddressIfEmpty(scopesByAddress, address, trackedScopes);
            return removed;
        }
    }

    private bool RemoveTrackedScope(Dictionary<nint, TrackedResourceScopes> scopesByAddress, nint address)
    {
        if (address == nint.Zero)
        {
            return false;
        }

        lock (_lock)
        {
            if (!scopesByAddress.TryGetValue(address, out TrackedResourceScopes? trackedScopes))
            {
                return false;
            }

            bool removed = trackedScopes.RedirectedScopes.Count > 0;
            trackedScopes.RedirectedScopes.Clear();
            RemoveAddressIfEmpty(scopesByAddress, address, trackedScopes);
            return removed;
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

    private bool TryResolveSingleScope(
        nint address,
        TrackedResourceScopes trackedScopes,
        out ObjectResourceScope resolvedScope)
    {
        resolvedScope = default;
        bool hasScope = false;
        foreach (ObjectResourceScope scope in trackedScopes.RootScopes.Values)
        {
            if (!TryAcceptScope(address, scope, ref resolvedScope, ref hasScope))
            {
                return false;
            }
        }

        foreach (ObjectResourceScope scope in trackedScopes.RedirectedScopes)
        {
            if (!TryAcceptScope(address, scope, ref resolvedScope, ref hasScope))
            {
                return false;
            }
        }

        return hasScope;
    }

    private bool TryAcceptScope(
        nint address,
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

        _logger.LogDebug(
            "skipping object resource scope for shared address 0x{Address:X} because it has conflicting collection scopes",
            (ulong)address);
        currentScope = default;
        return false;
    }

    private void LogConflictingScopes(nint address, TrackedResourceScopes trackedScopes)
    {
        if (!HasConflictingScopes(trackedScopes))
        {
            return;
        }

        _logger.LogDebug(
            "tracked object resource address 0x{Address:X} has multiple collection scopes and dependent loads will not guess a collection",
            (ulong)address);
    }

    private static bool HasConflictingScopes(TrackedResourceScopes trackedScopes)
    {
        bool hasScope = false;
        ObjectResourceScope currentScope = default;
        foreach (ObjectResourceScope scope in trackedScopes.RootScopes.Values)
        {
            if (!TryMatchScope(scope, ref currentScope, ref hasScope))
            {
                return true;
            }
        }

        foreach (ObjectResourceScope scope in trackedScopes.RedirectedScopes)
        {
            if (!TryMatchScope(scope, ref currentScope, ref hasScope))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryMatchScope(ObjectResourceScope scope, ref ObjectResourceScope currentScope, ref bool hasScope)
    {
        if (!hasScope)
        {
            currentScope = scope;
            hasScope = true;
            return true;
        }

        return currentScope == scope;
    }
}


