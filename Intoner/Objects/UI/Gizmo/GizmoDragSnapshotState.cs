using Intoner.Scene;
using System.Numerics;

namespace Intoner.Objects.UI;

/// <summary> tracks starting and applied snapshots for one gizmo drag </summary>
internal sealed class GizmoDragSnapshotState
{
    private readonly Dictionary<Guid, SceneItemSnapshot> _startSnapshots = [];
    private readonly Dictionary<Guid, SceneItemSnapshot> _appliedSnapshots = [];
    private GizmoSelectionEntry[] _selectionEntries = [];

    public IReadOnlyList<GizmoSelectionEntry> SelectionEntries
        => _selectionEntries;

    public SceneItemSnapshot PrimarySnapshot { get; private set; } = null!;

    public void Begin(GizmoSelectionEntry[] selectionEntries, SceneItemSnapshot primarySnapshot)
    {
        Reset();
        _selectionEntries = selectionEntries;
        PrimarySnapshot = primarySnapshot;
        _startSnapshots.EnsureCapacity(selectionEntries.Length);
        _appliedSnapshots.EnsureCapacity(selectionEntries.Length);
        for (int index = 0; index < selectionEntries.Length; ++index)
        {
            GizmoSelectionEntry entry = selectionEntries[index];
            _startSnapshots[entry.Snapshot.Id] = entry.Snapshot;
        }
    }

    public void Record(SceneItemSnapshot appliedSnapshot)
    {
        _appliedSnapshots.Clear();
        _appliedSnapshots[appliedSnapshot.Id] = appliedSnapshot;
    }

    public void Record(IReadOnlyList<SceneItemSnapshot> appliedSnapshots)
    {
        _appliedSnapshots.Clear();
        foreach (SceneItemSnapshot snapshot in appliedSnapshots)
        {
            _appliedSnapshots[snapshot.Id] = snapshot;
        }
    }

    public bool TryGetHistorySnapshots(
        out SceneItemSnapshot[] beforeSnapshots,
        out SceneItemSnapshot[] afterSnapshots)
    {
        if (_selectionEntries.Length == 0 || _appliedSnapshots.Count == 0)
        {
            beforeSnapshots = [];
            afterSnapshots = [];
            return false;
        }

        var before = new List<SceneItemSnapshot>(Math.Min(_selectionEntries.Length, _appliedSnapshots.Count));
        var after = new List<SceneItemSnapshot>(before.Capacity);
        for (int index = 0; index < _selectionEntries.Length; ++index)
        {
            GizmoSelectionEntry entry = _selectionEntries[index];
            if (_appliedSnapshots.TryGetValue(entry.Snapshot.Id, out SceneItemSnapshot? appliedSnapshot))
            {
                before.Add(entry.Snapshot);
                after.Add(appliedSnapshot);
            }
        }

        beforeSnapshots = before.ToArray();
        afterSnapshots = after.ToArray();
        return beforeSnapshots.Length > 0;
    }

    public bool TryGetAppliedSnapshot(Guid itemId, out SceneItemSnapshot snapshot)
        => _appliedSnapshots.TryGetValue(itemId, out snapshot!);

    public Vector3 ResolveReferenceRotationDegrees(Guid itemId)
    {
        if (_appliedSnapshots.TryGetValue(itemId, out SceneItemSnapshot? appliedSnapshot))
        {
            return appliedSnapshot.Transform.RotationDegrees;
        }

        return _startSnapshots.TryGetValue(itemId, out SceneItemSnapshot? startSnapshot)
            ? startSnapshot.Transform.RotationDegrees
            : PrimarySnapshot.Transform.RotationDegrees;
    }

    public void Reset()
    {
        _selectionEntries = [];
        _startSnapshots.Clear();
        _appliedSnapshots.Clear();
        PrimarySnapshot = null!;
    }
}
