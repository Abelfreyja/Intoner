using Intoner.Scene;
using System.Numerics;

namespace Intoner.Objects.Library;

internal sealed record ObjectLibraryPrefabPlacement(
    ObjectLibraryEntry Entry,
    SceneTransform Transform);

internal static class ObjectLibraryPrefabPlacementResolver
{
    public static bool CanResolve(IReadOnlyList<ObjectLibraryEntry> entries)
        => entries.Count > 0 && TryOrder(entries, out _);

    public static bool TryResolve(
        IReadOnlyList<ObjectLibraryEntry> entries,
        Vector3? targetAnchor,
        out IReadOnlyList<ObjectLibraryPrefabPlacement> placements)
    {
        placements = [];
        if (entries.Count == 0 || !TryOrder(entries, out IReadOnlyList<ObjectLibraryEntry> ordered))
        {
            return false;
        }

        Vector3 minimum = ordered[0].WorldTransform!.Position;
        Vector3 maximum = minimum;
        for (int index = 1; index < ordered.Count; ++index)
        {
            Vector3 position = ordered[index].WorldTransform!.Position;
            minimum = Vector3.Min(minimum, position);
            maximum = Vector3.Max(maximum, position);
        }

        Vector3 anchor = new(
            (minimum.X + maximum.X) * 0.5f,
            minimum.Y,
            (minimum.Z + maximum.Z) * 0.5f);
        Vector3 offset = targetAnchor.HasValue ? targetAnchor.Value - anchor : Vector3.Zero;
        placements = ordered
            .Select(entry => new ObjectLibraryPrefabPlacement(
                entry,
                entry.WorldTransform! with { Position = entry.WorldTransform.Position + offset }))
            .ToArray();
        return true;
    }

    public static bool CanRestoreWorldPosition(
        ObjectLibraryPrefab prefab,
        SceneCreationContext currentContext)
        => prefab.CapturedIn.Scope.IsValid
        && prefab.CapturedIn.Scope == currentContext.Scope;

    private static bool TryOrder(
        IReadOnlyList<ObjectLibraryEntry> entries,
        out IReadOnlyList<ObjectLibraryEntry> ordered)
    {
        Dictionary<Guid, ObjectLibraryEntry> byId = new(entries.Count);
        Dictionary<Guid, List<ObjectLibraryEntry>> childrenByParent = [];
        foreach (ObjectLibraryEntry entry in entries)
        {
            if (entry.Id == Guid.Empty
             || entry.WorldTransform is null
             || !byId.TryAdd(entry.Id, entry))
            {
                ordered = [];
                return false;
            }
        }

        Queue<ObjectLibraryEntry> ready = new(entries.Count);
        foreach (ObjectLibraryEntry entry in entries)
        {
            if (entry.AttachmentParentEntryId is not { } parentId)
            {
                ready.Enqueue(entry);
                continue;
            }

            if (!byId.ContainsKey(parentId))
            {
                ordered = [];
                return false;
            }

            if (!childrenByParent.TryGetValue(parentId, out List<ObjectLibraryEntry>? children))
            {
                children = [];
                childrenByParent.Add(parentId, children);
            }

            children.Add(entry);
        }

        List<ObjectLibraryEntry> result = new(entries.Count);
        while (ready.Count > 0)
        {
            ObjectLibraryEntry entry = ready.Dequeue();
            result.Add(entry);
            if (!childrenByParent.TryGetValue(entry.Id, out List<ObjectLibraryEntry>? children))
            {
                continue;
            }

            foreach (ObjectLibraryEntry child in children)
            {
                ready.Enqueue(child);
            }
        }

        ordered = result.Count == entries.Count ? result : [];
        return result.Count == entries.Count;
    }
}
