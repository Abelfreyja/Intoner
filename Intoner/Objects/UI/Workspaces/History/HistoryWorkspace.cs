using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Intoner.Objects.Runtime;
using Intoner.Objects.UI.Components;
using Intoner.Objects.UI.Services;
using Intoner.Scene;
using Intoner.Services.Input;
using Intoner.UI.Performance;
using System.Numerics;

namespace Intoner.Objects.UI;

internal sealed class HistoryWorkspace
{
    private readonly IHistoryCoordinator _historyCoordinator;
    private readonly ISceneHistoryManager _sceneHistoryManager;
    private readonly IObjectSceneView _sceneView;
    private readonly PlacementValidationService _placementValidationService;
    private readonly IShortcutService _shortcuts;
    private readonly EditorInteraction _interaction;
    private readonly EditorListCard _listCard;
    private readonly EditorOverlayLayer _editorOverlayLayer;
    private const string HistoryCheckpointDialogKey = "history-checkpoint";
    private const string HistoryClearDialogKey = "history-clear";
    private bool _focusCurrentHistoryEntry = true;

    public HistoryWorkspace(
        IHistoryCoordinator historyCoordinator,
        ISceneHistoryManager sceneHistoryManager,
        IObjectSceneView sceneView,
        PlacementValidationService placementValidationService,
        IShortcutService shortcuts,
        EditorInteraction interaction,
        EditorListCard listCard,
        EditorOverlayLayer editorOverlayLayer)
    {
        _historyCoordinator         = historyCoordinator;
        _sceneHistoryManager        = sceneHistoryManager;
        _sceneView                  = sceneView;
        _placementValidationService = placementValidationService;
        _shortcuts                  = shortcuts;
        _interaction                = interaction;
        _listCard                   = listCard;
        _editorOverlayLayer         = editorOverlayLayer;
    }

    public void FocusCurrentEntry()
        => _focusCurrentHistoryEntry = true;

    internal void RefreshHistoryContext()
    {
        if (_historyCoordinator.RefreshContext(_sceneView.GetCurrentLocationContext(), _interaction.CommitPendingHistory))
        {
            _placementValidationService.ClearCache();
            ResetHistoryWorkspaceState();
        }
    }

    internal bool TryUndoHistory()
    {
        _interaction.CommitPendingHistory();
        if (!_historyCoordinator.TryUndo())
        {
            return false;
        }

        _focusCurrentHistoryEntry = true;
        return true;
    }

    internal bool TryRedoHistory()
    {
        _interaction.CommitPendingHistory();
        if (!_historyCoordinator.TryRedo())
        {
            return false;
        }

        _focusCurrentHistoryEntry = true;
        return true;
    }

    internal void DrawToolbarHistoryTooltip(bool undo, Vector4 headingAccent)
    {
        IntonerTooltip.Draw(
            () =>
            {
                var heading = undo ? "Undo" : "Redo";
                IntonerTooltipContent.Heading(
                    undo ? FontAwesomeIcon.Undo : FontAwesomeIcon.Redo,
                    heading,
                    accentOverride: headingAccent,
                    separatorTopSpacing: 4f);

                IntonerTooltipContent.KeyHint(
                    "Press",
                    _shortcuts.GetGesture(undo ? EditorShortcuts.Undo : EditorShortcuts.Redo),
                    undo ? "to undo" : "to redo",
                    headingAccent);
                IntonerTooltipContent.Separator();

                if (!TryGetToolbarHistoryEntry(undo, out var entry))
                {
                    ImGui.TextDisabled(undo
                        ? "No change is available to undo."
                        : "No change is available to redo.");
                    return;
                }

                DrawToolbarHistoryActionTooltip(entry, undo);
            },
            new IntonerTooltipOptions
            {
                Accent = headingAccent,
            });
    }

    private static void DrawToolbarHistoryActionTooltip(SceneHistoryEntry entry, bool undo)
    {
        SceneHistoryKind kind = entry.Kind!.Value;
        Vector4 actionAccent = EditorColors.HistoryEntryAccent(kind);
        float scale = ImGuiHelpers.GlobalScale;
        float contentHeight = ImGui.GetTextLineHeight()
                            + ImGui.GetStyle().ItemSpacing.Y
                            + EditorBadgeRenderer.Height;
        float iconWidth = ImGui.GetTextLineHeight();
        Vector2 iconMin = ImGui.GetCursorScreenPos();
        Vector2 iconMax = iconMin + new Vector2(iconWidth, contentHeight);

        ImGui.Dummy(new Vector2(iconWidth, contentHeight));
        EditorIcon.DrawCentered(
            ImGui.GetWindowDrawList(),
            FontAwesomeIcon.History,
            iconMin,
            iconMax,
            actionAccent);

        ImGui.SameLine(0f, 6f * scale);
        using var content = ImRaii.Group();
        ImGui.TextUnformatted(undo ? "Current action" : "Next action");
        EditorBadgeRenderer.DrawInline(
        [
            EditorBadge.Label(SceneHistoryDescription.GetKindLabel(kind), color: actionAccent),
            EditorBadge.Label(SceneHistoryDescription.GetActionTitle(kind, entry.Title)),
        ]);
    }

