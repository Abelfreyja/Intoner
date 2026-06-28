using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Intoner.Objects.Models;
using Intoner.Objects.UI.Services;
using Intoner.UI;
using System.Numerics;

namespace Intoner.Objects.UI.Components;

internal static class TransformClipboardControls
{
    private const int ButtonCount = 2;

    public static float ResolveWidth()
    {
        float edge = ResolveButtonEdge();
        return (edge * ButtonCount) + ImGui.GetStyle().ItemSpacing.X;
    }

    public static bool Draw(IClipboardExportService clipboardExportService, string id, TransformClipboardKind kind, Vector3 value, out Vector3 pastedValue)
    {
        pastedValue = default;

        bool pasted = false;
        string label = ResolveLabel(kind);
        Vector4 accent = ResolveAccent(kind);
        if (DrawButton($"{id}_copy", FontAwesomeIcon.Copy, $"Copy {label}", accent, enabled: true))
        {
            clipboardExportService.CopyTransform(kind, value);
        }

        ImGui.SameLine();

        bool canPaste = clipboardExportService.TryPasteTransform(kind, out Vector3 clipboardValue);
        string pasteTooltip = canPaste
            ? $"Paste {label}"
            : $"Clipboard does not contain {label}";
        if (DrawButton($"{id}_paste", FontAwesomeIcon.FileImport, pasteTooltip, accent, canPaste))
        {
            pastedValue = clipboardValue;
            pasted = true;
        }

        return pasted;
    }

    private static bool DrawButton(string id, FontAwesomeIcon icon, string tooltip, Vector4 accent, bool enabled)
    {
        float edge = ResolveButtonEdge();
        Vector2 size = new(edge, edge);
        Vector4 textColor = enabled
            ? accent
            : EditorColors.TextDisabled;
        Vector4 fill = EditorColors.ButtonDefault with { W = enabled ? 0.62f : 0.34f };

        using var button = ImRaii.PushColor(ImGuiCol.Button, fill);
        using var hovered = ImRaii.PushColor(ImGuiCol.ButtonHovered, accent with { W = enabled ? 0.16f : 0.06f });
        using var active = ImRaii.PushColor(ImGuiCol.ButtonActive, accent with { W = enabled ? 0.22f : 0.06f });
        using var text = ImRaii.PushColor(ImGuiCol.Text, textColor);
        using var rounding = ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, 5f * ImGuiHelpers.GlobalScale);

        bool clicked;
        using (ImRaii.Disabled(!enabled))
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            clicked = ImGui.Button($"{icon.ToIconString()}##{id}", size);
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            UiSharedService.DrawAccentTooltipText(tooltip, accent, wrapEms: 35f);
        }

        return enabled && clicked;
    }

    private static float ResolveButtonEdge()
        => MathF.Max(ImGui.GetFrameHeight(), 22f * ImGuiHelpers.GlobalScale);

    private static string ResolveLabel(TransformClipboardKind kind)
        => kind switch
        {
            TransformClipboardKind.Position => "Position",
            TransformClipboardKind.Rotation => "Rotation",
            TransformClipboardKind.Scale    => "Scale",
            _                               => kind.ToString(),
        };

    private static Vector4 ResolveAccent(TransformClipboardKind kind)
        => kind switch
        {
            TransformClipboardKind.Position => EditorColors.TransformModeAccent(GizmoTransformMode.Translation),
            TransformClipboardKind.Rotation => EditorColors.TransformModeAccent(GizmoTransformMode.Rotation),
            TransformClipboardKind.Scale    => EditorColors.TransformModeAccent(GizmoTransformMode.Scale),
            _                               => EditorColors.AccentPurple,
        };
}
