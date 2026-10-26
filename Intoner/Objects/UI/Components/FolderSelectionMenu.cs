using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Intoner.Objects.Utils;

namespace Intoner.Objects.UI.Components;

internal static class FolderSelectionMenu
{
    public const string RootLabel = "No Folder";

    public static void DrawPaths(
        IReadOnlyList<string> paths,
        string? selectedPath,
        Action<string> select,
        string rootLabel = RootLabel,
        FontAwesomeIcon rootIcon = FontAwesomeIcon.TimesCircle)
        => Draw(
            paths,
            selectedPath is not null && string.IsNullOrWhiteSpace(selectedPath),
            path => selectedPath is not null && string.Equals(selectedPath, path, StringComparison.OrdinalIgnoreCase),
            static path => path,
            ObjectFolderUtility.GetParentFolderPath,
            ObjectFolderUtility.GetFolderName,
            () => select(string.Empty),
            select,
            rootLabel,
            rootIcon);

    public static void Draw<TFolder>(
        IReadOnlyList<TFolder> folders,
        bool ungroupedSelected,
        Func<TFolder, bool> isSelected,
        Func<TFolder, string> getId,
        Func<TFolder, string> getParentId,
        Func<TFolder, string> getLabel,
        Action selectUngrouped,
        Action<TFolder> selectFolder,
        string rootLabel = RootLabel,
        FontAwesomeIcon rootIcon = FontAwesomeIcon.TimesCircle)
    {
        using (ImRaii.PushId("folderRoot"))
        {
            if (EditorContextMenu.DrawItem(
                    rootIcon,
                    rootLabel,
                    selected: ungroupedSelected))
            {
                selectUngrouped();
                return;
            }
        }

        if (!HierarchyIndex<string, TFolder>.TryCreate(
                folders,
                getId,
                getParentId,
                string.Empty,
                StringComparer.OrdinalIgnoreCase,
                out HierarchyIndex<string, TFolder> hierarchy,
                out _))
        {
            return;
        }

        if (hierarchy.Roots.Count > 0)
        {
            EditorContextMenu.DrawSeparator();
        }

        foreach (TFolder folder in hierarchy.Roots)
        {
            DrawFolder(folder);
        }

        if (folders.Count == 0)
        {
            EditorContextMenu.DrawHint("No folders available");
        }

        void DrawFolder(TFolder folder)
        {
            IReadOnlyList<TFolder> children = hierarchy.GetChildren(getId(folder));
            if (children.Count == 0)
            {
                if (EditorContextMenu.DrawItem(
                        FontAwesomeIcon.Folder,
                        getLabel(folder),
                        isSelected(folder),
                        id: getId(folder)))
                {
                    selectFolder(folder);
                }

                return;
            }

            using EditorContextMenu.SubMenuScope subMenu = EditorContextMenu.BeginSubMenu(
                $"folderSelection:{getId(folder)}",
                FontAwesomeIcon.Folder,
                getLabel(folder));
            if (!subMenu)
            {
                return;
            }

            if (EditorContextMenu.DrawItem(
                    FontAwesomeIcon.FolderOpen,
                    "This Folder",
                    isSelected(folder),
                    id: $"{getId(folder)}:self"))
            {
                selectFolder(folder);
            }

            EditorContextMenu.DrawSeparator();
            foreach (TFolder child in children)
            {
                DrawFolder(child);
            }
        }
    }
}