    private bool TryGetToolbarHistoryEntry(bool undo, out SceneHistoryEntry entry)
    {
        var entries = _sceneHistoryManager.Entries;
        if (entries.Count == 0)
        {
            entry = default;
            return false;
        }

        var currentStateIndex = Math.Clamp(_sceneHistoryManager.CurrentStateIndex, 0, entries.Count - 1);
        var targetStateIndex = undo ? currentStateIndex : currentStateIndex + 1;
        if (targetStateIndex <= 0 || targetStateIndex >= entries.Count)
        {
            entry = default;
            return false;
        }

        entry = entries[targetStateIndex];
        return entry.Kind.HasValue;
    }

    internal void DrawHistoryWorkspace()
    {
        var entries = _sceneHistoryManager.Entries;
        DrawHistoryHero(entries);
        DrawHistoryTimelineCard(entries);
    }

    private void ResetHistoryWorkspaceState()
    {
        _interaction.Dialog.DismissIfCurrent(HistoryCheckpointDialogKey);
        _interaction.Dialog.DismissIfCurrent(HistoryClearDialogKey);
        _focusCurrentHistoryEntry = true;
    }

    private void DrawHistoryHero(IReadOnlyList<SceneHistoryEntry> entries)
    {
        var accent = ThemeColors.AccentPrimary;
        var currentStateIndex = Math.Clamp(_sceneHistoryManager.CurrentStateIndex, 0, Math.Max(0, entries.Count - 1));
        var checkpointCount = entries.Count(static entry => entry.HasCheckpoint);
        var currentEntry = entries[currentStateIndex];
        float actionButtonEdge = EditorIconButton.MeasureMaxEdge(
            FontAwesomeIcon.Undo,
            FontAwesomeIcon.Redo,
            FontAwesomeIcon.Flag,
            FontAwesomeIcon.Ban);
        float actionWidth = EditorLayout.ResolveActionStripWidth(actionButtonEdge, 4);

        EditorCard.DrawPanelCard(
            "object-history-hero",
            ThemeColors.ButtonDefault with { W = 0.30f },
            accent with { W = 0.24f },
            8f * ImGuiHelpers.GlobalScale,
            new Vector2(10f * ImGuiHelpers.GlobalScale, 8f * ImGuiHelpers.GlobalScale),
            () =>
            {
                EditorCard.DrawCardHeader(
                    "objectHistoryHeroHeader",
                    FontAwesomeIcon.History,
                    "History",
                    BuildHistoryBadges(entries, currentEntry, checkpointCount),
                    accent,
                    () => DrawHistoryHeroActions(entries, currentEntry, actionButtonEdge),
                    actionWidth);
            });
    }

    private void DrawHistoryHeroActions(
        IReadOnlyList<SceneHistoryEntry> entries,
        SceneHistoryEntry currentEntry,
        float buttonEdge)
    {
        var canUndo = _sceneHistoryManager.UndoActionKind is not null;
        var canRedo = _sceneHistoryManager.RedoActionKind is not null;
        var canClear = entries.Count > 1 || entries.Any(static entry => entry.HasCheckpoint);
        var checkpointTooltip = currentEntry.HasCheckpoint
            ? "Edit checkpoint on the current history state"
            : "Create a checkpoint on the current history state";

        using (ImRaii.Disabled(!canUndo))
        {
            if (EditorIconButton.DrawAccent(
                    "objectHistoryHeroUndo", FontAwesomeIcon.Undo, "Undo", ThemeColors.AccentBlue, buttonEdge,
                    drawTooltip: () => DrawToolbarHistoryTooltip(true, ThemeColors.AccentBlue)))
            {
                _ = TryUndoHistory();
            }
        }

        ImGui.SameLine();
        using (ImRaii.Disabled(!canRedo))
        {
            if (EditorIconButton.DrawAccent(
                    "objectHistoryHeroRedo", FontAwesomeIcon.Redo, "Redo", ThemeColors.AccentPrimary, buttonEdge,
                    drawTooltip: () => DrawToolbarHistoryTooltip(false, ThemeColors.AccentPrimary)))
            {
                _ = TryRedoHistory();
            }
        }

        ImGui.SameLine();
        if (EditorIconButton.DrawAccent("objectHistoryHeroCheckpoint", FontAwesomeIcon.Flag, checkpointTooltip, ThemeColors.AccentYellow, buttonEdge))
        {
            _interaction.CommitPendingHistory();
            OpenHistoryCheckpointDialog(_sceneHistoryManager.CurrentStateIndex);
        }

        ImGui.SameLine();
        using (ImRaii.Disabled(!canClear))
        {
            if (EditorIconButton.DrawAccent(
                    "objectHistoryHeroClear",
                    FontAwesomeIcon.Ban,
                    "Clear recorded history",
                    ThemeColors.DimRed,
                    buttonEdge,
                    tooltipTitleColor: ThemeColors.DimRed))
            {
                OpenClearHistoryDialog();
            }
        }
    }

