using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Intoner.UI.Performance;
using Intoner.UI.Windows;

namespace Intoner.Objects.UI;

internal sealed class EditorBackgroundWindow : IntonerWindow
{
    private const ImGuiWindowFlags BackgroundWindowFlags =
        ImGuiWindowFlags.NoDecoration
      | ImGuiWindowFlags.NoInputs
      | ImGuiWindowFlags.NoNav
      | ImGuiWindowFlags.NoSavedSettings
      | ImGuiWindowFlags.NoBackground
      | ImGuiWindowFlags.NoBringToFrontOnFocus
      | ImGuiWindowFlags.NoFocusOnAppearing;

    private readonly EditorWindow _editor;

    public EditorBackgroundWindow(IntonerUiPerformanceService uiPerformance, EditorWindow editor)
        : base("Intoner Background##intonerEditorBackground", uiPerformance)
    {
        _editor = editor;
        Flags = BackgroundWindowFlags;
        IsOpen = true;
        ShowCloseButton = false;
        RespectCloseHotkey = false;
        DisableWindowSounds = true;
        DisableFadeInFadeOut = true;
    }

    public override void Update()
        => _editor.UpdateShortcutsWithoutEditor();

    public override bool DrawConditions()
        => _editor.ShouldDrawSceneToolsWithoutEditor;

    public override void PreDraw()
    {
        base.PreDraw();
        ImGuiViewportPtr viewport = ImGui.GetMainViewport();
        ImGuiHelpers.ForceNextWindowMainViewport();
        ImGui.SetNextWindowPos(viewport.Pos, ImGuiCond.Always);
        ImGui.SetNextWindowSize(viewport.Size, ImGuiCond.Always);
    }

    protected override void DrawContent()
        => _editor.DrawSceneToolsWithoutEditor();
}
