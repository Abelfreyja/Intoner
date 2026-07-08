using Intoner.Objects.Assets;
using Intoner.Objects.Utils;
using System.Collections.Immutable;
using System.Threading;

namespace Intoner.Objects.Resources;

internal readonly record struct ObjectMemoryResource(string OwnerId, string MemoryPath, string GamePath, byte[] Data);

internal sealed class ObjectMemoryResourceRegistry
{
    private readonly Lock _stateLock = new();

    private ImmutableDictionary<string, ObjectMemoryResource> _resourcesByPath
        = ImmutableDictionary.Create<string, ObjectMemoryResource>(StringComparer.OrdinalIgnoreCase);
    private long _nextResourceId;

    public ObjectResolvedPath RegisterResource(string ownerId, string gamePath, byte[] data)
    {
        string normalizedOwnerId = ObjectStringUtility.TrimOrEmpty(ownerId);
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
            normalizedOwnerId,
            memoryPath,
            normalizedGamePath,
            data.ToArray());

        lock (_stateLock)
        {
            Volatile.Write(ref _resourcesByPath, _resourcesByPath.SetItem(memoryPath, resource));
        }

        return ObjectResolvedPath.FromMemory(memoryPath);
    }

    public void ReleaseOwner(string ownerId)
    {
        string normalizedOwnerId = ObjectStringUtility.TrimOrEmpty(ownerId);
        if (normalizedOwnerId.Length == 0)
        {
            return;
        }

        RemoveOwnerPaths(normalizedOwnerId, retainedPaths: null);
    }

    public void RetainOwnerResources(string ownerId, IReadOnlySet<string> activeMemoryPaths)
    {
        string normalizedOwnerId = ObjectStringUtility.TrimOrEmpty(ownerId);
        if (normalizedOwnerId.Length == 0)
        {
            return;
        }

        HashSet<string> normalizedActivePaths = new(StringComparer.OrdinalIgnoreCase);
        foreach (string path in activeMemoryPaths)
        {
            if (ObjectMemoryResourcePathUtility.TryParse(path, out ObjectMemoryResourcePath memoryPath))
            {
                normalizedActivePaths.Add(memoryPath.Path);
            }
        }

        RemoveOwnerPaths(normalizedOwnerId, normalizedActivePaths);
    }

    public bool TryGetResource(string memoryResourcePath, out ObjectMemoryResource resource)
    {
        resource = default;
        return ObjectMemoryResourcePathUtility.TryParse(memoryResourcePath, out ObjectMemoryResourcePath memoryPath)
            && Volatile.Read(ref _resourcesByPath).TryGetValue(memoryPath.Path, out resource);
    }

    public void Clear()
    {
        Volatile.Write(
            ref _resourcesByPath,
            ImmutableDictionary.Create<string, ObjectMemoryResource>(StringComparer.OrdinalIgnoreCase));
    }

    private void RemoveOwnerPaths(string ownerId, IReadOnlySet<string>? retainedPaths)
    {
        lock (_stateLock)
        {
            string[] removedPaths = _resourcesByPath
                .Where(pair => string.Equals(pair.Value.OwnerId, ownerId, StringComparison.OrdinalIgnoreCase)
                    && (retainedPaths is null || !retainedPaths.Contains(pair.Key)))
                .Select(static pair => pair.Key)
                .ToArray();
            Volatile.Write(ref _resourcesByPath, _resourcesByPath.RemoveRange(removedPaths));
        }
    }
}

