using Intoner.Objects.Utils;
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
            minimum.X * 0.5f + maximum.X * 0.5f,
            minimum.Y,
            minimum.Z * 0.5f + maximum.Z * 0.5f);
        List<ObjectLibraryPrefabPlacement> resolved = new(ordered.Count);
        foreach (ObjectLibraryEntry entry in ordered)
        {
            SceneTransform transform = entry.WorldTransform!;
            if (targetAnchor is { } target)
            {
                Vector3 position = transform.Position;
                transform = transform with
                {
                    Position = new Vector3(
                        (float)(target.X + ((double)position.X - anchor.X)),
                        (float)(target.Y + ((double)position.Y - anchor.Y)),
                        (float)(target.Z + ((double)position.Z - anchor.Z))),
                };
            }

            if (!NumericsUtility.IsFinite(transform.Position))
            {
                return false;
            }

            resolved.Add(new ObjectLibraryPrefabPlacement(entry, transform));
        }

        placements = resolved;
        return true;
    }

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
