using Dalamud.Interface;
using Intoner.Objects.Models;
using Intoner.Objects.Utils;
using System.Numerics;

namespace Intoner.Objects.UI.Components
{
    internal static class ObjectDestinationMenu
    {
        public const string UnassignedLabel = "Unassigned";

        public static void Draw(
            IReadOnlyList<ObjectLayoutSnapshot> layouts,
            Func<Guid?, IReadOnlyList<string>> getFolders,
            Guid? selectedLayoutId,
            string? selectedFolderPath,
            Action<Guid?, string> select)
        {
            DrawOwner(null, UnassignedLabel, FontAwesomeIcon.Cubes);
            foreach (ObjectLayoutSnapshot layout in layouts)
            {
                DrawOwner(layout.Id, TextUtility.TrimOrFallback(layout.Name, "Unnamed layout"), FontAwesomeIcon.LayerGroup);
            }
            if (layouts.Count == 0)
            {
                EditorContextMenu.DrawHint("No layouts available");
            }

            void DrawOwner(Guid? layoutId, string name, FontAwesomeIcon icon)
            {
                IReadOnlyList<string> folders = getFolders(layoutId);
                string id = layoutId?.ToString("N") ?? "unassigned";
                bool selectedOwner = selectedFolderPath is not null && selectedLayoutId == layoutId;
                Vector4? accent = layoutId.HasValue ? null : ThemeColors.AccentRose;
                if (folders.Count == 0)
                {
                    if (EditorContextMenu.DrawItem(icon, name, selectedOwner && selectedFolderPath!.Length == 0,
                            id: $"destination:{id}", accent: accent))
                    {
                        select(layoutId, string.Empty);
                    }
                    return;
                }

                using EditorContextMenu.SubMenuScope menu = EditorContextMenu.BeginSubMenu(
                    $"destination:{id}", icon, name,
                    color: selectedOwner && layoutId.HasValue ? ThemeColors.AccentPrimary : null, accent: accent);
                if (menu)
                {
                    FolderSelectionMenu.DrawPaths(folders, selectedOwner ? selectedFolderPath : null,
                        folder => select(layoutId, folder), "This Layout", icon);
                }
            }
        }
    }
}
