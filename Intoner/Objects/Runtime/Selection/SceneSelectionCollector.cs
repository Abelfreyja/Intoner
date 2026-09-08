using System.Numerics;
using System.Runtime.InteropServices;

namespace Intoner.Scene;

internal enum SceneSelectionPrimitiveKind
{
    Box,
    Sphere,
    Cone,
}

[StructLayout(LayoutKind.Auto)]
internal readonly record struct SceneSelectionModelDraw(
    uint SelectionId,
    Guid ItemId,
    string ModelPath,
    Matrix4x4 WorldTransform);

[StructLayout(LayoutKind.Auto)]
internal readonly record struct SceneSelectionPrimitiveDraw(
    uint SelectionId,
    Guid ItemId,
    SceneSelectionPrimitiveKind PrimitiveKind,
    Matrix4x4 WorldTransform);

internal sealed class SceneSelectionCollector
{
    private sealed class SelectionEntry
    {
        public required uint SelectionId { get; init; }
        public required SceneItemSnapshot Snapshot { get; init; }
        public HashSet<string>? ModelPaths { get; set; }
    }

    private readonly Dictionary<Guid, SelectionEntry> _entriesByItemId = [];
    private readonly Dictionary<uint, SelectionEntry> _entriesBySelectionId = [];
    private readonly List<SceneSelectionModelDraw> _modelDraws = [];
    private readonly List<SceneSelectionPrimitiveDraw> _primitiveDraws = [];
    private uint _nextSelectionId = 1;

    public IReadOnlyList<SceneSelectionModelDraw> ModelDraws
        => _modelDraws;

    public IReadOnlyList<SceneSelectionPrimitiveDraw> PrimitiveDraws
        => _primitiveDraws;

    public bool HasDraws
        => _modelDraws.Count > 0 || _primitiveDraws.Count > 0;

    public void AddModel(SceneItemSnapshot snapshot, string modelPath, SceneTransform transform)
        => AddModel(snapshot, modelPath, SceneTransformMath.CreateWorldTransform(transform));

    public void AddModel(SceneItemSnapshot snapshot, string modelPath, Matrix4x4 worldTransform)
    {
        if (string.IsNullOrWhiteSpace(modelPath))
        {
            return;
        }

        var selectionEntry = GetOrCreateSelectionEntry(snapshot);
        _modelDraws.Add(new SceneSelectionModelDraw(
            selectionEntry.SelectionId,
            snapshot.Id,
            modelPath,
            worldTransform));
        RegisterModelPath(selectionEntry, modelPath);
    }

    public void AddPrimitive(SceneItemSnapshot snapshot, SceneSelectionPrimitiveKind primitiveKind, Matrix4x4 worldTransform)
        => _primitiveDraws.Add(new SceneSelectionPrimitiveDraw(
            GetOrCreateSelectionEntry(snapshot).SelectionId,
            snapshot.Id,
            primitiveKind,
            worldTransform));

    public bool TryGetSnapshot(uint selectionId, out SceneItemSnapshot snapshot)
    {
        if (_entriesBySelectionId.TryGetValue(selectionId, out var selectionEntry))
        {
            snapshot = selectionEntry.Snapshot;
            return true;
        }

        snapshot = default!;
        return false;
    }

    public void TouchModelPaths(uint selectionId, Action<string> touch)
    {
        if (!_entriesBySelectionId.TryGetValue(selectionId, out var selectionEntry)
            || selectionEntry.ModelPaths is null)
        {
            return;
        }

        foreach (var modelPath in selectionEntry.ModelPaths)
        {
            touch(modelPath);
        }
    }

    private SelectionEntry GetOrCreateSelectionEntry(SceneItemSnapshot snapshot)
    {
        if (_entriesByItemId.TryGetValue(snapshot.Id, out var selectionEntry))
        {
            return selectionEntry;
        }

        selectionEntry = new SelectionEntry
        {
            SelectionId = _nextSelectionId++,
            Snapshot = snapshot,
        };

        _entriesByItemId[snapshot.Id] = selectionEntry;
        _entriesBySelectionId[selectionEntry.SelectionId] = selectionEntry;
        return selectionEntry;
    }

    private static void RegisterModelPath(SelectionEntry selectionEntry, string modelPath)
    {
        selectionEntry.ModelPaths ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        selectionEntry.ModelPaths.Add(modelPath);
    }
}
