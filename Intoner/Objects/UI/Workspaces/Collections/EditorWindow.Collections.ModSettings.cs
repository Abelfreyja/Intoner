using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Intoner.Objects.Collections;
using Intoner.Objects.UI.Components;
using Intoner.UI;
using System.Numerics;

namespace Intoner.Objects.UI;

internal sealed partial class EditorWindow
{
    private readonly record struct ModSettingRowLayout(
        Vector2 Min,
        Vector2 Max,
        Vector2 LabelPos,
        Vector2 ControlPos,
        Vector2 ResetPos,
        Vector2 EndCursor,
        float LabelWidth,
        float ControlWidth,
        float ResetButtonSize);

    private bool DrawObjectCollectionModSettingsPanel(ObjectCollectionSnapshot collection, ObjectCollectionModSettings entry, int index)
    {
        ObjectCollectionModSettingsView settingsView = _objectModDataSource.GetModSettings(entry);
        var lineAccent = entry.Enabled ? EditorColors.AccentPurple : EditorColors.AccentGrey;
        var lineColor = lineAccent with { W = entry.Enabled ? 0.58f : 0.32f };
        var lineX = ImGui.GetCursorScreenPos().X + Scaled(18f);
        var drawList = ImGui.GetWindowDrawList();
        float? lineStartY = null;
        var lineEndY = 0f;

        var childInset = Scaled(30f);
        var rightInset = Scaled(5f);
        using var indent = ImRaii.PushStyle(ImGuiStyleVar.IndentSpacing, childInset);
        ImGui.Indent(childInset);

        try
        {
            if (settingsView.ResolveState != ObjectCollectionResolveState.Ready)
            {
                DrawObjectCollectionMutedText(settingsView.StatusText, topSpacing: 1f);
                return false;
            }

            if (settingsView.Groups.Count == 0)
            {
                DrawObjectCollectionMutedText("No editable settings for this mod.", topSpacing: 1f);
                return false;
            }

            for (var groupIndex = 0; groupIndex < settingsView.Groups.Count; ++groupIndex)
            {
                ObjectCollectionModSettingsGroup group = settingsView.Groups[groupIndex];
                var groupStart = ImGui.GetCursorScreenPos();
                if (DrawObjectCollectionModSettingsGroup(collection, index, group, entry.Enabled, rightInset))
                {
                    return true;
                }

                var groupEnd = ImGui.GetCursorScreenPos();
                var centerY = groupStart.Y + ((groupEnd.Y - groupStart.Y) * 0.5f);
                lineStartY ??= groupStart.Y;
                lineEndY = centerY;
                drawList.AddLine(
                    new Vector2(lineX, centerY),
                    new Vector2(groupStart.X, centerY),
                    ImGui.GetColorU32(lineColor),
                    Scaled(1f));

                if (groupIndex + 1 < settingsView.Groups.Count)
                {
                    ImGui.Dummy(new Vector2(0f, Scaled(5f)));
                }
            }
        }
        finally
        {
            ImGui.Unindent(childInset);
        }

        if (lineStartY.HasValue)
        {
            drawList.AddLine(
                new Vector2(lineX, lineStartY.Value),
                new Vector2(lineX, lineEndY),
                ImGui.GetColorU32(lineColor),
                Scaled(1f));
        }

        return false;
    }

    private bool DrawObjectCollectionModSettingsGroup(
        ObjectCollectionSnapshot collection,
        int entryIndex,
        ObjectCollectionModSettingsGroup group,
        bool modEnabled,
        float rightInset)
    {
        using var id = ImRaii.PushId($"objectCollectionModSettingsGroup:{collection.Record.CollectionId}:{entryIndex}:{group.Name}");

        ModSettingRowLayout layout = ResolveObjectCollectionModSettingRowLayout(ImGui.GetCursorScreenPos(), rightInset);
        var accent = ResolveObjectCollectionModSettingAccent(group.HasOverride, modEnabled);
        DrawObjectCollectionModSettingFrame(layout, accent, group.HasOverride, modEnabled);
        DrawObjectCollectionModSettingLabel(layout, group.Name, modEnabled);

        if (DrawObjectCollectionModSettingControl(collection, entryIndex, group, modEnabled, layout))
        {
            ImGui.SetCursorScreenPos(layout.EndCursor);
            return true;
        }

        if (DrawObjectCollectionModSettingReset(collection, entryIndex, group, modEnabled, accent, layout))
        {
            ImGui.SetCursorScreenPos(layout.EndCursor);
            return true;
        }

        ImGui.SetCursorScreenPos(layout.EndCursor);
        return false;
    }

