using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Intoner.Objects.UI.Components;
using Intoner.Objects.Utils;
using Intoner.Scene;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Intoner.Objects.UI.Services;

/// <summary> scene click and box selection without changing selection during a pending drag </summary>
internal sealed class EditorSceneSelection
{
    [StructLayout(LayoutKind.Auto)]
    internal readonly record struct PointerState(
        EditorScreenArea Viewport,
        Vector2 Position,
        ScenePointerButtons Buttons,
        bool Pressed,
        bool ShouldCancel,
        bool ToggleSelection,
        float DragThreshold,
        bool BoxSelectionEnabled);

    private readonly ISceneSelectionService _picker;
    private readonly EditorInteraction _interaction;
    private EditorScreenArea _viewport;
    private Vector2 _start;
    private Vector2 _end;
    private bool _toggleSelection;
    private int _selectionRevision;

    public bool IsActive { get; private set; }
    public bool IsDragging { get; private set; }
    public EditorScreenArea Area => new(Vector2.Min(_start, _end), Vector2.Max(_start, _end));

    public EditorSceneSelection(ISceneSelectionService picker, EditorInteraction interaction)
    {
        _picker = picker;
        _interaction = interaction;
    }

    /// <summary> processes a captured viewport sample and commits selection only on a click or completed drag </summary>
    public void ProcessPointer(in PointerState pointer)
    {
        bool leftDown = (pointer.Buttons & ScenePointerButtons.Left) != ScenePointerButtons.None;
        bool auxiliaryButton = (pointer.Buttons & ~ScenePointerButtons.Left) != ScenePointerButtons.None;
        if (pointer.ShouldCancel || auxiliaryButton
            || !NumericsUtility.IsFinite(pointer.Position)
            || !NumericsUtility.IsFinite(pointer.Viewport.Min) || !NumericsUtility.IsFinite(pointer.Viewport.Max)
            || !EditorInputUtility.HasArea(pointer.Viewport.Min, pointer.Viewport.Max))
        {
            Cancel();
            return;
        }

        if (IsActive)
        {
            if (!pointer.BoxSelectionEnabled || _viewport != pointer.Viewport || _selectionRevision != _interaction.Selection.Revision)
            {
                Cancel();
                return;
            }

            _end = Vector2.Clamp(pointer.Position, _viewport.Min, _viewport.Max);
            float threshold = MathF.Max(1f, pointer.DragThreshold);
            IsDragging |= Vector2.DistanceSquared(_start, _end) >= threshold * threshold;
            if (leftDown)
            {
                return;
            }

            bool apply = IsDragging;
            Cancel();
            if (apply && _picker.TrySelectActiveItems(_viewport.Min, _viewport.Max - _viewport.Min, _start, _end, out var hits))
            {
                ApplySelection(hits, _toggleSelection);
            }

            return;
        }

        if (!pointer.Pressed || !leftDown || !pointer.Viewport.Contains(pointer.Position)
            || !_picker.TrySelectActiveItems(pointer.Viewport.Min, pointer.Viewport.Max - pointer.Viewport.Min,
                pointer.Position, pointer.Position, out var clickedItems))
        {
            return;
        }

        if (clickedItems.Count > 0)
        {
            SceneItemSnapshot item = clickedItems[0];
            if (!item.Locked)
            {
                _interaction.SelectionChanged(_interaction.Selection.TrySelect(item.Id, pointer.ToggleSelection));
            }

            return;
        }

        if (!pointer.BoxSelectionEnabled)
        {
            return;
        }

        _viewport = pointer.Viewport;
        _start = _end = pointer.Position;
        _toggleSelection = pointer.ToggleSelection;
        _selectionRevision = _interaction.Selection.Revision;
        IsActive = true;
    }

    public void Cancel()
    {
        IsActive = false;
        IsDragging = false;
    }

    public void Draw(ImDrawListPtr drawList)
    {
        if (!IsDragging)
        {
            return;
        }

        EditorScreenArea area = Area;
        drawList.AddRectFilled(area.Min, area.Max, ImGui.GetColorU32(ThemeColors.AccentBlue with { W = 0.12f }));
        drawList.AddRect(area.Min, area.Max, ImGui.GetColorU32(ThemeColors.AccentBlue with { W = 0.85f }),
            0f, ImDrawFlags.None, ImGuiHelpers.GlobalScale);
    }

    private void ApplySelection(IReadOnlyList<SceneItemSnapshot> hits, bool toggleSelection)
    {
        List<Guid> ids = new(hits.Count);
        foreach (SceneItemSnapshot hit in hits)
        {
            if (!hit.Locked)
            {
                ids.Add(hit.Id);
            }
        }

        _interaction.SelectionChanged(toggleSelection
            ? _interaction.Selection.TryToggleSelection(ids)
            : _interaction.Selection.TryReplaceSelection(ids));
    }
}
