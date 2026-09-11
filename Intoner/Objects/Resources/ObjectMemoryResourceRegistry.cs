using Intoner.Objects.Assets;
using Intoner.Objects.Utils;
using Microsoft.Extensions.Logging;
using System.Collections.Immutable;

namespace Intoner.Objects.Resources;

internal readonly record struct ObjectMemoryResource(string MemoryPath, string GamePath, byte[] Data);

internal sealed class ObjectMemoryResourceRegistry
{
    private readonly ILogger<ObjectMemoryResourceRegistry> _logger;
    private readonly Lock _stateLock = new();

    private ImmutableDictionary<string, ObjectMemoryResource> _resourcesByPath
        = ImmutableDictionary.Create<string, ObjectMemoryResource>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> _pathsByOwner = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _ownerCountByPath = new(StringComparer.OrdinalIgnoreCase);
    private long _nextResourceId;

    public ObjectMemoryResourceRegistry(ILogger<ObjectMemoryResourceRegistry> logger)
    {
        _logger = logger;
    }

    public ObjectResolvedPath RegisterResource(string ownerId, string gamePath, byte[] data)
    {
        string normalizedOwnerId = TextUtility.TrimOrEmpty(ownerId);
        if (normalizedOwnerId.Length == 0)
        {
            throw new ArgumentException("memory resource owner id must not be empty", nameof(ownerId));
        }

        if (!ObjectAssetPathRules.TryNormalizeSupportedResourcePath(gamePath, out string normalizedGamePath))
        {
            throw new ArgumentException("memory resource game path must be a supported object resource path", nameof(gamePath));
        }

        if (data is not { Length: > 0 })
        {
            throw new ArgumentException("memory resource data must not be empty", nameof(data));
        }

        long resourceId = Interlocked.Increment(ref _nextResourceId);
        string memoryPath = ObjectMemoryResourcePathUtility.Create(resourceId, normalizedGamePath);
        ObjectMemoryResource resource = new(
            memoryPath,
            normalizedGamePath,
            data.ToArray());

        using ObjectResourceLog.WriteScope logWrites = ObjectResourceLog.DeferWrites();
        lock (_stateLock)
        {
            AddOwnerPath(normalizedOwnerId, memoryPath);
            Volatile.Write(ref _resourcesByPath, _resourcesByPath.SetItem(memoryPath, resource));
            int bytes = resource.Data.Length;
            ObjectResourceLog.Write(() => _logger.LogDebug("object memory resource registered; owner={OwnerId}; path={Path}; gamePath={GamePath}; bytes={Bytes}",
                normalizedOwnerId, memoryPath, normalizedGamePath, bytes));
        }

        return ObjectResolvedPath.FromMemory(memoryPath);
    }

    public void ReleaseOwner(string ownerId)
    {
        string normalizedOwnerId = TextUtility.TrimOrEmpty(ownerId);
        if (normalizedOwnerId.Length == 0)
        {
            return;
        }

        using ObjectResourceLog.WriteScope logWrites = ObjectResourceLog.DeferWrites();
        lock (_stateLock)
        {
            if (!_pathsByOwner.Remove(normalizedOwnerId, out HashSet<string>? ownerPaths))
            {
                return;
            }

            List<string> unreferencedPaths = [];
            foreach (string path in ownerPaths)
            {
                int remainingOwners = _ownerCountByPath[path] - 1;
                int bytes = _resourcesByPath[path].Data.Length;
                ObjectResourceLog.Write(() => _logger.LogDebug("object memory resource owner released; owner={OwnerId}; path={Path}; remainingOwners={RemainingOwners}; payloadReleased={PayloadReleased}; bytes={Bytes}",
                    normalizedOwnerId, path, remainingOwners, remainingOwners == 0, bytes));
                if (remainingOwners > 0)
                {
                    _ownerCountByPath[path] = remainingOwners;
                    continue;
                }

                _ownerCountByPath.Remove(path);
                unreferencedPaths.Add(path);
            }

            Volatile.Write(ref _resourcesByPath, _resourcesByPath.RemoveRange(unreferencedPaths));
        }
    }

    public bool TryAcquireResource(string ownerId, string memoryResourcePath, out ObjectMemoryResource resource)
    {
        resource = default;
        string normalizedOwnerId = TextUtility.TrimOrEmpty(ownerId);
        if (normalizedOwnerId.Length == 0
            || !ObjectMemoryResourcePathUtility.TryParse(memoryResourcePath, out ObjectMemoryResourcePath memoryPath))
        {
            return false;
        }

        using ObjectResourceLog.WriteScope logWrites = ObjectResourceLog.DeferWrites();
        lock (_stateLock)
        {
            if (!_resourcesByPath.TryGetValue(memoryPath.Path, out resource))
            {
                return false;
            }

            AddOwnerPath(normalizedOwnerId, memoryPath.Path);
            return true;
        }
    }

    public bool TryGetResource(string memoryResourcePath, out ObjectMemoryResource resource)
    {
        resource = default;
        return ObjectMemoryResourcePathUtility.TryParse(memoryResourcePath, out ObjectMemoryResourcePath memoryPath)
            && Volatile.Read(ref _resourcesByPath).TryGetValue(memoryPath.Path, out resource);
    }

    public void Clear()
    {
        using ObjectResourceLog.WriteScope logWrites = ObjectResourceLog.DeferWrites();
        lock (_stateLock)
        {
            foreach (ObjectMemoryResource resource in _resourcesByPath.Values)
            {
                string path = resource.MemoryPath;
                int bytes = resource.Data.Length;
                ObjectResourceLog.Write(() => _logger.LogDebug("object memory resource released; reason=registry cleared; path={Path}; bytes={Bytes}",
                    path, bytes));
            }

            _pathsByOwner.Clear();
            _ownerCountByPath.Clear();
            Volatile.Write(
                ref _resourcesByPath,
                ImmutableDictionary.Create<string, ObjectMemoryResource>(StringComparer.OrdinalIgnoreCase));
        }
    }

    private void AddOwnerPath(string ownerId, string memoryPath)
    {
        if (!_pathsByOwner.TryGetValue(ownerId, out HashSet<string>? ownerPaths))
        {
            ownerPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _pathsByOwner.Add(ownerId, ownerPaths);
        }

        if (ownerPaths.Add(memoryPath))
        {
            int owners = _ownerCountByPath.GetValueOrDefault(memoryPath) + 1;
            _ownerCountByPath[memoryPath] = owners;
            ObjectResourceLog.Write(() => _logger.LogDebug("object memory resource owner retained; owner={OwnerId}; path={Path}; owners={OwnerCount}",
                ownerId, memoryPath, owners));
        }
    }
}

