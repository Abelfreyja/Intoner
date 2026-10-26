using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Intoner.Displays;
using Intoner.Objects.UI.Components;
using Intoner.Scene;
using Intoner.Services.Gpu;
using System.Numerics;

namespace Intoner.Objects.UI;

internal sealed partial class SceneItemInspector
{
    private readonly List<EditorIconButton.Item> _displayActions = [];

    private void DrawDisplayInspectorHero(DisplaySnapshot selected)
    {
        bool inputActive = _sceneInputService.IsActiveFor(selected.Id);
        _displayActions.Clear();
        _displayActions.Add(new("displayInteract", FontAwesomeIcon.MousePointer,
            inputActive ? "Stop Interacting With Display" : "Interact With Display",
            _sceneInputService.CanActivate(selected.Id) || inputActive, inputActive));
        _displayActions.Add(new("displayDuplicate", FontAwesomeIcon.Copy, "Duplicate Display"));
        _displayActions.Add(new("displayVisibility", selected.Visible ? FontAwesomeIcon.EyeSlash : FontAwesomeIcon.Eye,
            selected.Visible ? "Hide Display" : "Show Display"));
        _displayActions.Add(new("displayLock", selected.Locked ? FontAwesomeIcon.Unlock : FontAwesomeIcon.Lock,
            selected.Locked ? "Unlock Display" : "Lock Display"));
        _displayActions.Add(new("displayMoveToPlayer", FontAwesomeIcon.Running, "Move Display To Player"));
        _displayActions.Add(new("displayRemove", FontAwesomeIcon.Trash, "Remove Display",
            IconColor: ThemeColors.DimRed, TooltipTitleColor: ThemeColors.DimRed));
        EditorHeroCard.Draw(
            "display-inspector-hero",
            new EditorHeroCard.Content(
                FontAwesomeIcon.SlidersH,
                "Edit Display",
                SceneItemPresentation.BuildDisplayStatus(selected),
                ThemeColors.AccentPrimary,
                null),
            new EditorHeroCard.Actions(_displayActions, id => ExecuteDisplayInspectorAction(selected, id)));
    }

    private void ExecuteDisplayInspectorAction(DisplaySnapshot selected, string id)
    {
        switch (id)
        {
            case "displayInteract":
                if (_sceneInputService.IsActiveFor(selected.Id))
                    _sceneInputService.Deactivate();
                else
                    _ = _sceneInputService.TryActivate(selected.Id,
                        EditorInputUtility.CaptureScenePointerButtons(includeAuxiliaryButtons: true));
                break;
            case "displayDuplicate":
                _ = _historyCoordinator.TryDuplicateSceneItems([selected]);
                break;
            case "displayVisibility":
                _ = _sceneCommands.TrySetSceneItemsVisibleWithHistory([selected], !selected.Visible);
                break;
            case "displayLock":
                _ = _sceneCommands.TrySetSceneItemsLockedWithHistory([selected], !selected.Locked);
                break;
            case "displayMoveToPlayer":
                _ = _historyCoordinator.TryMoveItemToPlayer(selected.Id);
                break;
            case "displayRemove":
                _ = _historyCoordinator.TryRemoveSceneItems([selected]);
                break;
        }
    }

    private void DrawDisplayInspectorDetails(DisplaySnapshot selected)
    {
        EditorCard.DrawRemainingSpaceScrollCard("display-inspector-details", "##displayInspectorScroll", _editorOverlayLayer, _ =>
        {
            ImGui.TextUnformatted(selected.Name);
            ImGui.TextDisabled($"Display | created {EditorTimestampFormatter.FormatFull(selected.CreatedAtUtc)}");
            ImGuiHelpers.ScaledDummy(6f);
            DrawDisplaySnapshotEditor(selected);
        });
    }


    private void DrawDisplaySnapshotEditor(DisplaySnapshot snapshot)
    {
        DisplaySnapshot updated = snapshot;
        using (var commonTable = EditorPropertyTable.Begin("displayCommon"))
        {
            if (commonTable)
            {
                string name = updated.Name;
                EditorPropertyTable.NextRow("Name");
                if (ImGui.InputText("##displayName", ref name, 128))
                {
                    updated = updated with { Name = name };
                    _historyCoordinator.ApplyInspectorSnapshotEdit(
                        "DisplayName",
                        SceneHistoryKind.Organization,
                        "Rename Display",
                        snapshot,
                        updated);
                }

                bool visible = updated.Visible;
                if (EditorPropertyTable.Checkbox("displayVisible", "Visible", ref visible))
                {
                    updated = updated with { Visible = visible };
                    _historyCoordinator.ApplyInspectorSnapshotEdit(
                        "DisplayVisible",
                        SceneHistoryKind.Visibility,
                        "Change Display Visibility",
                        snapshot,
                        updated,
                        true);
                }

                bool locked = updated.Locked;
                if (EditorPropertyTable.Checkbox("displayLocked", "Locked", ref locked))
                {
                    updated = updated with { Locked = locked };
                    _historyCoordinator.ApplyInspectorSnapshotEdit(
                        "DisplayLocked",
                        SceneHistoryKind.Organization,
                        "Change Display Lock",
                        snapshot,
                        updated,
                        true);
                }

                SceneTransform transform = _transformEditor.DrawInspectorRows(snapshot);

                if (transform != updated.Transform)
                {
                    updated = updated with { Transform = transform };
                }
            }
        }

        DisplaySettings settings = updated.Settings;
        DrawDisplaySettings(
            "selected",
            ref settings,
            (editId, title, updatedSettings, recordImmediately) =>
            {
                updated = updated with { Settings = updatedSettings };
                _historyCoordinator.ApplyInspectorSnapshotEdit(
                    editId,
                    SceneHistoryKind.Appearance,
                    title,
                    snapshot,
                    updated,
                    recordImmediately);
            });
    }

