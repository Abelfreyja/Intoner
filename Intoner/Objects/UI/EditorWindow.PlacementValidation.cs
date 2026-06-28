using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Intoner.Objects.Models;
using Intoner.Objects.Runtime;
using Intoner.UI;
using System.Numerics;

namespace Intoner.Objects.UI;

internal sealed partial class EditorWindow
{
    private bool DrawHousingPlacementInspector(ObjectSnapshot snapshot)
    {
        if (!_placementEvaluations.TryGetValue(snapshot.Id, out PlacementEvaluation? evaluation)
            || evaluation is null)
        {
            return false;
        }

        Vector4 accent = ResolveHousingPlacementAccent(evaluation.Status);
        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(3f);
        DrawHousingPlacementHeader(evaluation, accent);
        DrawHousingPlacementMessage(evaluation, accent);
        DrawHousingPlacementFixes(snapshot, evaluation);
        return true;
    }

    private static void DrawHousingPlacementHeader(PlacementEvaluation evaluation, Vector4 accent)
    {
        using (ImRaii.PushFont(UiBuilder.IconFont))
        using (ImRaii.PushColor(ImGuiCol.Text, accent))
        {
            ImGui.TextUnformatted(ResolveHousingPlacementIcon(evaluation.Status).ToIconString());
        }

        ImGui.SameLine(0f, Scaled(6f));
        ImGui.TextUnformatted("Housing Placement");
        ImGui.SameLine(0f, Scaled(6f));
        DrawHousingPlacementStatusBadge(evaluation.Status, accent);
    }

    private static void DrawHousingPlacementMessage(PlacementEvaluation evaluation, Vector4 accent)
    {
        string message = evaluation.Status == PlacementValidationStatus.Valid
            ? "Placement satisfies the current housing rules."
            : evaluation.Message;
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        using var color = ImRaii.PushColor(
            ImGuiCol.Text,
            evaluation.Status == PlacementValidationStatus.Invalid
                ? EditorColors.WithAlpha(accent, 0.95f)
                : EditorColors.TextDisabled);
        using IDisposable wrap = ImRaiiScope.TextWrapPos(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X);
        ImGui.TextUnformatted(message);
    }

    private void DrawHousingPlacementFixes(ObjectSnapshot snapshot, PlacementEvaluation evaluation)
    {
        if (evaluation.Fixes.Count == 0)
        {
            return;
        }

        ImGuiHelpers.ScaledDummy(3f);
        ImGui.TextDisabled("Suggested fixes");
        for (var index = 0; index < evaluation.Fixes.Count; ++index)
        {
            PlacementFixProposal fix = evaluation.Fixes[index];
            float width = MathF.Max(1f, ImGui.GetContentRegionAvail().X);
            if (DrawIconTextButton($"##housingPlacementFix{snapshot.Id:N}{index}", ResolvePlacementFixIcon(fix.Kind), fix.Label, width)
                && TryApplyPlacementFix(snapshot, fix))
            {
                break;
            }

            if (ImGui.IsItemHovered())
            {
                UiSharedService.DrawAccentTooltip(() =>
                {
                    ImGui.TextUnformatted(fix.Label);
                    ImGui.Separator();
                    using IDisposable wrap = ImRaiiScope.TextWrapPos(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X);
                    ImGui.TextUnformatted(fix.Description);
                }, ResolvePlacementFixAccent(fix.Kind), unscaledFixedWidth: 260f);
            }

            if (index + 1 < evaluation.Fixes.Count)
            {
                ImGuiHelpers.ScaledDummy(3f);
            }
        }
    }

    private bool TryApplyPlacementFix(ObjectSnapshot snapshot, PlacementFixProposal fix)
    {
        CommitPendingHistory();
        return _placementFixExecutor.TryApply(snapshot, fix);
    }

    private static void DrawHousingPlacementStatusBadge(PlacementValidationStatus status, Vector4 accent)
    {
        string label = ResolveHousingPlacementStatusLabel(status);
        Vector2 padding = ScaledVector(6f, 1.5f);
        Vector2 textSize = ImGui.CalcTextSize(label);
        Vector2 size = textSize + (padding * 2f);
        Vector2 min = ImGui.GetCursorScreenPos();
        Vector2 max = min + size;
        ImDrawListPtr drawList = ImGui.GetWindowDrawList();

        drawList.AddRectFilled(min, max, ImGui.GetColorU32(EditorColors.WithAlpha(accent, 0.16f)), size.Y * 0.5f);
        drawList.AddRect(min, max, ImGui.GetColorU32(EditorColors.WithAlpha(accent, 0.72f)), size.Y * 0.5f);
        drawList.AddText(min + padding, ImGui.GetColorU32(EditorColors.WithAlpha(accent, 0.96f)), label);
        ImGui.Dummy(size);
    }

    private static FontAwesomeIcon ResolveHousingPlacementIcon(PlacementValidationStatus status)
        => status switch
        {
            PlacementValidationStatus.Valid   => FontAwesomeIcon.CheckCircle,
            PlacementValidationStatus.Invalid => FontAwesomeIcon.ExclamationTriangle,
            _                                  => FontAwesomeIcon.QuestionCircle,
        };

    private static Vector4 ResolveHousingPlacementAccent(PlacementValidationStatus status)
        => status switch
        {
            PlacementValidationStatus.Valid   => EditorColors.AccentGreen,
            PlacementValidationStatus.Invalid => EditorColors.HousingPlacementInvalid,
            _                                  => EditorColors.AccentYellow,
        };

    private static string ResolveHousingPlacementStatusLabel(PlacementValidationStatus status)
        => status switch
        {
            PlacementValidationStatus.Valid   => "valid",
            PlacementValidationStatus.Invalid => "invalid",
            _                                  => "unknown",
        };

    private static FontAwesomeIcon ResolvePlacementFixIcon(PlacementFixKind kind)
        => kind switch
        {
            PlacementFixKind.SnapToSurface         => FontAwesomeIcon.ArrowDown,
            PlacementFixKind.MoveToPlayerPlacement => FontAwesomeIcon.Running,
            PlacementFixKind.ClearAttachmentParent => FontAwesomeIcon.Unlink,
            _                                      => FontAwesomeIcon.Magic,
        };

    private static Vector4 ResolvePlacementFixAccent(PlacementFixKind kind)
        => kind switch
        {
            PlacementFixKind.SnapToSurface         => EditorColors.AccentBlue,
            PlacementFixKind.MoveToPlayerPlacement => EditorColors.AccentGreen,
            PlacementFixKind.ClearAttachmentParent => EditorColors.AccentYellow,
            _                                      => EditorColors.AccentPurple,
        };
}
