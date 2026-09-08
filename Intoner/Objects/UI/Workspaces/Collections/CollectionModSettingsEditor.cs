using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Intoner.Objects.Collections;
using Intoner.Objects.UI.Components;
using Intoner.Objects.Utils;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Intoner.Objects.UI;

internal sealed class CollectionModSettingsEditor
{
    private readonly ILogger<CollectionModSettingsEditor> _logger;
    private readonly IObjectCollectionManager _objectCollectionManager;
    private readonly EditorOverlayLayer _editorOverlayLayer;
    private const string UnnamedModGroupLabel = "(unnamed group)";
    private const string UnnamedModOptionLabel = "(unnamed option)";
    private const float ObjectCollectionModSettingsTwoColumnWidth = 700f;
    private const float ObjectCollectionModSettingsBodyTopPadding = 6f;
    private const float ObjectCollectionModSettingsBodyRightPadding = 8f;
    private const float ObjectCollectionModSettingsBodyBottomPadding = 8f;
    private const float ObjectCollectionModSettingsBodyLeftPadding = 12f;
    private const float ObjectCollectionModSettingsToolbarHeight = 34f;
    private const float ObjectCollectionModSettingsToolbarGap = 5f;
    private const float ObjectCollectionModSettingsGridMaxHeight = 260f;
    private const float ObjectCollectionModSettingRowHeight = 33f;
    private const float ObjectCollectionModSettingRowGap = 3f;
    private const float ObjectCollectionModSettingsMessageHeight = 39f;
    private readonly Dictionary<string, ObjectCollectionModSettingsPanelState> _collectionModSettingsPanelStates =
        new(StringComparer.OrdinalIgnoreCase);

    public CollectionModSettingsEditor(
        ILogger<CollectionModSettingsEditor> logger,
        IObjectCollectionManager objectCollectionManager,
        EditorOverlayLayer editorOverlayLayer)
    {
        _logger                  = logger;
        _objectCollectionManager = objectCollectionManager;
        _editorOverlayLayer      = editorOverlayLayer;
    }

    public void ForgetState(string key)
        => _collectionModSettingsPanelStates.Remove(key);

    internal sealed class ObjectCollectionModSettingsPanelState
    {
        public string Filter = string.Empty;
        public bool ChangedOnly;
        public ObjectCollectionModSettingsView? Source;
        public string AppliedFilter = string.Empty;
        public bool AppliedChangedOnly;
        public List<int> VisibleGroupIndices = [];
    }

    [StructLayout(LayoutKind.Auto)]
    internal readonly record struct ModSettingRowLayout(
        Vector2 Min,
        Vector2 Max,
        Vector2 LabelPos,
        Vector2 ControlPos,
        Vector2 ResetPos,
        Vector2 EndCursor,
        float LabelWidth,
        float ControlWidth,
        float ResetButtonSize);

    private static float ResolveObjectCollectionModSettingsPanelHeight(
        float width,
        ObjectCollectionModSettingsView settingsView,
        IReadOnlyList<int> visibleGroupIndices,
        bool showToolbar)
    {
        if (!showToolbar)
        {
            return EditorLayout.Scaled(ObjectCollectionModSettingsMessageHeight);
        }

        float contentWidth = MathF.Max(
            1f,
            width - EditorLayout.Scaled(
                ObjectCollectionModSettingsBodyLeftPadding
              + ObjectCollectionModSettingsBodyRightPadding));
        float gridHeight;
        if (settingsView.Groups.Count == 0)
        {
            gridHeight = EditorLayout.Scaled(ObjectCollectionModSettingsMessageHeight);
        }
        else if (visibleGroupIndices.Count == 0)
        {
            gridHeight = EditorLayout.Scaled(ObjectCollectionModSettingRowHeight);
        }
        else
        {
            gridHeight = MathF.Min(
                EditorLayout.Scaled(ObjectCollectionModSettingsGridMaxHeight),
                ResolveObjectCollectionModSettingsGridNaturalHeight(contentWidth, visibleGroupIndices.Count));
        }

        return EditorLayout.Scaled(
            ObjectCollectionModSettingsBodyTopPadding
          + ObjectCollectionModSettingsToolbarHeight
          + ObjectCollectionModSettingsToolbarGap
          + ObjectCollectionModSettingsBodyBottomPadding)
          + gridHeight;
    }

