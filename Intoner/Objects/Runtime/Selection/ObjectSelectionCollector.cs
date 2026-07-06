using Intoner.Objects.Models;
using Intoner.Objects.Resources;
using Intoner.Objects.Utils;
using Intoner.Objects.Assets;
using System.Numerics;

namespace Intoner.Objects.Runtime;

internal enum ObjectSelectionPrimitiveKind
{
    Sphere,
    Cone,
    Pyramid,
}

internal readonly record struct ObjectSelectionModelDraw(
    uint SelectionId,
    Guid ObjectId,
    string ModelPath,
    Matrix4x4 WorldTransform);

internal readonly record struct ObjectSelectionPrimitiveDraw(
    uint SelectionId,
    Guid ObjectId,
    ObjectSelectionPrimitiveKind PrimitiveKind,
    Matrix4x4 WorldTransform);

internal sealed class ObjectSelectionCollector
{
    private sealed class SelectionEntry
    {
        public required uint SelectionId { get; init; }
        public required ObjectSnapshot Snapshot { get; init; }
        public HashSet<string>? ModelPaths { get; set; }
    }

    private readonly Dictionary<Guid, SelectionEntry> _entriesByObjectId = [];
    private readonly Dictionary<uint, SelectionEntry> _entriesBySelectionId = [];
    private readonly List<ObjectSelectionModelDraw> _modelDraws = [];
    private readonly List<ObjectSelectionPrimitiveDraw> _primitiveDraws = [];
    private uint _nextSelectionId = 1;

    public IReadOnlyList<ObjectSelectionModelDraw> ModelDraws
        => _modelDraws;

    public IReadOnlyList<ObjectSelectionPrimitiveDraw> PrimitiveDraws
        => _primitiveDraws;

    public bool HasDraws
        => _modelDraws.Count > 0 || _primitiveDraws.Count > 0;

    public void AddModel(ObjectSnapshot snapshot, string modelPath, ObjectTransform transform)
        => AddModel(snapshot, modelPath, CreateWorldTransform(transform));

    public void AddModel(ObjectSnapshot snapshot, string modelPath, Matrix4x4 worldTransform)
    {
        var normalizedPath = ObjectResourcePathUtility.NormalizeTrackedPath(modelPath);
        if (string.IsNullOrWhiteSpace(normalizedPath) || !ObjectPathRules.IsModelPath(normalizedPath))
        {
            return;
        }

        var selectionEntry = GetOrCreateSelectionEntry(snapshot);
        _modelDraws.Add(new ObjectSelectionModelDraw(
            selectionEntry.SelectionId,
            snapshot.Id,
            normalizedPath,
            worldTransform));
        RegisterModelPath(selectionEntry, normalizedPath);
    }

    public void AddPrimitive(ObjectSnapshot snapshot, ObjectSelectionPrimitiveKind primitiveKind, Matrix4x4 worldTransform)
        => _primitiveDraws.Add(new ObjectSelectionPrimitiveDraw(
            GetOrCreateSelectionEntry(snapshot).SelectionId,
            snapshot.Id,
            primitiveKind,
            worldTransform));

    public bool TryGetSnapshot(uint selectionId, out ObjectSnapshot snapshot)
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

    public static Matrix4x4 CreateWorldTransform(ObjectTransform transform)
        => CreateWorldTransform(
            transform.Position,
            CreateRotation(transform.RotationDegrees),
            transform.Scale);

    public static Matrix4x4 CreateWorldTransform(Vector3 translation, Quaternion rotation, Vector3 scale)
    {
        var worldTransform = Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(rotation);
        worldTransform.Translation = translation;
        return worldTransform;
    }

    public static Quaternion CreateRotation(Vector3 rotationDegrees)
        => ObjectTransformMath.CreateRotationQuaternion(rotationDegrees);

    private SelectionEntry GetOrCreateSelectionEntry(ObjectSnapshot snapshot)
    {
        if (_entriesByObjectId.TryGetValue(snapshot.Id, out var selectionEntry))
        {
            return selectionEntry;
        }

        selectionEntry = new SelectionEntry
        {
            SelectionId = _nextSelectionId++,
            Snapshot = snapshot,
        };

        _entriesByObjectId[snapshot.Id] = selectionEntry;
        _entriesBySelectionId[selectionEntry.SelectionId] = selectionEntry;
        return selectionEntry;
    }

    private static void RegisterModelPath(SelectionEntry selectionEntry, string modelPath)
    {
        selectionEntry.ModelPaths ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        selectionEntry.ModelPaths.Add(modelPath);
    }
}


