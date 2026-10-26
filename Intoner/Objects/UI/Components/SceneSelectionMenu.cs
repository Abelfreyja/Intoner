using Dalamud.Interface;
using Intoner.Scene;
using Intoner.Objects.UI.Services;

namespace Intoner.Objects.UI.Components
{
    internal static class SceneSelectionMenu
    {
        public static void DrawChildren(EditorInteraction interaction, IEnumerable<SceneItemSnapshot> children)
        {
            if (EditorContextMenu.DrawItem(FontAwesomeIcon.Check, "Select All Children", enabled: children.Any(static child => !child.Locked)))
            {
                interaction.SelectionChanged(interaction.Selection.TryReplaceSelection(
                    children.Where(static child => !child.Locked).Select(static child => child.Id)));
            }
        }
    }
}