    internal bool DrawObjectCollectionModSettingsPanel(
        ObjectCollectionSnapshot collection,
        ObjectCollectionModSettings entry,
        int index,
        ObjectCollectionModSettingsView settingsView,
        int changedCount)
    {
        string stateKey = BuildObjectCollectionModRowKey(collection.Record.CollectionId, entry);
        ObjectCollectionModSettingsPanelState state = GetObjectCollectionModSettingsPanelState(stateKey);
        if (changedCount == 0)
        {
            state.ChangedOnly = false;
        }

        // measure and draw the same filtered rows, applying toolbar changes next frame
        IReadOnlyList<int> visibleGroupIndices = GetVisibleObjectCollectionModSettingIndices(settingsView, state);
        bool showToolbar = settingsView.Groups.Count > 0 || entry.Settings.Count > 0;
        using var panelId = ImRaii.PushId(stateKey);
        Vector2 startCursor = ImGui.GetCursorPos();
        Vector2 min = ImGui.GetCursorScreenPos();
        float width = EditorLayout.Positive(ImGui.GetContentRegionAvail().X);
        float height = ResolveObjectCollectionModSettingsPanelHeight(
            width,
            settingsView,
            visibleGroupIndices,
            showToolbar);
        Vector2 max = min + new Vector2(width, height);
        ImGui.Dummy(new Vector2(width, height));
        Vector2 endCursor = ImGui.GetCursorPos();
        ImDrawListPtr drawList = ImGui.GetWindowDrawList();
        Vector4 settingsBackground = CollectionPanel.CollectionModStyle.Settings;
        settingsBackground.W *= entry.Enabled ? 1f : 0.72f;
        drawList.AddRectFilled(
            min,
            max,
            ImGui.GetColorU32(settingsBackground));
        drawList.AddLine(
            min,
            new Vector2(max.X, min.Y),
            ImGui.GetColorU32(CollectionPanel.ObjectCollectionPanelStyle.Divider),
            EditorLayout.Scaled(1f));
        drawList.AddRectFilled(
            min,
            new Vector2(min.X + EditorLayout.Scaled(3f), max.Y),
            ImGui.GetColorU32(ThemeColors.AccentPrimary with { W = entry.Enabled ? 0.42f : 0.24f }));

        ImGui.SetCursorPos(new Vector2(
            startCursor.X + EditorLayout.Scaled(ObjectCollectionModSettingsBodyLeftPadding),
            startCursor.Y + EditorLayout.Scaled(ObjectCollectionModSettingsBodyTopPadding)));
        bool changed;
        if (showToolbar)
        {
            float contentWidth = EditorLayout.Positive(
                width - EditorLayout.Scaled(
                    ObjectCollectionModSettingsBodyLeftPadding
                  + ObjectCollectionModSettingsBodyRightPadding));
            changed = false;
            using (ImRaii.PushStyle(ImGuiStyleVar.CellPadding, Vector2.Zero))
            using (var content = ImRaii.Table(
                       "##objectCollectionModSettingsBody",
                       1,
                       ImGuiTableFlags.SizingStretchSame
                     | ImGuiTableFlags.NoPadInnerX
                     | ImGuiTableFlags.NoPadOuterX,
                       new Vector2(contentWidth, 0f)))
            {
                if (content)
                {
                    ImGui.TableSetupColumn("Content", ImGuiTableColumnFlags.WidthStretch);
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    changed = DrawObjectCollectionModSettingsContent(
                        collection,
                        entry,
                        index,
                        settingsView,
                        changedCount,
                        state);
                }
            }
        }
        else
        {
            string message = settingsView.ResolveState == ObjectCollectionResolveState.Ready
                ? "No editable settings for this mod."
                : settingsView.StatusText;
            DrawObjectCollectionModSettingsMessage(
                message,
                width - EditorLayout.Scaled(ObjectCollectionModSettingsBodyLeftPadding + ObjectCollectionModSettingsBodyRightPadding));
            changed = false;
        }

        ImGui.SetCursorPos(new Vector2(startCursor.X, endCursor.Y));
        return changed;
    }

    private bool DrawObjectCollectionModSettingsContent(
        ObjectCollectionSnapshot collection,
        ObjectCollectionModSettings entry,
        int index,
        ObjectCollectionModSettingsView settingsView,
        int changedCount,
        ObjectCollectionModSettingsPanelState state)
    {
        bool changed = DrawObjectCollectionModSettingsToolbar(
                collection,
                index,
                settingsView.Groups.Count,
                changedCount,
                CanResetObjectCollectionModSettings(entry),
                state);
        using var disabled = ImRaii.Disabled(changed);

        if (settingsView.Groups.Count == 0)
        {
            DrawObjectCollectionModSettingsMessage(
                settingsView.ResolveState == ObjectCollectionResolveState.Ready
                    ? "No editable settings for this mod."
                    : settingsView.StatusText,
                ImGui.GetContentRegionAvail().X);
            return changed;
        }

        IReadOnlyList<int> visibleGroupIndices = state.VisibleGroupIndices;
        if (visibleGroupIndices.Count == 0)
        {
            DrawObjectCollectionModSettingsEmptyState(
                state.AppliedChangedOnly ? "No changed settings match this filter." : "No settings match this filter.");
            return changed;
        }

        return DrawObjectCollectionModSettingsGrid(
            collection,
            index,
            settingsView,
            visibleGroupIndices,
            entry.Enabled) || changed;
    }

    private ObjectCollectionModSettingsPanelState GetObjectCollectionModSettingsPanelState(string key)
    {
        if (_collectionModSettingsPanelStates.TryGetValue(key, out ObjectCollectionModSettingsPanelState? state))
        {
            return state;
        }

        state = new ObjectCollectionModSettingsPanelState();
        _collectionModSettingsPanelStates.Add(key, state);
        return state;
    }

