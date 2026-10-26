using Intoner.Objects.Models;
using Intoner.Scene;
using System.Numerics;

namespace Intoner.Objects.Utils;

internal static class SceneGroupUtility
{
    public static bool TryPlanChange(SceneGroupSnapshot group, SceneGroupSnapshot requested,
        IReadOnlyList<ObjectSnapshot> members, IReadOnlySet<Guid> scalableMembers, bool editPivot,
        out IReadOnlyList<SceneItemSnapshot> before, out IReadOnlyList<SceneItemSnapshot> after)
    {
        before = [];
        after = [];
        if (editPivot)
        {
            if (group.Locked || requested with { Transform = group.Transform } != group
                || group.Transform.Scale != requested.Transform.Scale
                || !NumericsUtility.IsFinite(requested.Transform.Position)
                || !NumericsUtility.IsFinite(requested.Transform.RotationDegrees))
                return false;

            before = [group];
            after = [requested];
            return true;
        }

        before = [.. members, group];
        IReadOnlyList<SceneItemSnapshot> transformed = before;
        if (group.Transform != requested.Transform
            && !TryTransform(group, requested.Transform, members, scalableMembers, out transformed))
            return false;

        bool updateMembers = group.LayoutId != requested.LayoutId || !string.Equals(group.FolderPath, requested.FolderPath, StringComparison.Ordinal)
            || group.Visible != requested.Visible || group.Locked != requested.Locked;
        if (!updateMembers && Equals(transformed[^1], requested))
        {
            after = transformed;
            return true;
        }

        SceneItemSnapshot[] resolved = [.. transformed];
        if (updateMembers)
        {
            for (int index = 0; index < members.Count; ++index)
            {
                ObjectSnapshot member = (ObjectSnapshot)resolved[index];
                resolved[index] = member with
                {
                    LayoutId = requested.LayoutId,
                    FolderPath = requested.FolderPath,
                    Visible = group.Visible != requested.Visible ? requested.Visible : member.Visible,
                    Locked = group.Locked != requested.Locked ? requested.Locked : member.Locked,
                };
            }
        }
        resolved[^1] = requested;
        after = resolved;
        return true;
    }

    public static SceneGroupSnapshot? ResolveSelectedGroup(IReadOnlyList<SceneItemSnapshot> items)
    {
        if (items.Count == 0 || items[^1] is not SceneGroupSnapshot group)
            return null;

        for (int index = 0; index < items.Count - 1; ++index)
        {
            SceneItemSnapshot item = items[index];

            if (item.Id != group.Id && (item is not ObjectSnapshot member || member.GroupId != group.Id))
                return null;
        }

        return group;
    }

    public static bool TryTransform(SceneGroupSnapshot group, SceneTransform transform,
        IReadOnlyList<ObjectSnapshot> members, IReadOnlySet<Guid> scalableMembers, out IReadOnlyList<SceneItemSnapshot> transformed)
    {
        transformed = [];
        if (group.Locked || !IsValidTransform(transform)
            || members.Any(member => member.Locked || member.GroupId != group.Id)
            || !TryValidateOwner(group.LayoutId, members, [group], out _))
            return false;

        float factor = transform.Scale.X / group.Transform.Scale.X;

        if (!float.IsFinite(factor) || factor <= 0f)
            return false;

        bool rotate = group.Transform.RotationDegrees != transform.RotationDegrees;
        Quaternion delta = rotate
            ? SceneTransformMath.ResolveRotationDelta(SceneTransformMath.CreateRotationQuaternion(group.Transform.RotationDegrees),
                SceneTransformMath.CreateRotationQuaternion(transform.RotationDegrees))
            : Quaternion.Identity;
        List<SceneItemSnapshot> after = new(members.Count + 1);
        foreach (ObjectSnapshot member in members)
        {
            bool scaleSize = scalableMembers.Contains(member.Id);

            if (scaleSize && factor < SceneTransformMath.MinimumUniformScaleFactor(member.Transform.Scale))
                return false;

            SceneTransform scaled = SceneTransformMath.ApplyUniformScale(member.Transform, group.Transform.Position, factor, scaleSize);
            SceneTransform moved = rotate
                ? SceneTransformMath.ApplyRigidRotation(scaled, transform.Position, scaled.Position - group.Transform.Position,
                    SceneTransformMath.CreateRotationQuaternion(member.Transform.RotationDegrees), delta, member.Transform.RotationDegrees)
                : scaled with { Position = transform.Position + (scaled.Position - group.Transform.Position) };

            if (!NumericsUtility.IsFinite(moved.Position) || !NumericsUtility.IsFinite(moved.RotationDegrees) || !NumericsUtility.IsFinite(moved.Scale))
                return false;

            after.Add(member with { Transform = moved });
        }

        after.Add(group with { Transform = transform });
        transformed = after;
        return true;
    }

    public static bool TryValidateOwner(
        Guid? layoutId,
        IReadOnlyList<ObjectSnapshot> objects,
        IReadOnlyList<SceneGroupSnapshot> groups,
        out string error)
    {
        error = string.Empty;
        HashSet<Guid> identities = [];
        Dictionary<Guid, SceneGroupSnapshot> groupsById = [];
        foreach (SceneGroupSnapshot group in groups)
        {
            if (!IsValid(group) || !identities.Add(group.Id) || group.LayoutId != layoutId)
            {
                error = $"group '{group.Name}' ({group.Id:D}) has an invalid identity, owner, name, or pivot";
                return false;
            }

            groupsById.Add(group.Id, group);
        }

        foreach (ObjectSnapshot snapshot in objects)
        {
            if (snapshot.Id == Guid.Empty || !identities.Add(snapshot.Id) || snapshot.LayoutId != layoutId)
            {
                error = $"object '{snapshot.Name}' ({snapshot.Id:D}) has a duplicate identity or invalid owner";
                return false;
            }

            if (snapshot.GroupId is Guid groupId && (!groupsById.TryGetValue(groupId, out SceneGroupSnapshot? group)
                || !IsValidMember(snapshot, group)))
            {
                error = "group members must share their owner's folder and location, and hidden groups cannot contain visible members";
                return false;
            }
        }

        return true;
    }

    public static bool IsValid(SceneGroupSnapshot group)
        => group.Id != Guid.Empty && !string.IsNullOrWhiteSpace(group.Name) && IsValidTransform(group.Transform);

    public static bool IsValidMember(ObjectSnapshot snapshot, SceneGroupSnapshot group)
        => snapshot.GroupId == group.Id
        && snapshot.LayoutId == group.LayoutId
        && string.Equals(snapshot.FolderPath, group.FolderPath, StringComparison.OrdinalIgnoreCase)
        && snapshot.CreatedIn.HasSameLocation(group.CreatedIn)
        && (group.Visible || !snapshot.Visible);

    private static bool IsValidTransform(SceneTransform transform)
        => NumericsUtility.IsFinite(transform.Position)
        && NumericsUtility.IsFinite(transform.RotationDegrees)
        && NumericsUtility.IsFinite(transform.Scale)
        && transform.Scale.X >= SceneTransformMath.MinimumScale
        && transform.Scale.X.Equals(transform.Scale.Y)
        && transform.Scale.X.Equals(transform.Scale.Z);
}