    private void DrawDisplaySettings(
        string id,
        ref DisplaySettings settings,
        Action<string, string, DisplaySettings, bool>? onChanged)
    {
        using var settingsTable = EditorPropertyTable.Begin($"displaySettings_{id}");
        if (!settingsTable)
        {
            return;
        }

        WindowsCaptureTargetDescriptor target = settings.Target;
        if (DrawDisplayCaptureTargetRow(id, ref target))
        {
            settings = settings with { Target = target };
            onChanged?.Invoke("DisplaySource", "Change Display Source", settings, true);
        }

        Vector2 size = settings.Size;
        EditorPropertyTable.NextRow("Surface Size");
        if (ImGui.DragFloat2($"##displaySize_{id}", ref size, 0.01f, 0.1f, 100f, "%.3f"))
        {
            settings = settings with { Size = size };
            onChanged?.Invoke("DisplaySize", "Resize Display", settings, false);
        }

        Vector4 tint = settings.Tint;
        EditorPropertyTable.NextRow("Tint");
        if (ImGui.ColorEdit4($"##displayTint_{id}", ref tint, ImGuiColorEditFlags.Float))
        {
            settings = settings with { Tint = tint };
            onChanged?.Invoke("DisplayTint", "Change Display Tint", settings, false);
        }

        Vector2 cropMin = new(settings.UvRect.X, settings.UvRect.Y);
        EditorPropertyTable.NextRow("Crop Min");
        if (ImGui.DragFloat2($"##displayCropMin_{id}", ref cropMin, 0.005f, 0f, 1f, "%.3f"))
        {
            settings = settings with { UvRect = settings.UvRect with { X = cropMin.X, Y = cropMin.Y } };
            onChanged?.Invoke("DisplayCropMin", "Change Display Crop", settings, false);
        }

        Vector2 cropMax = new(settings.UvRect.Z, settings.UvRect.W);
        EditorPropertyTable.NextRow("Crop Max");
        if (ImGui.DragFloat2($"##displayCropMax_{id}", ref cropMax, 0.005f, 0f, 1f, "%.3f"))
        {
            settings = settings with { UvRect = settings.UvRect with { Z = cropMax.X, W = cropMax.Y } };
            onChanged?.Invoke("DisplayCropMax", "Change Display Crop", settings, false);
        }

        bool twoSided = settings.TwoSided;
        if (EditorPropertyTable.Checkbox($"displayTwoSided_{id}", "Two Sided", ref twoSided))
        {
            settings = settings with { TwoSided = twoSided };
            onChanged?.Invoke("DisplayTwoSided", "Change Display Sides", settings, true);
        }

        bool useSceneDepth = settings.UseSceneDepth;
        if (EditorPropertyTable.Checkbox($"displaySceneDepth_{id}", "Scene Occlusion", ref useSceneDepth))
        {
            settings = settings with { UseSceneDepth = useSceneDepth };
            onChanged?.Invoke("DisplaySceneDepth", "Change Display Occlusion", settings, true);
        }

        bool nameplatesAboveDisplay = settings.NameplatesAboveDisplay;
        if (EditorPropertyTable.Checkbox(
                $"displayNameplatesAboveDisplay_{id}",
                "Nameplates Above Display",
                ref nameplatesAboveDisplay))
        {
            settings = settings with { NameplatesAboveDisplay = nameplatesAboveDisplay };
            onChanged?.Invoke("DisplayNameplateOrder", "Change Nameplate Order", settings, true);
        }
    }

    private bool DrawDisplayCaptureTargetRow(string id, ref WindowsCaptureTargetDescriptor target)
    {
        IReadOnlyList<WindowsCaptureTarget> targets = _captureTargetService.GetTargets();
        WindowsCaptureTargetDescriptor currentTarget = target;
        WindowsCaptureTarget resolvedTarget = default;
        bool available = currentTarget.IsConfigured
                         && _captureTargetService.TryResolve(
                             currentTarget,
                             out resolvedTarget);
        string preview = "<none>";
        if (target.IsConfigured)
        {
            preview = available ? target.Name : $"{target.Name} (unavailable)";
        }

        EditorPropertyTable.NextRow("Capture Source");
        using var combo = ImRaii.Combo($"##displaySource_{id}", preview);
        if (!combo)
        {
            return false;
        }

        bool changed = false;
        if (ImGui.Selectable($"<none>##displaySourceNone_{id}", !target.IsConfigured))
        {
            target = new WindowsCaptureTargetDescriptor();
            changed = true;
        }

        foreach (WindowsCaptureTarget candidate in targets)
        {
            bool selected = available
                ? candidate.Handle == resolvedTarget.Handle
                  && candidate.Target.Kind == resolvedTarget.Target.Kind
                : target.HasSameCaptureSource(candidate.Target);
            string kind = candidate.Target.Kind == WindowsCaptureTargetKind.Monitor ? "Monitor" : "Window";
            if (ImGui.Selectable(
                    $"[{kind}] {candidate.Target.Name}##displaySource_{id}:{candidate.Handle:X}",
                    selected))
            {
                target = candidate.Target;
                changed = true;
            }

            if (selected)
            {
                ImGui.SetItemDefaultFocus();
            }
        }

        return changed;
    }
}
