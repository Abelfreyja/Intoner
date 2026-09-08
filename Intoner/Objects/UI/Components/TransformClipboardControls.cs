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

    public static bool Draw(IObjectClipboardService clipboard, string id, ObjectTransformPart part, Vector3 value, out Vector3 pastedValue)
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

        bool canPaste = clipboard.TryPasteTransform(part, out Vector3 clipboardValue);
        string pasteTooltip = canPaste
            ? $"Paste {label}"
            : $"Clipboard does not contain {label}";
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
