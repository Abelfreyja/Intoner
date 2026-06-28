using Intoner.Objects.Utils;
using System.Collections.Immutable;

namespace Intoner.Objects.Resources;

internal enum ObjectLocalFileKind
{
    DirectRead,
    Model,
    Texture,
    Sound,
}

internal sealed class ObjectLocalFileTracker : IDisposable
{
    private readonly record struct CollectionLocalFileSnapshot(
        long Revision,
        IReadOnlySet<string> Paths);

    private readonly IObjectResolvedCollectionStore _collectionStore;
    private readonly Func<string, string?> _tryNormalizeLocalFilePath;
    private readonly Lock _stateLock = new();
    private readonly Dictionary<string, CollectionLocalFileSnapshot> _localFilesByCollection = new(StringComparer.OrdinalIgnoreCase);
    private readonly ObjectDisposalState _disposeState = new();
    private ImmutableHashSet<string> _activePaths = ImmutableHashSet.Create<string>(StringComparer.OrdinalIgnoreCase);

    public ObjectLocalFileTracker(
        IObjectResolvedCollectionStore collectionStore,
        Func<string, string?> tryNormalizeLocalFilePath)
    {
        _collectionStore = collectionStore;
        _tryNormalizeLocalFilePath = tryNormalizeLocalFilePath;

        _collectionStore.CollectionChanged += HandleCollectionChanged;
        Refresh();
    }

    public bool ContainsLocalFilePath(string normalizedLocalFilePath)
    {
        string? localFilePath = _tryNormalizeLocalFilePath(normalizedLocalFilePath);
        return localFilePath is not null
            && Volatile.Read(ref _activePaths).Contains(localFilePath);
    }

    public void Refresh()
    {
        if (_disposeState.IsDisposing)
        {
            return;
        }

        lock (_stateLock)
        {
            if (_disposeState.IsDisposing)
            {
                return;
            }

            _localFilesByCollection.Clear();
            foreach (ObjectCollectionResolveData collection in _collectionStore.GetCollections())
            {
                SetCollectionLocked(collection, BuildCollectionLocalFiles(collection));
            }

            PublishLocked();
        }
    }

    public void Dispose()
    {
        if (!_disposeState.TryBeginDispose())
        {
            return;
        }

        _collectionStore.CollectionChanged -= HandleCollectionChanged;
        Clear();
    }

    private void HandleCollectionChanged(ObjectResolvedCollectionChangedInfo info)
    {
        if (_disposeState.IsDisposing)
        {
            return;
        }

        if (info.Removed)
        {
            if (_collectionStore.TryGetCollection(info.CollectionId, out ObjectCollectionResolveData currentSnapshot)
             && currentSnapshot.Revision > info.Revision)
            {
                Update(currentSnapshot);
                return;
            }

            Remove(info.CollectionId, info.Revision);
            return;
        }

        if (!_collectionStore.TryGetCollection(info.CollectionId, out ObjectCollectionResolveData snapshot))
        {
            Remove(info.CollectionId, info.Revision);
            return;
        }

        Update(snapshot);
    }

    private void Update(ObjectCollectionResolveData collection)
    {
        if (!IsCurrentCollection(collection))
        {
            return;
        }

        CollectionLocalFileSnapshot collectionLocalFiles = BuildCollectionLocalFiles(collection);
        lock (_stateLock)
        {
            if (_disposeState.IsDisposing || !IsCurrentCollection(collection))
            {
                return;
            }

            SetCollectionLocked(collection, collectionLocalFiles);
            PublishLocked();
        }
    }

    private void Remove(string collectionId, long revision)
    {
        string normalizedCollectionId = ObjectCollectionKeyUtility.NormalizeCollectionId(collectionId);
        if (normalizedCollectionId.Length == 0)
        {
            return;
        }

        lock (_stateLock)
        {
            if (_disposeState.IsDisposing)
            {
                return;
            }

            if (!_localFilesByCollection.TryGetValue(normalizedCollectionId, out CollectionLocalFileSnapshot snapshot)
             || (revision > 0 && snapshot.Revision > revision))
            {
                return;
            }

            _localFilesByCollection.Remove(normalizedCollectionId);
            PublishLocked();
        }
    }

    private void Clear()
    {
        lock (_stateLock)
        {
            _localFilesByCollection.Clear();
            Volatile.Write(ref _activePaths, ImmutableHashSet.Create<string>(StringComparer.OrdinalIgnoreCase));
        }
    }

    private void PublishLocked()
    {
        ImmutableHashSet<string>.Builder paths = ImmutableHashSet.CreateBuilder<string>(StringComparer.OrdinalIgnoreCase);
        foreach (CollectionLocalFileSnapshot snapshot in _localFilesByCollection.Values)
        {
            paths.UnionWith(snapshot.Paths);
        }

        Volatile.Write(ref _activePaths, paths.ToImmutable());
    }

    private void SetCollectionLocked(ObjectCollectionResolveData collection, CollectionLocalFileSnapshot collectionLocalFiles)
    {
        if (_localFilesByCollection.TryGetValue(collection.CollectionId, out CollectionLocalFileSnapshot existing)
         && existing.Revision > collection.Revision)
        {
            return;
        }

        _localFilesByCollection[collection.CollectionId] = collectionLocalFiles;
    }

    private bool IsCurrentCollection(ObjectCollectionResolveData collection)
        => _collectionStore.TryGetCollection(collection.CollectionId, out ObjectCollectionResolveData snapshot)
            && snapshot.Revision == collection.Revision;

    private CollectionLocalFileSnapshot BuildCollectionLocalFiles(ObjectCollectionResolveData collection)
    {
        HashSet<string> collectionPaths = new(StringComparer.OrdinalIgnoreCase);
        foreach (ObjectResolvedPath resolvedPath in collection.Redirects.Values)
        {
            if (!resolvedPath.IsLocalFile)
            {
                continue;
            }

            string? localFilePath = _tryNormalizeLocalFilePath(resolvedPath.Path);
            if (localFilePath is null)
            {
                continue;
            }

            collectionPaths.Add(localFilePath);
        }

        return new CollectionLocalFileSnapshot(collection.Revision, collectionPaths);
    }
}