    private static ModSettingRowLayout ResolveObjectCollectionModSettingRowLayout(Vector2 start, float rightInset)
    {
        var width = Positive(ImGui.GetContentRegionAvail().X - rightInset);
        var padding = ScaledVector(9f, 5f);
        var resetButtonSize = ResolveActionIconButtonEdge(FontAwesomeIcon.Undo);
        var rowHeight = MathF.Max(resetButtonSize, ImGui.GetFrameHeight()) + (padding.Y * 2f);
        var min = start;
        var max = new Vector2(start.X + width, start.Y + rowHeight);
        var resetPos = new Vector2(max.X - padding.X - resetButtonSize, min.Y + ((rowHeight - resetButtonSize) * 0.5f));
        var labelX = min.X + padding.X;
        var controlRight = resetPos.X - Scaled(7f);
        var availableBeforeReset = MathF.Max(EditorListCard.MinimumTextWidth, controlRight - labelX);
        var labelWidth = MathF.Min(Scaled(190f), MathF.Max(Scaled(90f), availableBeforeReset * 0.34f));
        var controlX = labelX + labelWidth + Scaled(10f);
        var controlWidth = MathF.Max(Scaled(120f), controlRight - controlX);
        var labelY = min.Y + ((rowHeight - ImGui.GetTextLineHeight()) * 0.5f);
        var controlY = min.Y + ((rowHeight - ImGui.GetFrameHeight()) * 0.5f);

        return new ModSettingRowLayout(
            min,
            max,
            new Vector2(labelX, labelY),
            new Vector2(controlX, controlY),
            resetPos,
            new Vector2(start.X, max.Y),
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
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(
            layout.Min,
            layout.Max,
            ImGui.GetColorU32(EditorColors.ButtonDefault with { W = modEnabled ? 0.18f : 0.12f }),
            Scaled(6f));
        drawList.AddRect(
            layout.Min,
            layout.Max,
            ImGui.GetColorU32(accent with { W = ResolveObjectCollectionModSettingBorderAlpha(hasOverride, modEnabled) }),
            Scaled(6f));
    }

    private static void DrawObjectCollectionModSettingLabel(
        ModSettingRowLayout layout,
        string groupName,
        bool modEnabled)
    {
        var labelColor = modEnabled
            ? EditorColors.Text
            : EditorColors.TextDisabled with { W = 0.78f };
        string clippedGroupName = ClipTextToWidth(groupName, layout.LabelWidth);
        ImGui.GetWindowDrawList().AddText(layout.LabelPos, ImGui.GetColorU32(labelColor), clippedGroupName);
        if (!string.Equals(clippedGroupName, groupName, StringComparison.Ordinal)
         && EditorInputUtility.IsMouseInside(
                new Vector2(layout.LabelPos.X, layout.Min.Y),
                new Vector2(layout.LabelPos.X + layout.LabelWidth, layout.Max.Y)))
        {
            UiSharedService.DrawAccentTooltipText(groupName, EditorColors.AccentPurple, wrapEms: 35f);
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
            modEnabled ? EditorColors.Text : EditorColors.TextDisabled with { W = 0.82f });
        using var checkMark = ImRaii.PushColor(
            ImGuiCol.CheckMark,
            modEnabled ? EditorColors.AccentPurple : EditorColors.AccentGrey);
        using var frameBg = ImRaii.PushColor(
            ImGuiCol.FrameBg,
            EditorColors.ButtonDefault with { W = modEnabled ? 0.48f : 0.24f });
        using var frameBgHovered = ImRaii.PushColor(
            ImGuiCol.FrameBgHovered,
            modEnabled ? EditorColors.ButtonDefault with { W = 0.58f } : EditorColors.AccentGrey with { W = 0.18f });
        using var frameBgActive = ImRaii.PushColor(
            ImGuiCol.FrameBgActive,
            modEnabled ? EditorColors.ButtonDefault with { W = 0.50f } : EditorColors.AccentGrey with { W = 0.14f });

        return group.Kind == ObjectCollectionModSettingsGroupKind.Single
            ? DrawObjectCollectionSingleModGroup(collection, entryIndex, group, layout.ControlWidth)
            : DrawObjectCollectionMultiModGroup(collection, entryIndex, group, layout.ControlWidth);
    }

    private bool DrawObjectCollectionModSettingReset(
        ObjectCollectionSnapshot collection,
        int entryIndex,
        ObjectCollectionModSettingsGroup group,
        bool modEnabled,
        Vector4 accent,
        ModSettingRowLayout layout)
    {
        ImGui.SetCursorScreenPos(layout.ResetPos);
        using var disabled = ImRaii.Disabled(!group.HasOverride);
        if (!DrawAccentIconButton(
                "resetGroup",
                FontAwesomeIcon.Undo,
                group.HasOverride ? "Use default for this group" : "Using default",
                modEnabled ? accent : EditorColors.AccentGrey,
                layout.ResetButtonSize))
        {
            return false;
        }

        ClearObjectCollectionModGroupSelection(collection, entryIndex, group.Name);
        return true;
    }

    private static Vector4 ResolveObjectCollectionModSettingAccent(bool hasOverride, bool modEnabled)
        => modEnabled
            ? hasOverride
                ? EditorColors.AccentPurple
                : EditorColors.TextDisabled
            : EditorColors.AccentGrey;

    private static float ResolveObjectCollectionModSettingBorderAlpha(bool hasOverride, bool modEnabled)
        => modEnabled
            ? hasOverride ? 0.46f : 0.20f
            : hasOverride ? 0.30f : 0.16f;

    private bool DrawObjectCollectionSingleModGroup(
        ObjectCollectionSnapshot collection,
        int entryIndex,
        ObjectCollectionModSettingsGroup group,
        float width)
    {
        string preview = group.Options.FirstOrDefault(static option => option.Selected).Name ?? string.Empty;
        if (preview.Length == 0)
        {
            preview = "Select option";
        }

        ImGui.SetNextItemWidth(width);
        using var combo = ImRaii.Combo("##singleOption", preview);
        if (!combo)
        {
            if (ImGui.IsItemHovered() && ImGui.CalcTextSize(preview).X > width)
            {
                ImGui.SetTooltip(preview);
            }

            return false;
        }

        for (var optionIndex = 0; optionIndex < group.Options.Count; ++optionIndex)
        {
            ObjectCollectionModSettingsOption option = group.Options[optionIndex];
            bool selected = option.Selected;
            using var optionId = ImRaii.PushId(optionIndex);
            if (ImGui.Selectable(option.Name, selected))
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
                ImGui.SetTooltip(BuildObjectCollectionMultiModGroupTooltip(group));
            }

            return false;
        }

        for (var optionIndex = 0; optionIndex < group.Options.Count; ++optionIndex)
        {
            ObjectCollectionModSettingsOption option = group.Options[optionIndex];
            bool selected = option.Selected;
            using var optionId = ImRaii.PushId(optionIndex);
            if (ImGui.Checkbox(option.Name, ref selected))
            {
                List<string> selectedOptions = BuildToggledObjectCollectionModOptionSelection(group, optionIndex, selected);
                UpdateObjectCollectionModGroupSelection(collection, entryIndex, group.Name, selectedOptions);
                return true;
            }

            DrawObjectCollectionModOptionTooltip(option);
        }

        return false;
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
        if (option.DefaultSelected && ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Default");
        }
    }

    private static string BuildObjectCollectionMultiModGroupPreview(ObjectCollectionModSettingsGroup group)
    {
        List<string> selectedOptions = CollectSelectedObjectCollectionModOptionNames(group);
        return selectedOptions.Count switch
        {
            0 => "None selected",
            1 => "1 selected",
            _ => $"{selectedOptions.Count} selected",
        };
    }

    private static string BuildObjectCollectionMultiModGroupTooltip(ObjectCollectionModSettingsGroup group)
    {
        List<string> selectedOptions = CollectSelectedObjectCollectionModOptionNames(group);
        if (selectedOptions.Count == 0)
        {
            return "No options selected";
        }

        return selectedOptions.Count == 1
            ? $"Selected: {selectedOptions[0]}"
            : $"Selected:\n- {string.Join("\n- ", selectedOptions)}";
    }

    private static List<string> CollectSelectedObjectCollectionModOptionNames(ObjectCollectionModSettingsGroup group)
        => group.Options
            .Where(static option => option.Selected)
            .Select(static option => option.Name)
            .ToList();

}
