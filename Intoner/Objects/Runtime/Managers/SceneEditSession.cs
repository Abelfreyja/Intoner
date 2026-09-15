using Microsoft.Extensions.Logging;

namespace Intoner.Scene
{
    /// <summary> owns reversible runtime previews and commits through the ordinary scene transaction </summary>
    internal sealed class SceneEditSession : IDisposable
    {
        private readonly ILogger _logger;
        private readonly Lock _stateLock;
        private readonly SceneItemMutationCoordinator _mutations;
        private readonly SceneItemOwnership[] _items;
        private readonly SceneItemSnapshot[] _current;
        private readonly Dictionary<ISceneItemDomain, long> _revisions = new(ReferenceEqualityComparer.Instance);
        private bool _completed;
        private bool _recoveryRequired;

        public SceneEditSession(ILogger logger, Lock stateLock, SceneItemMutationCoordinator mutations, SceneItemOwnership[] items)
        {
            _logger = logger;
            _stateLock = stateLock;
            _mutations = mutations;
            _items = items;
            _current = new SceneItemSnapshot[items.Length];
            for (int index = 0; index < items.Length; ++index)
            {
                _current[index] = items[index].Snapshot;
                _revisions[items[index].Domain] = items[index].Domain.Revisions.Persistent;
            }
        }

        public SceneMutationStatus Update(IReadOnlyList<SceneItemSnapshot> snapshots, out IReadOnlyList<SceneItemSnapshot> applied)
        {
            lock (_stateLock)
            {
                applied = [];
                if (_completed || snapshots.Count != _items.Length)
                {
                    return SceneMutationStatus.Rejected;
                }

                if (!IsCurrent())
                {
                    return CancelCore() ? SceneMutationStatus.Rejected : SceneMutationStatus.RecoveryRequired;
                }

                for (int index = 0; index < snapshots.Count; ++index)
                {
                    if (snapshots[index].Id != _items[index].Snapshot.Id)
                    {
                        return SceneMutationStatus.Rejected;
                    }
                }

                SceneItemSnapshot[] previous = [.. _current];
                for (int index = 0; index < snapshots.Count; ++index)
                {
                    SceneMutationStatus status = Preview(index, snapshots[index]);
                    if (status.IsFullyApplied())
                    {
                        continue;
                    }

                    for (int undo = index - 1; undo >= 0; --undo)
                    {
                        if (!Preview(undo, previous[undo]).IsFullyApplied())
                        {
                            _recoveryRequired = true;
                        }
                    }

                    if (_recoveryRequired)
                    {
                        _ = CancelCore();
                        return SceneMutationStatus.RecoveryRequired;
                    }

                    return SceneMutationStatus.Rejected;
                }

                applied = [.. _current];
                return SceneMutationStatus.Applied;
            }
        }

        public SceneMutationStatus Commit()
        {
            lock (_stateLock)
            {
                if (_completed)
                {
                    return SceneMutationStatus.Rejected;
                }

                bool current = IsCurrent();
                SceneItemSnapshotChange[] changes = new SceneItemSnapshotChange[_items.Length];
                for (int index = 0; index < changes.Length; ++index)
                {
                    changes[index] = new SceneItemSnapshotChange(_items[index].Snapshot, _current[index]);
                }

                if (!CancelCore())
                {
                    return SceneMutationStatus.RecoveryRequired;
                }

                return current ? _mutations.ApplyChanges(changes) : SceneMutationStatus.Rejected;
            }
        }

        public SceneMutationStatus Cancel()
        {
            lock (_stateLock)
            {
                return CancelCore() ? SceneMutationStatus.Applied : SceneMutationStatus.RecoveryRequired;
            }
        }

        public void Dispose()
            => _ = Cancel();

        private bool IsCurrent()
            => _revisions.All(static pair => pair.Key.Revisions.Persistent == pair.Value);

        private bool CancelCore()
        {
            if (_completed)
            {
                return !_recoveryRequired;
            }

            _completed = true;
            Dictionary<Guid, SceneItemSnapshot>? persisted = null;
            if (!IsCurrent())
            {
                persisted = [];
                foreach (ISceneItemDomain domain in _revisions.Keys)
                {
                    foreach (SceneItemSnapshot snapshot in domain.GetPlacedItems())
                    {
                        persisted[snapshot.Id] = snapshot;
                    }
                }
            }

            for (int index = _items.Length - 1; index >= 0; --index)
            {
                SceneItemSnapshot original = _items[index].Snapshot;
                if (Equals(original, _current[index]))
                {
                    continue;
                }

                if (persisted is not null && (!persisted.TryGetValue(original.Id, out SceneItemSnapshot? saved) || !Equals(saved, original)))
                {
                    _items[index].Domain.RequestPreviewRecovery();
                    continue;
                }

                _ = Preview(index, original, restoring: true);
            }

            if (_recoveryRequired)
            {
                _logger.LogError("scene edit ended after a preview recovery failure");
            }

            return !_recoveryRequired;
        }

        private SceneMutationStatus Preview(int index, SceneItemSnapshot after, bool restoring = false)
        {
            ISceneItemDomain domain = _items[index].Domain;
            SceneMutationStatus status;
            try
            {
                status = domain.PreviewChange(_current[index], after, out SceneItemSnapshot applied);
                if (status.IsFullyApplied())
                {
                    _current[index] = applied;
                }
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "scene edit preview failed for item {ItemId}", after.Id);
                status = SceneMutationStatus.RecoveryRequired;
            }

            if (status == SceneMutationStatus.RecoveryRequired || (restoring && !status.IsFullyApplied()))
            {
                domain.RequestPreviewRecovery();
                _recoveryRequired = true;
            }

            return status;
        }
    }
}
