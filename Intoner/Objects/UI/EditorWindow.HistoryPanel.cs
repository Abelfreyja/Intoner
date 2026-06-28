using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Intoner.Objects.Runtime;
using Intoner.Objects.UI.Components;
using Intoner.UI;
using Intoner.UI.Performance;
using System.Numerics;

namespace Intoner.Objects.UI;

internal sealed partial class EditorWindow
{
    private const string HistoryCheckpointPopupId = "##objectHistoryCheckpointPopup";

    private int? _historyCheckpointEditStateIndex;
    private string _historyCheckpointEditLabel = string.Empty;
    private bool _openHistoryCheckpointPopupNextFrame;
    private bool _focusCurrentHistoryEntry = true;

    private void DrawHistoryWorkspace()
    {
        var entries = _objectHistoryManager.Entries;
        DrawHistoryHero(entries);
        DrawHistoryTimelineCard(entries);
        DrawHistoryCheckpointPopup(entries);
    }

    private void ResetHistoryWorkspaceState()
    {
        _historyCheckpointEditStateIndex = null;
        _historyCheckpointEditLabel = string.Empty;
        _openHistoryCheckpointPopupNextFrame = false;
        _focusCurrentHistoryEntry = true;
    }

    private void DrawHistoryHero(IReadOnlyList<ObjectHistoryEntry> entries)
    {
        var accent = EditorColors.AccentPurple;
        var currentStateIndex = Math.Clamp(_objectHistoryManager.CurrentStateIndex, 0, Math.Max(0, entries.Count - 1));
        var checkpointCount = entries.Count(static entry => entry.HasCheckpoint);
        var currentEntry = entries[currentStateIndex];

        DrawPanelCard(
            "object-history-hero",
            EditorColors.ButtonDefault with { W = 0.30f },
            accent with { W = 0.24f },
            8f * ImGuiHelpers.GlobalScale,
            new Vector2(10f * ImGuiHelpers.GlobalScale, 8f * ImGuiHelpers.GlobalScale),
            () =>
            {
                using var table = ImRaii.Table("##objectHistoryHeroHeader", 2, ImGuiTableFlags.SizingStretchProp);
                if (!table)
                {
                    return;
                }

                ImGui.TableSetupColumn("Info", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("Actions", ImGuiTableColumnFlags.WidthFixed);

                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                using (ImRaii.PushFont(UiBuilder.IconFont))
                using (ImRaii.PushColor(ImGuiCol.Text, accent))
                {
                    ImGui.TextUnformatted(FontAwesomeIcon.History.ToIconString());
                }

                ImGui.SameLine();
                using (ImRaii.Group())
                {
                    ImGui.TextUnformatted("History");
                    using var wrap = ImRaiiScope.TextWrapPos();
                    ImGui.TextDisabled(BuildHistoryStatus(entries, currentEntry, checkpointCount));
                }

                ImGui.TableNextColumn();
                DrawHistoryHeroActions(entries, currentEntry);

            });
    }

    private void DrawHistoryHeroActions(IReadOnlyList<ObjectHistoryEntry> entries, ObjectHistoryEntry currentEntry)
    {
        var canUndo = _objectHistoryManager.UndoActionKind is not null;
        var canRedo = _objectHistoryManager.RedoActionKind is not null;
        var canClear = entries.Count > 1 || entries.Any(static entry => entry.HasCheckpoint);
        var checkpointTooltip = currentEntry.HasCheckpoint
            ? "Edit checkpoint on the current history state"
            : "Create a checkpoint on the current history state";

        using (ImRaii.Disabled(!canUndo))
        {
            if (DrawAccentIconButton("objectHistoryHeroUndo", FontAwesomeIcon.Undo, "Undo", EditorColors.AccentBlue))
            {
                _ = TryUndoHistory();
            }
        }

        ImGui.SameLine();
        using (ImRaii.Disabled(!canRedo))
        {
            if (DrawAccentIconButton("objectHistoryHeroRedo", FontAwesomeIcon.Redo, "Redo", EditorColors.AccentPurple))
            {
                _ = TryRedoHistory();
            }
        }

        ImGui.SameLine();
        if (DrawAccentIconButton("objectHistoryHeroCheckpoint", FontAwesomeIcon.Flag, checkpointTooltip, EditorColors.AccentYellow))
        {
            CommitPendingHistory();
            QueueHistoryCheckpointEditor(_objectHistoryManager.CurrentStateIndex);
        }

        ImGui.SameLine();
        using (ImRaii.Disabled(!canClear))
        {
            if (DrawAccentIconButton("objectHistoryHeroClear", FontAwesomeIcon.Ban, "Clear recorded history", EditorColors.DimRed))
            {
                CommitPendingHistory();
                _objectHistoryManager.ClearHistory();
                ResetHistoryWorkspaceState();
            }
        }
    }

    private void DrawHistoryTimelineCard(IReadOnlyList<ObjectHistoryEntry> entries)
    {
        var padding = new Vector2(10f * ImGuiHelpers.GlobalScale, 8f * ImGuiHelpers.GlobalScale);
        var itemSpacingY = 2f * ImGuiHelpers.GlobalScale;
        var availableHeight = MathF.Max(1f, ImGui.GetContentRegionAvail().Y - ImGui.GetStyle().ItemSpacing.Y);
        var innerHeight = MathF.Max(1f, availableHeight - (padding.Y * 2f) - (itemSpacingY * 2f));
        var background = EditorColors.ButtonDefault with { W = 0.24f };
        var rounding = 8f * ImGuiHelpers.GlobalScale;

        DrawPanelCard(
            "object-history-timeline-card",
            background,
            EditorColors.AccentPurple with { W = 0.18f },
            rounding,
            padding,
            () =>
            {
                using var childBg = ImRaii.PushColor(ImGuiCol.ChildBg, Vector4.Zero);
                using var child = ObjectScrollList.Begin(
                    "##objectHistoryEntries",
                    new Vector2(0f, innerHeight),
                    CreateOverlayScrollPanelOptions(background, rounding, EditorColors.AccentPurple));
                if (!child)
                {
                    return;
                }

                var topInset = 2f * ImGuiHelpers.GlobalScale;
                ImGui.Dummy(new Vector2(0f, topInset));

                var itemHeight = MathF.Max(52f * ImGuiHelpers.GlobalScale, (ImGui.GetTextLineHeight() * 2f) + (20f * ImGuiHelpers.GlobalScale));
                if (_focusCurrentHistoryEntry)
                {
                    var itemStep = itemHeight + itemSpacingY;
                    var targetScroll = (_objectHistoryManager.CurrentStateIndex * itemStep) - (innerHeight * 0.45f);
                    ImGui.SetScrollY(MathF.Max(0f, targetScroll));
                    _focusCurrentHistoryEntry = false;
                }

                UiVirtualList.Draw(
                    entries,
                    UiVirtualListOptions.Rows(itemHeight, itemSpacingY),
                    (entry, _) => DrawHistoryEntryCard(entry, itemHeight));
            });
    }

    private void DrawHistoryEntryCard(ObjectHistoryEntry entry, float height)
    {
        var currentStateIndex = _objectHistoryManager.CurrentStateIndex;
        var isCurrent = entry.StateIndex == currentStateIndex;
        var isFuture = entry.StateIndex > currentStateIndex;
        var startPos = ImGui.GetCursorPos();
        var insetX = 4f * ImGuiHelpers.GlobalScale;
        var width = MathF.Max(1f, ImGui.GetContentRegionAvail().X - (insetX * 2f));
        var accent = EditorColors.HistoryEntryAccent(entry.Kind);
        var checkpointAccent = EditorColors.AccentYellow;

        ImGui.SetCursorPosX(startPos.X + insetX);
        ListEntryCardInteraction interaction = DrawListEntryCardInteraction(
            $"historyEntry:{entry.StateIndex}",
            false,
            new Vector2(width, height));
        if (interaction.Clicked)
        {
            _ = TryJumpToHistoryState(entry.StateIndex);
        }

        using (var popup = ImRaii.ContextPopupItem($"##historyEntryContext:{entry.StateIndex}"))
        {
            if (popup)
            {
                if (!isCurrent && ImGui.MenuItem("Jump Here"))
                {
                    _ = TryJumpToHistoryState(entry.StateIndex);
                }

                if (ImGui.MenuItem(entry.HasCheckpoint ? "Edit Checkpoint" : "Set Checkpoint"))
                {
                    CommitPendingHistory();
                    QueueHistoryCheckpointEditor(entry.StateIndex);
                }

                if (entry.HasCheckpoint && ImGui.MenuItem("Remove Checkpoint"))
                {
                    CommitPendingHistory();
                    if (_objectHistoryManager.TryClearCheckpoint(entry.StateIndex))
                    {
                        _focusCurrentHistoryEntry = true;
                    }
                }
            }
        }

        var endPos = ImGui.GetCursorPos();
        ImGui.SetCursorPos(new Vector2(startPos.X, endPos.Y));

        var min = interaction.Min;
        var max = interaction.Max;
        var drawList = ImGui.GetWindowDrawList();
        var fill = isCurrent
            ? EditorColors.WithAlpha(accent, 0.18f)
            : isFuture
                ? EditorColors.WithAlpha(EditorColors.ButtonDefault, 0.14f)
                : EditorColors.WithAlpha(EditorColors.ButtonDefault, 0.22f);
        var border = isCurrent
            ? EditorColors.WithAlpha(accent, 0.90f)
            : interaction.Hovered
                ? EditorColors.WithAlpha(accent, isFuture ? 0.34f : 0.58f)
                : EditorColors.WithAlpha(EditorColors.Border, isFuture ? 0.18f : 0.38f);
        var text = EditorColors.WithAlpha(EditorColors.Text, isFuture ? 0.62f : 1f);
        var disabledText = EditorColors.WithAlpha(EditorColors.TextDisabled, isFuture ? 0.56f : 0.88f);
        var rounding = 8f * ImGuiHelpers.GlobalScale;
        var padX = 10f * ImGuiHelpers.GlobalScale;
        var padY = 8f * ImGuiHelpers.GlobalScale;
        var badgeText = isCurrent
            ? "current"
            : entry.IsInitialState
                ? "start"
                : $"#{entry.StateIndex}";
        var badgeAccent = isCurrent ? accent : entry.HasCheckpoint ? checkpointAccent : accent;

        var leftBarAccent = entry.HasCheckpoint ? checkpointAccent : accent;
        DrawListEntryCardFrame(
            drawList,
            min,
            max,
            fill,
            border,
            EditorColors.WithAlpha(leftBarAccent, isCurrent ? 0.98f : 0.58f),
            rounding,
            interaction.Hovered && !isCurrent);

        var badgePaddingX = 7f * ImGuiHelpers.GlobalScale;
        var badgePaddingY = 3f * ImGuiHelpers.GlobalScale;
        var badgeTextSize = ImGui.CalcTextSize(badgeText);
        var badgeMin = new Vector2(max.X - badgeTextSize.X - (badgePaddingX * 2f) - padX, min.Y + padY);
        var badgeMax = new Vector2(max.X - padX, badgeMin.Y + badgeTextSize.Y + (badgePaddingY * 2f));
        drawList.AddRectFilled(badgeMin, badgeMax, ImGui.GetColorU32(EditorColors.WithAlpha(badgeAccent, isCurrent ? 0.28f : 0.18f)), 999f);
        drawList.AddRect(badgeMin, badgeMax, ImGui.GetColorU32(EditorColors.WithAlpha(badgeAccent, 0.68f)), 999f, ImDrawFlags.None, MathF.Max(1f * ImGuiHelpers.GlobalScale, 1f));
        drawList.AddText(new Vector2(badgeMin.X + badgePaddingX, badgeMin.Y + badgePaddingY), ImGui.GetColorU32(text), badgeText);

        var textWidth = MathF.Max(ResolveMinimumCardTextWidth(), badgeMin.X - min.X - (padX * 2f));
        drawList.AddText(new Vector2(min.X + padX, min.Y + padY), ImGui.GetColorU32(text), ClipTextToWidth(entry.Title, textWidth));

        var detail = BuildHistoryEntryDetail(entry, isFuture);
        var metaY = min.Y + padY + ImGui.GetTextLineHeight() + (4f * ImGuiHelpers.GlobalScale);
        drawList.AddText(
            new Vector2(min.X + padX, metaY),
            ImGui.GetColorU32(disabledText),
            ClipTextToWidth(detail, textWidth));

    }

    private bool TryJumpToHistoryState(int stateIndex)
    {
        CommitPendingHistory();
        if (!_historyCoordinator.TryJumpToState(stateIndex))
        {
            return false;
        }

        _focusCurrentHistoryEntry = true;
        return true;
    }

    private void QueueHistoryCheckpointEditor(int stateIndex)
    {
        var entries = _objectHistoryManager.Entries;
        if (stateIndex < 0 || stateIndex >= entries.Count)
        {
            return;
        }

        var entry = entries[stateIndex];
        _historyCheckpointEditStateIndex = stateIndex;
        _historyCheckpointEditLabel = entry.HasCheckpoint
            ? entry.CheckpointLabel ?? string.Empty
            : BuildDefaultHistoryCheckpointLabel(entries);
        _openHistoryCheckpointPopupNextFrame = true;
    }

    private void DrawHistoryCheckpointPopup(IReadOnlyList<ObjectHistoryEntry> entries)
    {
        if (_openHistoryCheckpointPopupNextFrame)
        {
            ImGui.OpenPopup(HistoryCheckpointPopupId);
            _openHistoryCheckpointPopupNextFrame = false;
        }

        using var popup = ImRaii.Popup(HistoryCheckpointPopupId, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        if (!popup)
        {
            return;
        }

        if (!_historyCheckpointEditStateIndex.HasValue
            || _historyCheckpointEditStateIndex.Value < 0
            || _historyCheckpointEditStateIndex.Value >= entries.Count)
        {
            ImGui.CloseCurrentPopup();
            return;
        }

        var stateIndex = _historyCheckpointEditStateIndex.Value;
        var entry = entries[stateIndex];
        var title = entry.HasCheckpoint ? "Edit Checkpoint" : "Set Checkpoint";

        ImGui.TextUnformatted(title);
        ImGui.Separator();
        ImGui.TextDisabled(entry.IsInitialState ? "Starting point" : $"State {stateIndex}");
        using (ImRaiiScope.TextWrapPos(26f * ImGui.GetFontSize()))
        {
            ImGui.TextUnformatted(entry.Title);
        }
        ImGuiHelpers.ScaledDummy(4f);

        ImGui.SetNextItemWidth(MathF.Max(220f * ImGuiHelpers.GlobalScale, 1f));
        ImGui.InputText("##historyCheckpointLabel", ref _historyCheckpointEditLabel, 128);

        var canSave = !string.IsNullOrWhiteSpace(_historyCheckpointEditLabel);
        using (ImRaii.Disabled(!canSave))
        {
            if (ImGui.Button(entry.HasCheckpoint ? "Save Checkpoint" : "Create Checkpoint") && canSave)
            {
                CommitPendingHistory();
                if (_objectHistoryManager.TrySetCheckpoint(stateIndex, _historyCheckpointEditLabel))
                {
                    _focusCurrentHistoryEntry = true;
                    ImGui.CloseCurrentPopup();
                }
            }
        }

        if (entry.HasCheckpoint)
        {
            ImGui.SameLine();
            if (ImGui.Button("Remove"))
            {
                CommitPendingHistory();
                if (_objectHistoryManager.TryClearCheckpoint(stateIndex))
                {
                    _focusCurrentHistoryEntry = true;
                    ImGui.CloseCurrentPopup();
                }
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel"))
        {
            ImGui.CloseCurrentPopup();
        }
    }

    private static string BuildHistoryStatus(IReadOnlyList<ObjectHistoryEntry> entries, ObjectHistoryEntry currentEntry, int checkpointCount)
    {
        var actionCount = Math.Max(0, entries.Count - 1);
        var checkpointLabel = checkpointCount == 1 ? "1 checkpoint" : $"{checkpointCount} checkpoints";
        return $"{actionCount} recorded changes | {checkpointLabel} | current: {currentEntry.Title}";
    }

    private static string BuildHistoryEntryDetail(ObjectHistoryEntry entry, bool isFuture)
    {
        var detailParts = new List<string>(3);
        detailParts.Add(
            entry.Kind.HasValue
                ? ObjectHistoryDescription.GetKindLabel(entry.Kind.Value)
                : "Starting point");

        if (entry.HasCheckpoint)
        {
            detailParts.Add($"checkpoint: {entry.CheckpointLabel}");
        }

        if (isFuture)
        {
            detailParts.Add("redo");
        }

        return string.Join(" | ", detailParts);
    }

    private static string BuildDefaultHistoryCheckpointLabel(IReadOnlyList<ObjectHistoryEntry> entries)
        => $"Checkpoint {entries.Count(static entry => entry.HasCheckpoint) + 1}";

}