    private bool DrawObjectCollectionModSettingsToolbar(
        ObjectCollectionSnapshot collection,
        int entryIndex,
        int settingCount,
        int changedCount,
        bool canReset,
        ObjectCollectionModSettingsPanelState state)
    {
        float startX = ImGui.GetCursorPosX();
        float rowStartY = ImGui.GetCursorPosY();
        float contentWidth = ImGui.GetContentRegionAvail().X;
        float toolbarHeight = EditorLayout.Scaled(ObjectCollectionModSettingsToolbarHeight);
        float modeHeight = EditorLayout.Scaled(26f);
        string settingCountText = settingCount.ToString(CultureInfo.InvariantCulture);
        string changedCountText = changedCount.ToString(CultureInfo.InvariantCulture);
        float allWidth = ImGui.CalcTextSize($"All {settingCountText}").X + EditorLayout.Scaled(16f);
        float changedWidth = ImGui.CalcTextSize($"Changed {changedCountText}").X + EditorLayout.Scaled(16f);
        float modeWidth = allWidth + changedWidth;
        const int segmentCount = 2;

        ImGui.SetCursorPos(new Vector2(
            startX,
            rowStartY + ((toolbarHeight - modeHeight) * 0.5f)));
        if (EditorSegmentedControl.DrawCompactSegment(
                "##objectCollectionModSettingsAll",
                "All",
                settingCountText,
                !state.ChangedOnly,
                enabled: true,
                "Show all mod settings",
                ThemeColors.AccentPrimary,
                new Vector2(allWidth, modeHeight),
                0,
                segmentCount))
        {
            state.ChangedOnly = false;
        }

        ImGui.SameLine(0f, 0f);
        if (EditorSegmentedControl.DrawCompactSegment(
                "##objectCollectionModSettingsChanged",
                "Changed",
                changedCountText,
                state.ChangedOnly,
                enabled: changedCount > 0,
                "Show only settings changed from the mod defaults",
                ThemeColors.AccentPrimary,
                new Vector2(changedWidth, modeHeight),
                1,
                segmentCount))
        {
            state.ChangedOnly = true;
        }

        float controlGap = EditorLayout.Scaled(7f);
        float resetSize = EditorLayout.Scaled(29f);
        float minimumSearchWidth = EditorLayout.Scaled(100f);
        float searchWidth = MathF.Min(
            EditorLayout.Scaled(260f),
            MathF.Max(
                minimumSearchWidth,
                contentWidth - modeWidth - (controlGap * 2f) - resetSize));
        float resetX = startX + contentWidth - resetSize;
        float searchX = resetX - controlGap - searchWidth;
        float minimumSearchX = startX + modeWidth + controlGap;
        if (searchX < minimumSearchX)
        {
            searchX = minimumSearchX;
            searchWidth = MathF.Max(1f, resetX - controlGap - searchX);
        }

        float searchHeight = EditorLayout.Scaled(28f);
        ImGui.SetCursorPos(new Vector2(
            searchX,
            rowStartY + ((toolbarHeight - searchHeight) * 0.5f)));
        _ = EditorSearchField.Draw(
            "objectCollectionModSettingsFilter",
            ref state.Filter,
            new EditorSearchFieldOptions(
                "Filter settings",
                ThemeColors.AccentPrimary,
                MaxLength: CollectionPanel.ObjectCollectionFilterMaxLength,
                Width: searchWidth,
                Height: searchHeight));

        ImGui.SetCursorPos(new Vector2(
            resetX,
            rowStartY + ((toolbarHeight - resetSize) * 0.5f)));
        bool reset;
        using (ImRaii.Disabled(!canReset))
        {
            reset = EditorIconButton.DrawAccent(
                "objectCollectionModSettingsResetAll",
                FontAwesomeIcon.Undo,
                "Use defaults for every setting in this mod",
                ThemeColors.AccentPrimary,
                resetSize,
                style: EditorAccentIconButtonStyle.Subtle);
        }

        if (reset && canReset)
        {
            ClearObjectCollectionModSettings(collection, entryIndex);
        }

        ImGui.SetCursorPos(new Vector2(
            startX,
            rowStartY + toolbarHeight + EditorLayout.Scaled(ObjectCollectionModSettingsToolbarGap)));
        return reset && canReset;
    }

    internal static bool CanResetObjectCollectionModSettings(ObjectCollectionModSettings entry)
        => entry.Enabled && entry.Settings.Count > 0;

    internal static IReadOnlyList<int> GetVisibleObjectCollectionModSettingIndices(
        ObjectCollectionModSettingsView settingsView,
        ObjectCollectionModSettingsPanelState state)
    {
        string filter = state.Filter.Trim();
        if (ReferenceEquals(state.Source, settingsView)
         && string.Equals(state.AppliedFilter, filter, StringComparison.Ordinal)
         && state.AppliedChangedOnly == state.ChangedOnly)
        {
            return state.VisibleGroupIndices;
        }

        state.Source = settingsView;
        state.AppliedFilter = filter;
        state.AppliedChangedOnly = state.ChangedOnly;
        state.VisibleGroupIndices.Clear();
        for (int index = 0; index < settingsView.Groups.Count; ++index)
        {
            ObjectCollectionModSettingsGroup group = settingsView.Groups[index];
            if ((!state.ChangedOnly || group.HasOverride)
             && ObjectCollectionModSettingMatchesFilter(group, filter))
            {
                state.VisibleGroupIndices.Add(index);
            }
        }

        return state.VisibleGroupIndices;
    }

