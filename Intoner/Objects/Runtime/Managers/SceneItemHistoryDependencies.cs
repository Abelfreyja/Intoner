using Intoner.Objects.Models;

namespace Intoner.Scene;

/// <summary> captures unchanged parents and group membership required by a history action </summary>
internal sealed class SceneItemHistoryDependencies
{
    private readonly IReadOnlyList<SceneItemSnapshot> _snapshots;
    private readonly IReadOnlyDictionary<Guid, HashSet<Guid>> _members;
    public static SceneItemHistoryDependencies Empty { get; } = new([], new Dictionary<Guid, HashSet<Guid>>());

    private SceneItemHistoryDependencies(IReadOnlyList<SceneItemSnapshot> snapshots, IReadOnlyDictionary<Guid, HashSet<Guid>> members)
    {
        _snapshots = snapshots;
        _members = members;
    }

    public SceneHistoryResult Check(IReadOnlyDictionary<Guid, SceneItemSnapshot> current)
    {
        SceneItemSnapshot? changed = _snapshots.FirstOrDefault(snapshot => current.GetValueOrDefault(snapshot.Id) != snapshot);

        if (changed is not null)
        {
            return SceneHistoryResult.Conflict($"{SceneHistoryDescription.GetItemName(changed)} changed externally and is required by this action.");
        }

        if (_members.Count == 0)
        {
            return SceneHistoryResult.Success;
        }

        Dictionary<Guid, int> remaining = _members.ToDictionary(static group => group.Key, static group => group.Value.Count);
        foreach (SceneItemSnapshot item in current.Values)
        {
            if (item is not ObjectSnapshot { GroupId: Guid groupId } || !_members.TryGetValue(groupId, out HashSet<Guid>? expected))
            {
                continue;
            }

            if (!expected.Contains(item.Id))
            {
                return SceneHistoryResult.Conflict("Group membership changed externally.");
            }

            --remaining[groupId];
        }

        if (remaining.Values.Any(static count => count != 0))
        {
            return SceneHistoryResult.Conflict("Group membership changed externally.");
        }

        return SceneHistoryResult.Success;
    }

    public static (SceneItemHistoryDependencies Before, SceneItemHistoryDependencies After) Capture(
        IReadOnlyList<SceneItemSnapshot> stored, IReadOnlyList<SceneItemSnapshotChange> changes,
        IReadOnlyList<SceneItemSnapshot> unchanged)
    {
        Dictionary<Guid, SceneItemSnapshot> snapshots = stored.ToDictionary(static item => item.Id);
        HashSet<Guid> changed = [];
        HashSet<Guid> groups = [];
        HashSet<Guid> parents = [];
        foreach (SceneItemSnapshotChange change in changes)
        {
            SceneItemSnapshot item = (change.After ?? change.Before)!;
            changed.Add(item.Id);
            AddDependencies(change.Before);
            AddDependencies(change.After);

            if (change.After is { } next)
            {
                snapshots[item.Id] = next;
            }
            else
            {
                snapshots.Remove(item.Id);
            }
        }

        SceneItemHistoryDependencies after = Build(snapshots);
        foreach (SceneItemSnapshotChange change in changes)
        {
            if (change.Before is { } previous)
            {
                snapshots[previous.Id] = previous;
            }
            else
            {
                snapshots.Remove(change.After!.Id);
            }
        }

        return (Build(snapshots), after);

        void AddDependencies(SceneItemSnapshot? item)
        {
            if (item is SceneGroupSnapshot group)
            {
                groups.Add(group.Id);
            }
            else if (item is ObjectSnapshot obj)
            {
                if (obj.GroupId is Guid groupId)
                {
                    groups.Add(groupId);
                }

                if (obj.Model is FurnitureModel { AttachmentParentId: Guid parent })
                {
                    parents.Add(parent);
                }
            }
        }

        SceneItemHistoryDependencies Build(Dictionary<Guid, SceneItemSnapshot> state)
        {
            Dictionary<Guid, SceneItemSnapshot> required = unchanged.ToDictionary(static item => item.Id);
            Dictionary<Guid, HashSet<Guid>> memberships = groups.ToDictionary(static id => id, static _ => new HashSet<Guid>());
            foreach (Guid id in parents.Concat(groups))
            {
                if (!changed.Contains(id) && state.TryGetValue(id, out SceneItemSnapshot? dependency))
                {
                    required[id] = dependency;
                }
            }

            if (memberships.Count > 0)
            {
                foreach (SceneItemSnapshot item in state.Values)
                {
                    if (item is not ObjectSnapshot { GroupId: Guid groupId } || !memberships.TryGetValue(groupId, out HashSet<Guid>? members))
                    {
                        continue;
                    }

                    members.Add(item.Id);

                    if (!changed.Contains(item.Id))
                    {
                        required[item.Id] = item;
                    }
                }
            }

            return new SceneItemHistoryDependencies(required.Values.ToArray(), memberships);
        }
    }
}