    private void OpenClearHistoryDialog()
    {
        _interaction.OpenDialog(EditorDialog.Request.Confirmation(
            HistoryClearDialogKey,
            "Clear History",
            "Clear History",
            ClearRecordedHistory) with
        {
            Icon = FontAwesomeIcon.Ban,
            ConfirmIcon = FontAwesomeIcon.Trash,
            Accent = ThemeColors.DimRed,
            Description = "This permanently removes all undo, redo, and checkpoint entries. Placed objects are not changed.",
        });
    }

    private void ClearRecordedHistory()
    {
        _interaction.CommitPendingHistory();
        _sceneHistoryManager.ClearHistory();
        ResetHistoryWorkspaceState();
    }

    private void DrawHistoryTimelineCard(IReadOnlyList<SceneHistoryEntry> entries)
    {
        var padding = new Vector2(10f * ImGuiHelpers.GlobalScale, 8f * ImGuiHelpers.GlobalScale);
        var itemSpacingY = 2f * ImGuiHelpers.GlobalScale;
        var availableHeight = MathF.Max(1f, ImGui.GetContentRegionAvail().Y - ImGui.GetStyle().ItemSpacing.Y);
        var innerHeight = MathF.Max(1f, availableHeight - (padding.Y * 2f) - (itemSpacingY * 2f));
        var background = ThemeColors.ButtonDefault with { W = 0.24f };
        var rounding = 8f * ImGuiHelpers.GlobalScale;

        EditorCard.DrawPanelCard(
            "object-history-timeline-card",
            background,
            ThemeColors.AccentPrimary with { W = 0.18f },
            rounding,
            padding,
            () =>
            {
                using var childBg = ImRaii.PushColor(ImGuiCol.ChildBg, Vector4.Zero);
                using var child = EditorScrollList.Begin(
                    "##objectHistoryEntries",
                    new Vector2(0f, innerHeight),
                    _editorOverlayLayer.CreateScrollPanelOptions(background, rounding, ThemeColors.AccentPrimary));
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
                    var targetScroll = (_sceneHistoryManager.CurrentStateIndex * itemStep) - (innerHeight * 0.45f);
                    ImGui.SetScrollY(MathF.Max(0f, targetScroll));
                    _focusCurrentHistoryEntry = false;
                }

                UiVirtualList.Draw(
                    entries,
                    UiVirtualListOptions.Rows(itemHeight, itemSpacingY),
                    (entry, _) => DrawHistoryEntryCard(entries, entry, itemHeight));
            });
    }

    private void DrawHistoryEntryCard(IReadOnlyList<SceneHistoryEntry> entries, SceneHistoryEntry entry, float height)
    {
        var currentStateIndex = _sceneHistoryManager.CurrentStateIndex;
        var isCurrent = entry.StateIndex == currentStateIndex;
        var isFuture = entry.StateIndex > currentStateIndex;
        var startPos = ImGui.GetCursorPos();
        var insetX = 4f * ImGuiHelpers.GlobalScale;
        var width = MathF.Max(1f, ImGui.GetContentRegionAvail().X - (insetX * 2f));
        var accent = EditorColors.HistoryEntryAccent(entry.Kind);
        var checkpointAccent = ThemeColors.AccentYellow;

        ImGui.SetCursorPosX(startPos.X + insetX);
        EditorListCard.Interaction interaction = EditorListCard.DrawInteraction(
            $"historyEntry:{entry.StateIndex}",
            false,
            new Vector2(width, height));
        if (interaction.Clicked)
        {
            _ = TryJumpToHistoryState(entries, entry.StateIndex);
        }

        using (var popup = EditorContextMenu.BeginForLastItem($"##historyEntryContext:{entry.StateIndex}"))
        {
            if (popup)
            {
                if (!isCurrent && EditorContextMenu.DrawItem(FontAwesomeIcon.History, "Jump Here"))
                {
                    _ = TryJumpToHistoryState(entries, entry.StateIndex);
                }

                if (EditorContextMenu.DrawItem(FontAwesomeIcon.Flag, entry.HasCheckpoint ? "Edit Checkpoint" : "Set Checkpoint"))
                {
                    _interaction.CommitPendingHistory();
                    if (ReferenceEquals(entries, _sceneHistoryManager.Entries))
                    {
                        OpenHistoryCheckpointDialog(entry.StateIndex);
                    }
                }

                if (entry.HasCheckpoint)
                {
                    EditorContextMenu.DrawSeparator();
                    if (EditorContextMenu.DrawItem(
                            FontAwesomeIcon.TimesCircle,
                            "Remove Checkpoint",
                            color: ThemeColors.DimRed))
                    {
                        _ = TryUpdateHistoryCheckpoint(entries, entry.StateIndex, null);
                    }
                }
            }
        }

        var endPos = ImGui.GetCursorPos();
        ImGui.SetCursorPos(new Vector2(startPos.X, endPos.Y));

        var min = interaction.Min;
        var max = interaction.Max;
        var drawList = ImGui.GetWindowDrawList();
        var fill = (isCurrent, isFuture) switch
        {
            (true, _)     => ThemeColors.WithAlpha(accent, 0.18f),
            (false, true) => ThemeColors.WithAlpha(ThemeColors.ButtonDefault, 0.14f),
            _             => ThemeColors.WithAlpha(ThemeColors.ButtonDefault, 0.22f),
        };
        var border = (isCurrent, interaction.Hovered, isFuture) switch
        {
            (true, _, _)         => ThemeColors.WithAlpha(accent, 0.90f),
            (false, true, true)  => ThemeColors.WithAlpha(accent, 0.34f),
            (false, true, false) => ThemeColors.WithAlpha(accent, 0.58f),
            (false, false, true) => ThemeColors.WithAlpha(ThemeColors.Border, 0.18f),
            _                    => ThemeColors.WithAlpha(ThemeColors.Border, 0.38f),
        };
        var text = ThemeColors.WithAlpha(ThemeColors.Text, isFuture ? 0.62f : 1f);
        var rounding = 8f * ImGuiHelpers.GlobalScale;
        var padX = 10f * ImGuiHelpers.GlobalScale;
        var padY = 8f * ImGuiHelpers.GlobalScale;
        var badgeText = (isCurrent, entry.IsInitialState) switch
        {
            (true, _)      => "current",
            (false, true)  => "start",
            (false, false) => $"#{entry.StateIndex}",
        };
        var badgeAccent = !isCurrent && entry.HasCheckpoint ? checkpointAccent : accent;

        var leftBarAccent = entry.HasCheckpoint ? checkpointAccent : accent;
        _listCard.DrawFrame(
            drawList,
            min,
            max,
            fill,
            border,
            ThemeColors.WithAlpha(leftBarAccent, isCurrent ? 0.98f : 0.58f),
            rounding,
            interaction.Hovered && !isCurrent);

        float badgeLeft = EditorBadgeRenderer.DrawRightAligned(
            drawList,
            null,
            EditorBadge.Label(badgeText, color: badgeAccent),
            max.X - padX,
            min.Y + padY,
            isCurrent,
            isFuture ? 0.62f : 1f,
            panelSurface: true,
            borderColor: badgeAccent);
        var textWidth = MathF.Max(EditorListCard.MinimumTextWidth, badgeLeft - min.X - (padX * 2f));
        drawList.AddText(new Vector2(min.X + padX, min.Y + padY), ImGui.GetColorU32(text), EditorTextUtility.ClipTextToWidth(entry.Title, textWidth));

        var metaY = min.Y + padY + ImGui.GetTextLineHeight() + (4f * ImGuiHelpers.GlobalScale);
        EditorBadgeRenderer.DrawAt(
            drawList,
            BuildHistoryEntryBadges(entry, isFuture, accent),
            min.X + padX,
            metaY,
            isCurrent,
            isFuture ? 0.62f : 1f,
            badgeLeft - (6f * ImGuiHelpers.GlobalScale),
            panelSurface: true,
            borderColor: accent);
    }

    private bool TryJumpToHistoryState(IReadOnlyList<SceneHistoryEntry> entries, int stateIndex)
    {
        _interaction.CommitPendingHistory();
        if (!ReferenceEquals(entries, _sceneHistoryManager.Entries)
         || !_historyCoordinator.TryJumpToState(stateIndex))
        {
            return false;
        }

        _focusCurrentHistoryEntry = true;
        return true;
    }

    private void OpenHistoryCheckpointDialog(int stateIndex)
    {
        var entries = _sceneHistoryManager.Entries;
        if (stateIndex < 0 || stateIndex >= entries.Count)
        {
            return;
        }

        var entry = entries[stateIndex];
        string initialLabel = entry.HasCheckpoint
            ? entry.CheckpointLabel ?? string.Empty
            : BuildDefaultHistoryCheckpointLabel(entries);
        _interaction.OpenDialog(EditorDialog.Request.TextInput(
            HistoryCheckpointDialogKey,
            entry.HasCheckpoint ? "Edit Checkpoint" : "Set Checkpoint",
            entry.HasCheckpoint ? "Save Checkpoint" : "Create Checkpoint",
            label => TryUpdateHistoryCheckpoint(entries, stateIndex, label)) with
        {
            Icon = FontAwesomeIcon.Flag,
            ConfirmIcon = entry.HasCheckpoint ? FontAwesomeIcon.Save : FontAwesomeIcon.Flag,
            Accent = ThemeColors.AccentYellow,
            InitialValue = initialLabel,
            Placeholder = "checkpoint name",
            Detail = entry.IsInitialState ? "Starting point" : $"State {stateIndex}",
            Description = entry.Title,
            MaxLength = 128,
            FailureMessage = "The checkpoint could not be updated. Reopen it from the current timeline.",
            Validate = static label => string.IsNullOrWhiteSpace(label) ? "Enter a checkpoint name." : null,
            Secondary = entry.HasCheckpoint
                ? new EditorDialog.SecondaryAction(
                    "Remove",
                    FontAwesomeIcon.Trash,
                    ThemeColors.DimRed,
                    () => TryUpdateHistoryCheckpoint(entries, stateIndex, null))
                : null,
        });
    }

    private bool TryUpdateHistoryCheckpoint(IReadOnlyList<SceneHistoryEntry> entries, int stateIndex, string? label)
    {
        _interaction.CommitPendingHistory();
        if (!ReferenceEquals(entries, _sceneHistoryManager.Entries))
        {
            return false;
        }

        bool updated = label is null
            ? _sceneHistoryManager.TryClearCheckpoint(stateIndex)
            : _sceneHistoryManager.TrySetCheckpoint(stateIndex, label);
        if (!updated)
        {
            return false;
        }

        _focusCurrentHistoryEntry = true;
        return true;
    }

    private static IReadOnlyList<EditorBadge> BuildHistoryBadges(
        IReadOnlyList<SceneHistoryEntry> entries,
        SceneHistoryEntry currentEntry,
        int checkpointCount)
    {
        int actionCount = Math.Max(0, entries.Count - 1);
        return
        [
            EditorBadge.Count(FontAwesomeIcon.History, actionCount, "change", "changes"),
            EditorBadge.Count(FontAwesomeIcon.Flag, checkpointCount, "checkpoint", "checkpoints"),
            EditorBadge.Label(currentEntry.Title, FontAwesomeIcon.MousePointer),
        ];
    }

    private static IReadOnlyList<EditorBadge> BuildHistoryEntryBadges(
        SceneHistoryEntry entry,
        bool isFuture,
        Vector4 accent)
    {
        List<EditorBadge> badges = new(3)
        {
            EditorBadge.Label(
                entry.Kind.HasValue
                    ? SceneHistoryDescription.GetKindLabel(entry.Kind.Value)
                    : "Starting point",
                FontAwesomeIcon.History,
                accent),
        };

        if (entry.HasCheckpoint)
        {
            badges.Add(EditorBadge.Label(entry.CheckpointLabel!, FontAwesomeIcon.Flag, ThemeColors.AccentYellow));
        }

        if (isFuture)
        {
            badges.Add(EditorBadge.Label("Redo", FontAwesomeIcon.Redo, accent));
        }

        return badges;
    }

    private static string BuildDefaultHistoryCheckpointLabel(IReadOnlyList<SceneHistoryEntry> entries)
        => $"Checkpoint {entries.Count(static entry => entry.HasCheckpoint) + 1}";
}
