using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Intoner.Objects.Api;
using Intoner.Objects.Models;
using Intoner.Objects.UI.Services;
using System.Numerics;

namespace Intoner.Objects.UI.Components;

internal static class TransformClipboardControls
{
    private const int ButtonCount = 2;

    public static float ResolveWidth()
    {
        float edge = EditorIconButton.MeasureCompactEdge();
        return (edge * ButtonCount) + ImGui.GetStyle().ItemSpacing.X;
    }

    public static bool Draw(IObjectClipboardService clipboard, string id, ObjectTransformPart part, Vector3 value, out Vector3 pastedValue,
        bool uniformScale = false)
    {
        pastedValue = default;

        bool pasted = false;
        string label = ResolveLabel(part);
        Vector4 accent = ResolveAccent(part);
        if (EditorIconButton.DrawCompact($"{id}_copy", FontAwesomeIcon.Copy, $"Copy {label}", accent))
        {
            _ = clipboard.CopyTransform(part, value);
        }

        ImGui.SameLine();

        bool hasTransform = clipboard.TryPasteTransform(part, out Vector3 clipboardValue);
        bool canPaste = hasTransform && (!uniformScale || clipboardValue == new Vector3(clipboardValue.X));
        string pasteTooltip = canPaste ? $"Paste {label}" : "Group scaling requires equal X, Y, and Z values.";

        if (!hasTransform)
        {
            pasteTooltip = $"Clipboard does not contain {label}";
        }

        if (EditorIconButton.DrawCompact($"{id}_paste", FontAwesomeIcon.FileImport, pasteTooltip, accent, canPaste))
        {
            pastedValue = clipboardValue;
            pasted = true;
        }

        return pasted;
    }

    private static string ResolveLabel(ObjectTransformPart part)
        => part switch
        {
            ObjectTransformPart.Position => "Position",
            ObjectTransformPart.Rotation => "Rotation",
            ObjectTransformPart.Scale    => "Scale",
            _                            => part.ToString(),
        };

    private static Vector4 ResolveAccent(ObjectTransformPart part)
        => part switch
        {
            ObjectTransformPart.Position => EditorColors.TransformModeAccent(GizmoTransformMode.Translation),
            ObjectTransformPart.Rotation => EditorColors.TransformModeAccent(GizmoTransformMode.Rotation),
            ObjectTransformPart.Scale    => EditorColors.TransformModeAccent(GizmoTransformMode.Scale),
            _                            => ThemeColors.AccentPrimary,
        };
}
