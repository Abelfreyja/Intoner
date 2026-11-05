using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Intoner.Objects.Models;
using Intoner.Objects.UI.Components;
using Intoner.Services.Input;
using Intoner.UI.Performance;
using Intoner.UI.Windows;
using System.Numerics;

namespace Intoner.Objects.UI;

internal sealed class WorldAssetPickerWindow : IntonerWindow
{
    private readonly EditorWorldPicker       _picker;
    private readonly EditorWindow            _editor;
    private readonly WorldAssetPickerBrowser _browser;
    private readonly WorldAssetPickerToolbar _toolbar;
    private bool _expanded;
    private IDisposable? _padding;

    public WorldAssetPickerWindow(IntonerUiPerformanceService performance, EditorWorldPicker picker,
        EditorWindow editor, WorldAssetPickerToolbar toolbar)
        : base("World Picker##intonerWorldPicker", performance)
    {
        _picker  = picker;
        _editor  = editor;
        _toolbar = toolbar;
        _browser = new(picker);
        IsOpen = true;
        ShowCloseButton = false;
        RespectCloseHotkey = false;
        DisableWindowSounds = true;
        DisableFadeInFadeOut = true;
        Flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoScrollbar
            | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoMove
            | ImGuiWindowFlags.NoFocusOnAppearing;
    }

    public override void Update()
    {
        _picker.BeginFrame();

        if (!_picker.IsOpen)
        {
            _browser.Clear();
        }
    }

    public override bool DrawConditions()
        => _picker.IsOpen;

    public override void PreDraw()
    {
        ImGuiHelpers.ForceNextWindowMainViewport();

        if (_picker.ConsumeFocusRequest())
        {
            BringToFront();
        }

        float scale = ImGuiHelpers.GlobalScale;
        ImGui.SetNextWindowPos(ImGui.GetMainViewport().Pos + new Vector2(30f, 80f) * scale, ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new Vector2(366f * scale, WorldAssetPickerToolbar.Height + (_expanded ? WorldAssetPickerBrowser.Height + 6f * scale : 0f)));
        _padding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, new Vector2(6f) * scale);
    }

    public override void PostDraw()
    {
        _padding?.Dispose();
        _padding = null;
    }

    protected override void DrawContent()
    {
        if (_picker.State.IsPicking)
        {
            if (ImGui.IsKeyPressed(ImGuiKey.Escape, false) || !DesktopInputGuard.CanRoutePointerToCurrentProcess())
            {
                _picker.Cancel();
            }
            else
            {
                ImGuiViewportPtr viewport = ImGui.GetMainViewport();
                bool canPick = !_editor.IsInteractionBlocked && ImGui.IsMousePosValid() && !ImGui.IsAnyItemActive()
                    && !ImGui.IsWindowHovered(ImGuiHoveredFlags.AnyWindow) && !ImGui.GetIO().WantCaptureMouse
                    && !ImGui.IsMouseDown(ImGuiMouseButton.Right) && !ImGui.IsMouseDown(ImGuiMouseButton.Middle);
                _picker.ProcessPointer(new(viewport.Pos, viewport.Pos + viewport.Size), ImGui.GetIO().MousePos, canPick);
            }
        }
        else
        {
            _picker.RefreshSelection();
        }

        ImGuiViewportPtr bounds = ImGui.GetMainViewport();
        Vector2 max = Vector2.Max(bounds.Pos, bounds.Pos + bounds.Size - ImGui.GetWindowSize());
        ImGui.SetWindowPos(Vector2.Clamp(ImGui.GetWindowPos(), bounds.Pos, max));

        if (_toolbar.Draw(!_editor.IsInteractionBlocked, ref _expanded))
        {
            _editor.OpenWorkspace(WorkspaceMode.CatalogCreate);
        }

        if (_expanded)
        {
            ImGui.SetCursorPos(new Vector2(6f * ImGuiHelpers.GlobalScale, WorldAssetPickerToolbar.Height));
            _browser.Draw();
        }
    }
}
