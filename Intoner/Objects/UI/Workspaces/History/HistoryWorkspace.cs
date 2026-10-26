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
using System.Globalization;
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
    private IReadOnlyList<SceneHistoryEntry>? _lastEntries;
    private int _lastCurrentStateIndex = -1;
    private NavigationAvailability _navigation;

    internal readonly record struct NavigationAvailability(int Current, SceneHistoryResult Undo, SceneHistoryResult Redo)
    {
        public bool CanJumpTo(int index) => index == Current || (index < Current ? Undo : Redo).IsSuccess;
        public bool IsBlockedAction(int index)
            => index == Current && Current > 0 && !Undo.IsSuccess || index == Current + 1 && !Redo.IsSuccess;
        public SceneHistoryResult GetBlock(int index) => index <= Current ? Undo : Redo;
        public int GetBlockIndex(int index) => index <= Current ? Current : Current + 1;
    }

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
        if (_historyCoordinator.RefreshContext(_sceneView.GetCurrentLocationContext()))
        {
            _placementValidationService.ClearCache();
        }
    }

    internal bool TryUndoHistory()
        => TryReplayHistory(_historyCoordinator.TryUndo);

    internal bool TryRedoHistory()
        => TryReplayHistory(_historyCoordinator.TryRedo);

    private bool TryReplayHistory(Func<bool> replay)
    {
        _interaction.CommitPendingHistory();

        if (!replay())
        {
            return false;
        }

        _focusCurrentHistoryEntry = true;
        return true;
    }

    internal void DrawToolbarHistoryTooltip(bool undo, Vector4 headingAccent)
    {
        if (IntonerTooltip.IsSuppressed)
        {
            return;
        }

        string heading = undo ? "Undo" : "Redo";
        FontAwesomeIcon icon = undo ? FontAwesomeIcon.Undo : FontAwesomeIcon.Redo;
        ShortcutDefinition shortcut = undo ? EditorShortcuts.Undo : EditorShortcuts.Redo;
        string hintDescription = undo ? "to undo" : "to redo";
        IntonerTooltipKeyHint? keyHint = _shortcuts.GetGesture(shortcut) is { } gesture
            ? new IntonerTooltipKeyHint(gesture, hintDescription)
            : null;
        SceneHistorySnapshot snapshot = _sceneHistoryManager.GetSnapshot();
        bool hasEntry = TryGetToolbarHistoryEntry(snapshot, undo, out SceneHistoryEntry entry);
        string actionLabel = undo ? "Current action" : "Next action";
        string emptyText = undo ? "No change is available to undo." : "No change is available to redo.";
        SceneHistoryResult availability = undo ? snapshot.Undo : snapshot.Redo;
        string blockedReason = hasEntry && !availability.IsSuccess ? availability.Reason : string.Empty;
        Vector4 actionAccent = hasEntry ? EditorColors.HistoryEntryAccent(entry.Kind!.Value) : headingAccent;
        IReadOnlyList<EditorBadge> badges = hasEntry
            ? [EditorBadge.Label(SceneHistoryDescription.GetKindLabel(entry.Kind!.Value), color: actionAccent),
               EditorBadge.Label(SceneHistoryDescription.GetActionTitle(entry.Kind.Value, entry.Title))]
            : [];
        float bodyWidth = hasEntry
            ? ImGui.GetTextLineHeight() + 6f * ImGuiHelpers.GlobalScale
                + MathF.Max(ImGui.CalcTextSize(actionLabel).X,
                    EditorBadgeRenderer.MeasureWidth(badges[0]) + EditorBadgeRenderer.MeasureWidth(badges[1]))
            : ImGui.CalcTextSize(emptyText).X;
        bodyWidth = MathF.Max(bodyWidth, IntonerTooltipContent.MeasureNoticeWidth(FontAwesomeIcon.ExclamationTriangle, blockedReason));
        float contentWidth = MathF.Max(IntonerTooltipContent.MeasureHeaderWidth(icon, heading), bodyWidth);

        if (keyHint is { } hint)
        {
            contentWidth = MathF.Max(contentWidth, IntonerTooltipContent.MeasureKeyHintWidth(hint));
        }

        IntonerTooltip.DrawContentSized(
            () =>
            {
                IntonerTooltipContent.Header(icon, heading, accentOverride: headingAccent);

                if (keyHint is { } hint)
                {
                    IntonerTooltipContent.KeyHint(hint, headingAccent);
                }

                IntonerTooltipContent.Separator();

                if (hasEntry)
                {
                    DrawToolbarHistoryActionTooltip(actionLabel, actionAccent, badges);
                }
                else
                {
                    using var wrap = ImRaii.TextWrapPos(bodyWidth > ImGui.GetContentRegionAvail().X ? 0f : -1f);
                    ImGui.TextDisabled(emptyText);
                }

                if (blockedReason.Length > 0)
                {
                    IntonerTooltipContent.Separator();
                    DrawHistoryWarning(blockedReason);
                }
            },
            contentWidth,
            new IntonerTooltipOptions
            {
                Accent = headingAccent,
                MaxWidth = hasEntry ? 320f : null,
            });
    }

    private static void DrawToolbarHistoryActionTooltip(string label, Vector4 accent, IReadOnlyList<EditorBadge> badges)
    {
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
            accent);

        ImGui.SameLine(0f, 6f * scale);
        using var content = ImRaii.Group();
        ImGui.TextUnformatted(label);
        EditorBadgeRenderer.DrawInline(badges, wrap: true);
    }

    private static void DrawHistoryWarning(string blockedReason)
    {
        IntonerTooltipContent.Notice(FontAwesomeIcon.ExclamationTriangle, blockedReason, ThemeColors.DimRed);
    }

    private void DrawHistoryEntryWarning(IReadOnlyList<SceneHistoryEntry> entries, SceneHistoryEntry entry, bool blockedAction)
    {
        bool undo = entry.StateIndex <= _navigation.Current;
        string heading = (blockedAction, undo) switch
        {
            (false, _) => "State unavailable",
            (_, true) => "Undo blocked",
            _ => "Redo blocked",
        };
        SceneHistoryResult result = _navigation.GetBlock(entry.StateIndex);
        SceneHistoryEntry blocker = entries[_navigation.GetBlockIndex(entry.StateIndex)];
        Vector4 accent = ThemeColors.DimRed;
        float width = MathF.Max(IntonerTooltipContent.MeasureHeaderWidth(FontAwesomeIcon.ExclamationTriangle, heading),
            MathF.Max(IntonerTooltipContent.MeasureItemWidth(FontAwesomeIcon.History, blocker.Title),
                IntonerTooltipContent.MeasureNoticeWidth(FontAwesomeIcon.ExclamationTriangle, result.Reason)));
        IntonerTooltip.DrawContentSized(() =>
        {
            IntonerTooltipContent.Header(FontAwesomeIcon.ExclamationTriangle, heading, accentOverride: accent);
            IntonerTooltipContent.Item(FontAwesomeIcon.History, blockedAction ? blocker.Title : "Blocked action",
                blockedAction ? string.Empty : blocker.Title, EditorColors.HistoryEntryAccent(blocker.Kind));
            IntonerTooltipContent.Separator();
            DrawHistoryWarning(result.Reason);
        }, width, new IntonerTooltipOptions { Accent = accent, MaxWidth = 320f });
    }

    private static bool TryGetToolbarHistoryEntry(SceneHistorySnapshot snapshot, bool undo, out SceneHistoryEntry entry)
    {
        var entries = snapshot.Entries;
        if (entries.Count == 0)
        {
            entry = default;
            return false;
        }

        var currentStateIndex = Math.Clamp(snapshot.CurrentStateIndex, 0, entries.Count - 1);
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
        SceneHistorySnapshot snapshot = _sceneHistoryManager.GetSnapshot();
        var entries = snapshot.Entries;
        int currentStateIndex = snapshot.CurrentStateIndex;
        _navigation = new NavigationAvailability(currentStateIndex, snapshot.Undo, snapshot.Redo);

        if (!ReferenceEquals(entries, _lastEntries) || currentStateIndex != _lastCurrentStateIndex)
        {
            _lastEntries = entries;
            _lastCurrentStateIndex = currentStateIndex;
            _focusCurrentHistoryEntry = true;
        }

        DrawHistoryHero(entries, snapshot.LastResult);
        DrawHistoryTimelineCard(entries);
    }

    private void ResetHistoryWorkspaceState()
    {
        _interaction.Dialog.DismissIfCurrent(HistoryCheckpointDialogKey);
        _interaction.Dialog.DismissIfCurrent(HistoryClearDialogKey);
        _focusCurrentHistoryEntry = true;
    }

    private void DrawHistoryHero(IReadOnlyList<SceneHistoryEntry> entries, SceneHistoryResult lastResult)
    {
        var accent = ThemeColors.AccentPrimary;
        var currentStateIndex = Math.Clamp(_navigation.Current, 0, Math.Max(0, entries.Count - 1));
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

                if (!lastResult.IsSuccess && !string.IsNullOrWhiteSpace(lastResult.Message))
                {
                    ImGui.Spacing();
                    DrawHistoryWarning(lastResult.Message);
                }
            });
    }

    private void DrawHistoryHeroActions(
        IReadOnlyList<SceneHistoryEntry> entries,
        SceneHistoryEntry currentEntry,
        float buttonEdge)
    {
        var canUndo = currentEntry.Kind.HasValue && _navigation.Undo.IsSuccess;
        var canRedo = _navigation.Current + 1 < entries.Count && entries[_navigation.Current + 1].Kind.HasValue && _navigation.Redo.IsSuccess;
        var canClear = entries.Count > 1 || entries.Any(static entry => entry.HasCheckpoint)
            || _navigation.Undo.Outcome == SceneHistoryOutcome.RecoveryRequired;
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
            SceneHistorySnapshot snapshot = _sceneHistoryManager.GetSnapshot();
            OpenHistoryCheckpointDialog(snapshot.Entries, snapshot.CurrentStateIndex);
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
                    var targetScroll = (_navigation.Current * itemStep) - (innerHeight * 0.45f);
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
        var currentStateIndex = _navigation.Current;
        var isCurrent = entry.StateIndex == currentStateIndex;
        var isFuture = entry.StateIndex > currentStateIndex;
        var startPos = ImGui.GetCursorPos();
        var insetX = 4f * ImGuiHelpers.GlobalScale;
        var width = MathF.Max(1f, ImGui.GetContentRegionAvail().X - (insetX * 2f));
        bool isBlocked = _navigation.IsBlockedAction(entry.StateIndex);
        bool unreachable = !_navigation.CanJumpTo(entry.StateIndex);
        bool dimmed = unreachable && !isBlocked;
        var accent = isBlocked ? ThemeColors.DimRed : EditorColors.HistoryEntryAccent(entry.Kind);
        var checkpointAccent = ThemeColors.AccentYellow;

        ImGui.SetCursorPosX(startPos.X + insetX);
        EditorListCard.Interaction interaction = EditorListCard.DrawInteraction(
            $"historyEntry:{entry.StateIndex}",
            false,
            new Vector2(width, height));
        if (interaction.Clicked && !isCurrent && !unreachable)
        {
            _ = TryJumpToHistoryState(entries, entry.StateIndex);
        }

        using (var popup = EditorContextMenu.BeginForLastItem($"##historyEntryContext:{entry.StateIndex}"))
        {
            if (popup)
            {
                if (!isCurrent && EditorContextMenu.DrawItem(FontAwesomeIcon.History, "Jump Here", enabled: !unreachable))
                {
                    _ = TryJumpToHistoryState(entries, entry.StateIndex);
                }

                if (EditorContextMenu.DrawItem(FontAwesomeIcon.Flag, entry.HasCheckpoint ? "Edit Checkpoint" : "Set Checkpoint"))
                {
                    _interaction.CommitPendingHistory();
                    if (ReferenceEquals(entries, _sceneHistoryManager.Entries))
                    {
                        OpenHistoryCheckpointDialog(entries, entry.StateIndex);
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

        if ((isBlocked || unreachable) && interaction.Hovered)
        {
            DrawHistoryEntryWarning(entries, entry, isBlocked);
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

        if (isBlocked)
        {
            fill = ThemeColors.WithAlpha(accent, 0.12f);
            border = ThemeColors.WithAlpha(accent, interaction.Hovered ? 0.90f : 0.70f);
        }
        else if (dimmed)
        {
            fill = ThemeColors.WithAlpha(ThemeColors.ButtonDefault, 0.10f);
            border = ThemeColors.WithAlpha(ThemeColors.Border, 0.16f);
        }

        float contentAlpha = (dimmed, isFuture && !isBlocked) switch
        {
            (true, _) => 0.35f,
            (_, true) => 0.62f,
            _ => 1f,
        };
        float railAlpha = (dimmed, isCurrent || isBlocked) switch
        {
            (true, _) => 0.18f,
            (_, true) => 0.98f,
            _ => 0.58f,
        };
        var text = ThemeColors.WithAlpha(ThemeColors.Text, contentAlpha);
        var rounding = 8f * ImGuiHelpers.GlobalScale;
        var padX = 10f * ImGuiHelpers.GlobalScale;
        var padY = 8f * ImGuiHelpers.GlobalScale;
        var badgeText = (isCurrent, entry.IsInitialState) switch
        {
            (true, _)      => "current",
            (false, true)  => "start",
            (false, false) => $"#{entry.StateIndex}",
        };
        var badgeAccent = !isBlocked && !isCurrent && entry.HasCheckpoint ? checkpointAccent : accent;

        var leftBarAccent = !isBlocked && entry.HasCheckpoint ? checkpointAccent : accent;
        _listCard.DrawFrame(
            drawList,
            min,
            max,
            fill,
            border,
            ThemeColors.WithAlpha(leftBarAccent, railAlpha),
            rounding,
            interaction.Hovered && !isCurrent && !unreachable);

        string time = entry.RecordedAtUtc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        float badgeTop = min.Y + (height - EditorBadgeRenderer.Height) * 0.5f;
        float badgeLeft = EditorBadgeRenderer.DrawRightAligned(
            drawList,
            [EditorBadge.Label(time, FontAwesomeIcon.Clock, badgeAccent)],
            EditorBadge.Label(badgeText, color: badgeAccent),
            max.X - padX,
            badgeTop,
            isCurrent,
            contentAlpha,
            panelSurface: true,
            borderColor: badgeAccent);
        var textWidth = MathF.Max(EditorListCard.MinimumTextWidth, badgeLeft - min.X - (padX * 2f));
        drawList.AddText(new Vector2(min.X + padX, min.Y + padY), ImGui.GetColorU32(text), EditorTextUtility.ClipTextToWidth(entry.Title, textWidth));

        var metaY = min.Y + padY + ImGui.GetTextLineHeight() + (4f * ImGuiHelpers.GlobalScale);
        EditorBadgeRenderer.DrawAt(
            drawList,
            BuildHistoryEntryBadges(entry, isFuture, isBlocked, accent),
            min.X + padX,
            metaY,
            isCurrent,
            contentAlpha,
            badgeLeft - (6f * ImGuiHelpers.GlobalScale),
            panelSurface: true,
            borderColor: accent);
    }

    private bool TryJumpToHistoryState(IReadOnlyList<SceneHistoryEntry> entries, int stateIndex)
        => TryReplayHistory(() => _historyCoordinator.TryJumpToState(stateIndex, entries));

    private void OpenHistoryCheckpointDialog(IReadOnlyList<SceneHistoryEntry> entries, int stateIndex)
    {
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
        bool updated = label is null
            ? _sceneHistoryManager.TryClearCheckpoint(stateIndex, entries)
            : _sceneHistoryManager.TrySetCheckpoint(stateIndex, label, entries);
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
        bool isBlocked,
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

        if (isBlocked)
        {
            badges.Add(EditorBadge.Label("Blocked", FontAwesomeIcon.ExclamationTriangle, accent));
        }
        else if (isFuture)
        {
            badges.Add(EditorBadge.Label("Redo", FontAwesomeIcon.Redo, accent));
        }

        return badges;
    }

    private static string BuildDefaultHistoryCheckpointLabel(IReadOnlyList<SceneHistoryEntry> entries)
        => $"Checkpoint {entries.Count(static entry => entry.HasCheckpoint) + 1}";
}
