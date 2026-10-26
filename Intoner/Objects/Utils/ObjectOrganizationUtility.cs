using Intoner.Objects.Models;

namespace Intoner.Objects.Utils;

internal static class ObjectOrganizationUtility
{
    /// <summary> joins a validated destination group without changing the member's owner or world transform </summary>
    public static ObjectSnapshot WithGroup(ObjectSnapshot snapshot, SceneGroupSnapshot group)
        => snapshot with
        {
            GroupId = group.Id,
            FolderPath = group.FolderPath,
            Visible = group.Visible && snapshot.Visible,
        };

    /// <summary> moving a member independently detaches it only when its owner or folder changes </summary>
    public static ObjectSceneItemSnapshot WithDestination(ObjectSceneItemSnapshot snapshot, Guid? layoutId,
        string folderPath, bool movingGroup = false)
    {
        string folder = ObjectFolderUtility.SanitizeFolderPath(folderPath);
        if (snapshot.LayoutId == layoutId && string.Equals(snapshot.FolderPath, folder, StringComparison.OrdinalIgnoreCase))
            return snapshot;

        ObjectSceneItemSnapshot moved = snapshot with { LayoutId = layoutId, FolderPath = folder };
        return moved is ObjectSnapshot member && !movingGroup ? member with { GroupId = null } : moved;
    }
}