    private static bool ObjectCollectionModSettingMatchesFilter(
        ObjectCollectionModSettingsGroup group,
        string filter)
    {
        if (filter.Length == 0
         || group.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
         || group.Description.Contains(filter, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (ObjectCollectionModSettingsOption option in group.Options)
        {
            if (option.Visible
             && (option.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                 || option.Description.Contains(filter, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    private bool DrawObjectCollectionModSettingsGrid(
        ObjectCollectionSnapshot collection,
        int entryIndex,
        ObjectCollectionModSettingsView settingsView,
        IReadOnlyList<int> visibleGroupIndices,
        bool modEnabled)
    {
        float width = EditorLayout.Positive(ImGui.GetContentRegionAvail().X);
        float naturalHeight = ResolveObjectCollectionModSettingsGridNaturalHeight(width, visibleGroupIndices.Count);
        float maxHeight = EditorLayout.Scaled(ObjectCollectionModSettingsGridMaxHeight);
        if (naturalHeight <= maxHeight)
        {
            return DrawObjectCollectionModSettingsGridContent(
                collection,
                entryIndex,
                settingsView,
                visibleGroupIndices,
                modEnabled);
        }

        using var childBackground = ImRaii.PushColor(ImGuiCol.ChildBg, Vector4.Zero);
        using var windowPadding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        using var child = EditorScrollList.Begin(
            "##objectCollectionModSettingsScroll",
            new Vector2(width, maxHeight),
            _editorOverlayLayer.CreateScrollPanelOptions(CollectionPanel.CollectionModStyle.Settings, 0f, ThemeColors.AccentPrimary));
        if (!child)
        {
            return false;
        }

        return DrawObjectCollectionModSettingsGridContent(
            collection,
            entryIndex,
            settingsView,
            visibleGroupIndices,
            modEnabled);
    }

    private bool DrawObjectCollectionModSettingsGridContent(
        ObjectCollectionSnapshot collection,
        int entryIndex,
        ObjectCollectionModSettingsView settingsView,
        IReadOnlyList<int> visibleGroupIndices,
        bool modEnabled)
    {
        int columnCount = ResolveObjectCollectionModSettingsColumnCount(ImGui.GetContentRegionAvail().X);
        bool changed = false;
        using var cellPadding = ImRaii.PushStyle(ImGuiStyleVar.CellPadding, EditorLayout.ScaledVector(4f, 0f));
        using var table = ImRaii.Table(
            "##objectCollectionModSettingsGrid",
            columnCount,
            ImGuiTableFlags.SizingStretchSame
          | ImGuiTableFlags.NoPadOuterX
          | ImGuiTableFlags.NoSavedSettings);
        if (!table)
        {
            return false;
        }

        for (int offset = 0; offset < visibleGroupIndices.Count; offset += columnCount)
        {
            ImGui.TableNextRow();
            for (int column = 0; column < columnCount && offset + column < visibleGroupIndices.Count; ++column)
            {
                ImGui.TableSetColumnIndex(column);
                int groupIndex = visibleGroupIndices[offset + column];
                ObjectCollectionModSettingsGroup group = settingsView.Groups[groupIndex];
                using var disabled = ImRaii.Disabled(changed);
                changed |= DrawObjectCollectionModSettingsGroup(collection, entryIndex, groupIndex, group, modEnabled);
            }
        }

        ImGui.SetCursorPosY(ImGui.GetCursorPosY() - EditorLayout.Scaled(ObjectCollectionModSettingRowGap));
        return changed;
    }

    private static int ResolveObjectCollectionModSettingsColumnCount(float width)
        => width >= EditorLayout.Scaled(ObjectCollectionModSettingsTwoColumnWidth) ? 2 : 1;

    private static float ResolveObjectCollectionModSettingsGridNaturalHeight(float width, int itemCount)
    {
        int columnCount = ResolveObjectCollectionModSettingsColumnCount(width);
        int rowCount = (itemCount + columnCount - 1) / columnCount;
        return (rowCount * EditorLayout.Scaled(ObjectCollectionModSettingRowHeight))
             + (Math.Max(0, rowCount - 1) * EditorLayout.Scaled(ObjectCollectionModSettingRowGap));
    }

    private static void DrawObjectCollectionModSettingsMessage(string message, float availableWidth)
    {
        string text = string.IsNullOrWhiteSpace(message)
            ? "Mod settings are unavailable."
            : message;
        string visibleText = EditorTextUtility.ClipTextToWidth(text, EditorLayout.Positive(availableWidth));
        Vector2 textSize = ImGui.CalcTextSize(visibleText);
        Vector2 min = ImGui.GetCursorScreenPos();
        ImGui.GetWindowDrawList().AddText(
            new Vector2(
                min.X,
                min.Y + MathF.Max(
                    0f,
                    (EditorLayout.Scaled(ObjectCollectionModSettingsMessageHeight)
                     - EditorLayout.Scaled(ObjectCollectionModSettingsBodyTopPadding)
                     - textSize.Y) * 0.5f)),
            ImGui.GetColorU32(ThemeColors.TextDisabled),
            visibleText);
    }

    private static void DrawObjectCollectionModSettingsEmptyState(string message)
    {
        float width = EditorLayout.Positive(ImGui.GetContentRegionAvail().X);
        float height = EditorLayout.Scaled(ObjectCollectionModSettingRowHeight);
        Vector2 min = ImGui.GetCursorScreenPos();
        ImGui.Dummy(new Vector2(width, height));
        ImDrawListPtr drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(
            min,
            min + new Vector2(width, height),
            ImGui.GetColorU32(CollectionPanel.CollectionModStyle.Setting));
        Vector2 textSize = ImGui.CalcTextSize(message);
        drawList.AddText(
            new Vector2(min.X + EditorLayout.Scaled(8f), min.Y + ((height - textSize.Y) * 0.5f)),
            ImGui.GetColorU32(ThemeColors.TextDisabled),
            EditorTextUtility.ClipTextToWidth(message, MathF.Max(1f, width - EditorLayout.Scaled(16f))));
    }

    private bool DrawObjectCollectionModSettingsGroup(
        ObjectCollectionSnapshot collection,
        int entryIndex,
        int groupIndex,
        ObjectCollectionModSettingsGroup group,
        bool modEnabled)
    {
        using var id = ImRaii.PushId(groupIndex);

        ModSettingRowLayout layout = ResolveObjectCollectionModSettingRowLayout(ImGui.GetCursorScreenPos());
        bool availableAppearance = modEnabled && group.Available;
        var accent = ResolveObjectCollectionModSettingAccent(group.HasOverride, availableAppearance);
        DrawObjectCollectionModSettingFrame(layout, accent, group.HasOverride, availableAppearance);
        DrawObjectCollectionModSettingLabel(layout, group, availableAppearance);

        bool changed = DrawObjectCollectionModSettingControl(collection, entryIndex, group, modEnabled, layout);
        using var disabled = ImRaii.Disabled(changed);
        changed |= DrawObjectCollectionModSettingReset(collection, entryIndex, group, modEnabled, layout);

        ImGui.SetCursorScreenPos(layout.EndCursor);
        return changed;
    }

    private static ModSettingRowLayout ResolveObjectCollectionModSettingRowLayout(Vector2 start)
    {
        var width = EditorLayout.Positive(ImGui.GetContentRegionAvail().X);
        Vector2 padding = EditorLayout.ScaledVector(8f, 2f);
        float resetButtonSize = EditorLayout.Scaled(27f);
        float rowHeight = EditorLayout.Scaled(ObjectCollectionModSettingRowHeight);
        float rowGap = EditorLayout.Scaled(ObjectCollectionModSettingRowGap);
        float controlHeight = EditorLayout.Scaled(27f);
        Vector2 min = start;
        Vector2 max = new(start.X + width, start.Y + rowHeight);
        float controlRight = max.X - EditorLayout.Scaled(3f);
        Vector2 resetPos = new(controlRight - resetButtonSize, min.Y + ((rowHeight - resetButtonSize) * 0.5f));
        float labelX = min.X + padding.X;
        float availableBeforeReset = MathF.Max(EditorListCard.MinimumTextWidth, resetPos.X - labelX);
        float labelWidth = MathF.Min(EditorLayout.Scaled(190f), MathF.Max(EditorLayout.Scaled(112f), availableBeforeReset * 0.42f));
        float controlX = labelX + labelWidth + EditorLayout.Scaled(6f);
        float valueRight = resetPos.X - EditorLayout.Scaled(6f);
        float controlWidth = MathF.Max(EditorLayout.Scaled(96f), valueRight - controlX);
        float labelY = min.Y + ((rowHeight - ImGui.GetTextLineHeight()) * 0.5f);
        float controlY = min.Y + ((rowHeight - controlHeight) * 0.5f);

        return new ModSettingRowLayout(
            min,
            max,
            new Vector2(labelX, labelY),
            new Vector2(controlX, controlY),
            resetPos,
            new Vector2(start.X, max.Y + rowGap),
            labelWidth,
            controlWidth,
            resetButtonSize);
    }

    private static void DrawObjectCollectionModSettingFrame(
        ModSettingRowLayout layout,
        Vector4 accent,
        bool hasOverride,
        bool modEnabled)
    {
        ImDrawListPtr drawList = ImGui.GetWindowDrawList();
        bool hovered = EditorInputUtility.IsMouseInside(layout.Min, layout.Max);
        Vector4 fill = hasOverride
            ? CollectionPanel.CollectionModStyle.SettingChanged
            : CollectionPanel.CollectionModStyle.Setting;
        if (hovered && modEnabled)
        {
            fill = CollectionPanel.CollectionModStyle.SettingHover;
        }

        fill.W *= modEnabled ? 1f : 0.48f;
        drawList.AddRectFilled(layout.Min, layout.Max, ImGui.GetColorU32(fill));
        if (hasOverride)
        {
            drawList.AddRectFilled(
                layout.Min,
                new Vector2(layout.Min.X + EditorLayout.Scaled(2f), layout.Max.Y),
                ImGui.GetColorU32(accent with { W = modEnabled ? 0.94f : 0.42f }));
        }
    }

    private static void DrawObjectCollectionModSettingLabel(
        ModSettingRowLayout layout,
        ObjectCollectionModSettingsGroup group,
        bool modEnabled)
    {
        var labelColor = modEnabled
            ? ThemeColors.Text with { W = 0.94f }
            : ThemeColors.TextDisabled with { W = 0.78f };
        string groupLabel = ResolveModSettingLabel(group.Name, UnnamedModGroupLabel);
        string clippedGroupName = EditorTextUtility.ClipTextToWidth(groupLabel, layout.LabelWidth);
        ImGui.GetWindowDrawList().AddText(
            layout.LabelPos,
            ImGui.GetColorU32(labelColor),
            clippedGroupName);
        if (IntonerTooltip.IsAreaHovered(
                new Vector2(layout.LabelPos.X, layout.Min.Y),
                new Vector2(layout.LabelPos.X + layout.LabelWidth, layout.Max.Y)))
        {
            DrawObjectCollectionModSettingTooltip(
                string.Equals(clippedGroupName, groupLabel, StringComparison.Ordinal) ? string.Empty : groupLabel,
                group.Description,
                group.Available,
                isDefault: false);
        }
    }

    private bool DrawObjectCollectionModSettingControl(
        ObjectCollectionSnapshot collection,
        int entryIndex,
        ObjectCollectionModSettingsGroup group,
        bool modEnabled,
        ModSettingRowLayout layout)
    {
        ImGui.SetCursorScreenPos(layout.ControlPos);
        using var controlText = ImRaii.PushColor(
            ImGuiCol.Text,
            modEnabled ? ThemeColors.Text : ThemeColors.TextDisabled with { W = 0.82f });
        using var checkMark = ImRaii.PushColor(
            ImGuiCol.CheckMark,
            modEnabled ? ThemeColors.AccentPrimary : ThemeColors.AccentGrey);
        using var frameBg = ImRaii.PushColor(
            ImGuiCol.FrameBg,
            CollectionPanel.CollectionModStyle.Field with
            {
                W = CollectionPanel.CollectionModStyle.Field.W * (modEnabled ? 1f : 0.50f),
            });
        using var frameBgHovered = ImRaii.PushColor(
            ImGuiCol.FrameBgHovered,
                modEnabled
                    ? CollectionPanel.CollectionModStyle.FieldHover
                    : CollectionPanel.CollectionModStyle.Field with { W = CollectionPanel.CollectionModStyle.Field.W * 0.50f });
        using var frameBgActive = ImRaii.PushColor(
            ImGuiCol.FrameBgActive,
                modEnabled
                    ? CollectionPanel.CollectionModStyle.FieldHover
                    : CollectionPanel.CollectionModStyle.Field with { W = CollectionPanel.CollectionModStyle.Field.W * 0.50f });
        using var frameBorder = ImRaii.PushColor(
            ImGuiCol.Border,
            CollectionPanel.CollectionModStyle.FieldBorder with
            {
                W = CollectionPanel.CollectionModStyle.FieldBorder.W * (modEnabled ? 1f : 0.42f),
            });
        using var frameBorderSize = ImRaii.PushStyle(ImGuiStyleVar.FrameBorderSize, EditorLayout.Scaled(1f));
        using var frameRounding = ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, EditorLayout.Scaled(3f));
        using var framePadding = ImRaii.PushStyle(
            ImGuiStyleVar.FramePadding,
            new Vector2(EditorLayout.Scaled(8f), MathF.Max(0f, (EditorLayout.Scaled(27f) - ImGui.GetTextLineHeight()) * 0.5f)));
        using var disabled = ImRaii.Disabled(!modEnabled || !group.Available);

        return group.Kind == ObjectCollectionModSettingsGroupKind.Single
            ? DrawObjectCollectionSingleModGroup(collection, entryIndex, group, layout.ControlWidth)
            : DrawObjectCollectionMultiModGroup(collection, entryIndex, group, layout.ControlWidth);
    }

    private bool DrawObjectCollectionModSettingReset(
        ObjectCollectionSnapshot collection,
        int entryIndex,
        ObjectCollectionModSettingsGroup group,
        bool modEnabled,
        ModSettingRowLayout layout)
    {
        if (!group.HasOverride)
        {
            return false;
        }

        ImGui.SetCursorScreenPos(layout.ResetPos);
        using var disabled = ImRaii.Disabled(!modEnabled || !group.Available);
        if (!EditorIconButton.DrawAccent(
                "objectCollectionModSettingReset",
                FontAwesomeIcon.Undo,
                "Use the mod default for this setting",
                ThemeColors.AccentPrimary,
                edgeOverride: layout.ResetButtonSize,
                style: EditorAccentIconButtonStyle.Subtle))
        {
            return false;
        }

        ClearObjectCollectionModGroupSelection(collection, entryIndex, group.Name);
        return true;
    }

    private static Vector4 ResolveObjectCollectionModSettingAccent(bool hasOverride, bool modEnabled)
        => (modEnabled, hasOverride) switch
        {
            (true, true) => ThemeColors.AccentPrimary,
            (true, false) => ThemeColors.TextDisabled,
            _ => ThemeColors.AccentGrey,
        };

    private bool DrawObjectCollectionSingleModGroup(
        ObjectCollectionSnapshot collection,
        int entryIndex,
        ObjectCollectionModSettingsGroup group,
        float width)
    {
        string preview = ResolveSelectedObjectCollectionSingleModOptionLabel(group);

        ImGui.SetNextItemWidth(width);
        using var combo = ImRaii.Combo("##singleOption", preview);
        if (!combo)
        {
            if (ImGui.IsItemHovered() && ImGui.CalcTextSize(preview).X > width)
            {
                IntonerTooltip.DrawText(preview);
            }

            return false;
        }

        for (var optionIndex = 0; optionIndex < group.Options.Count; ++optionIndex)
        {
            ObjectCollectionModSettingsOption option = group.Options[optionIndex];
            if (!option.Visible)
            {
                continue;
            }

            bool selected = option.Selected;
            using var optionId = ImRaii.PushId(optionIndex);
            bool changed;
            using (ImRaii.Disabled(!option.Available))
            {
                changed = ImGui.Selectable(
                    ResolveModSettingLabel(option.Name, UnnamedModOptionLabel),
                    selected);
            }

            if (changed)
            {
                UpdateObjectCollectionModGroupSelection(collection, entryIndex, group.Name, [option.Name]);
                return true;
            }

            if (selected)
            {
                ImGui.SetItemDefaultFocus();
            }

            DrawObjectCollectionModOptionTooltip(option);
        }

        return false;
    }

    private bool DrawObjectCollectionMultiModGroup(
        ObjectCollectionSnapshot collection,
        int entryIndex,
        ObjectCollectionModSettingsGroup group,
        float width)
    {
        string preview = BuildObjectCollectionMultiModGroupPreview(group);
        ImGui.SetNextItemWidth(width);
        using var combo = ImRaii.Combo("##multiOptions", preview);
        if (!combo)
        {
            if (ImGui.IsItemHovered())
            {
                IntonerTooltip.DrawText(BuildObjectCollectionMultiModGroupTooltip(group));
            }

            return false;
        }

        bool edited = false;
        for (var optionIndex = 0; optionIndex < group.Options.Count; ++optionIndex)
        {
            ObjectCollectionModSettingsOption option = group.Options[optionIndex];
            if (!option.Visible)
            {
                continue;
            }

            bool selected = option.Selected;
            using var optionId = ImRaii.PushId(optionIndex);
            bool changed;
            using (ImRaii.Disabled(edited || !option.Available))
            {
                changed = ImGui.Checkbox(
                    ResolveModSettingLabel(option.Name, UnnamedModOptionLabel),
                    ref selected);
            }

            if (changed)
            {
                List<string> selectedOptions = BuildToggledObjectCollectionModOptionSelection(group, optionIndex, selected);
                UpdateObjectCollectionModGroupSelection(collection, entryIndex, group.Name, selectedOptions);
                edited = true;
            }

            DrawObjectCollectionModOptionTooltip(option);
        }

        return edited;
    }

    private static List<string> BuildToggledObjectCollectionModOptionSelection(
        ObjectCollectionModSettingsGroup group,
        int toggledOptionIndex,
        bool toggledSelected)
    {
        List<string> selectedOptions = [];
        for (var optionIndex = 0; optionIndex < group.Options.Count; ++optionIndex)
        {
            ObjectCollectionModSettingsOption option = group.Options[optionIndex];
            bool selected = optionIndex == toggledOptionIndex
                ? toggledSelected
                : option.Selected;
            if (selected)
            {
                selectedOptions.Add(option.Name);
            }
        }

        return selectedOptions;
    }

    private static void DrawObjectCollectionModOptionTooltip(ObjectCollectionModSettingsOption option)
    {
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            DrawObjectCollectionModSettingTooltip(
                string.Empty,
                option.Description,
                option.Available,
                option.DefaultSelected);
        }
    }

    private static void DrawObjectCollectionModSettingTooltip(
        string title,
        string description,
        bool available,
        bool isDefault)
    {
        List<string> lines = [];
        if (title.Length > 0)
        {
            lines.Add(title);
        }

        if (description.Length > 0)
        {
            lines.Add(description);
        }

        if (!available)
        {
            lines.Add("Unavailable for the current mod selections.");
        }

        if (isDefault)
        {
            lines.Add("Selected by default.");
        }

        if (lines.Count > 0)
        {
            IntonerTooltip.DrawText(string.Join('\n', lines), ThemeColors.AccentPrimary, 35f);
        }
    }

    private static string BuildObjectCollectionMultiModGroupPreview(ObjectCollectionModSettingsGroup group)
    {
        int selectedCount = 0;
        foreach (ObjectCollectionModSettingsOption option in group.Options)
        {
            if (option.Visible && option.Selected)
            {
                ++selectedCount;
            }
        }

        return selectedCount switch
        {
            0 => "None selected",
            1 => "1 selected",
            _ => $"{selectedCount} selected",
        };
    }

    private static string ResolveSelectedObjectCollectionSingleModOptionLabel(ObjectCollectionModSettingsGroup group)
    {
        foreach (ObjectCollectionModSettingsOption option in group.Options)
        {
            if (option.Visible && option.Selected)
            {
                return ResolveModSettingLabel(option.Name, UnnamedModOptionLabel);
            }
        }

        return "Select option";
    }

    private static string BuildObjectCollectionMultiModGroupTooltip(ObjectCollectionModSettingsGroup group)
    {
        List<string> selectedOptions = CollectSelectedObjectCollectionModOptionLabels(group);
        if (selectedOptions.Count == 0)
        {
            return "No options selected";
        }

        return selectedOptions.Count == 1
            ? $"Selected: {selectedOptions[0]}"
            : $"Selected:\n- {string.Join("\n- ", selectedOptions)}";
    }

    private static List<string> CollectSelectedObjectCollectionModOptionLabels(ObjectCollectionModSettingsGroup group)
        => group.Options
            .Where(static option => option.Visible && option.Selected)
            .Select(static option => ResolveModSettingLabel(option.Name, UnnamedModOptionLabel))
            .ToList();

    private static string ResolveModSettingLabel(string value, string fallback)
        => value.Length > 0 ? value : fallback;

    internal static string BuildObjectCollectionModRowKey(string collectionId, ObjectCollectionModSettings entry)
        => $"{ObjectCollectionKeyUtility.NormalizeCollectionId(collectionId)}:{ObjectCollectionKeyUtility.NormalizeModDirectory(entry.ModDirectory)}";

    internal void UpdateObjectCollectionEntry(ObjectCollectionSnapshot collection, int index, ObjectCollectionModSettings updatedEntry)
    {
        if (index < 0 || index >= collection.Record.Entries.Count)
        {
            return;
        }

        List<ObjectCollectionModSettings> entries = [.. collection.Record.Entries];
        entries[index] = updatedEntry;
        _ = TryUpdateObjectCollectionEntries(collection, entries);
    }

    internal bool TryUpdateObjectCollectionEntries(
        ObjectCollectionSnapshot collection,
        List<ObjectCollectionModSettings> entries)
    {
        if (_objectCollectionManager.TryUpdateCollection(collection.Record with { Entries = entries }, out _))
        {
            return true;
        }

        _logger.LogWarning(
            "could not save object collection {CollectionId}",
            collection.Record.CollectionId);
        return false;
    }

    private void UpdateObjectCollectionModGroupSelection(
        ObjectCollectionSnapshot collection,
        int index,
        string groupName,
        IReadOnlyList<string> selectedOptionNames)
    {
        if (index < 0 || index >= collection.Record.Entries.Count)
        {
            return;
        }

        string normalizedGroupName = CollectionModSettingsUtility.NormalizeGroupName(groupName);
        ObjectCollectionModSettings entry = collection.Record.Entries[index];
        Dictionary<string, List<string>> settings = CollectionModSettingsUtility.CloneSettings(entry.Settings);
        CollectionModSettingsUtility.RemoveGroup(settings, normalizedGroupName);
        if (!CollectionModSettingsUtility.TryNormalizeOptionNames(selectedOptionNames, out List<string> normalizedOptionNames))
        {
            return;
        }

        settings[normalizedGroupName] = normalizedOptionNames;

        UpdateObjectCollectionEntry(collection, index, entry with { Settings = settings });
    }

    private void ClearObjectCollectionModGroupSelection(
        ObjectCollectionSnapshot collection,
        int index,
        string groupName)
    {
        if (index < 0 || index >= collection.Record.Entries.Count)
        {
            return;
        }

        string normalizedGroupName = CollectionModSettingsUtility.NormalizeGroupName(groupName);
        ObjectCollectionModSettings entry = collection.Record.Entries[index];
        Dictionary<string, List<string>> settings = CollectionModSettingsUtility.CloneSettings(entry.Settings);
        if (!CollectionModSettingsUtility.RemoveGroup(settings, normalizedGroupName))
        {
            return;
        }

        UpdateObjectCollectionEntry(collection, index, entry with { Settings = settings });
    }

    private void ClearObjectCollectionModSettings(ObjectCollectionSnapshot collection, int index)
    {
        if (index < 0 || index >= collection.Record.Entries.Count)
        {
            return;
        }

        ObjectCollectionModSettings entry = collection.Record.Entries[index];
        if (entry.Settings.Count > 0)
        {
            UpdateObjectCollectionEntry(
                collection,
                index,
                entry with { Settings = new Dictionary<string, List<string>>(StringComparer.Ordinal) });
        }
    }
}
