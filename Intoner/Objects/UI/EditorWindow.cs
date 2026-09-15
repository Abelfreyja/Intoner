using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Intoner.Objects.Catalog;
using Intoner.Objects.Models;
using Intoner.Objects.Runtime;
using Intoner.Objects.UI.Components;
using Intoner.Objects.UI.Services;
using Intoner.Objects.UI.Services.Backdrop;
using Intoner.Objects.UI.TitleBar;
using Intoner.Services;
using Intoner.Services.Configuration;
using Intoner.Services.Input;
using Intoner.UI.Performance;
using Intoner.UI.Windows;
using System.Numerics;

namespace Intoner.Objects.UI;

internal sealed class EditorWindow : IntonerWindow, IDisposable
{
    internal bool ShouldDrawSceneToolsWithoutEditor
        => !_editorContentDrawnThisFrame
           && _configurationService.Current.Ui.ShowSceneToolsWithoutEditor;

    private readonly EditorWorkspaceHost            _workspaces;
    private readonly IObjectSceneView               _sceneView;
    private readonly IHistoryCoordinator            _historyCoordinator;
    private readonly EditorLoadCoordinator          _editorLoad;
    private readonly IShortcutService               _shortcuts;
    private readonly IIntonerConfigurationService   _configurationService;
    private readonly EditorInteraction              _interaction;
    private readonly EditorSceneOverlay             _sceneOverlay;
    private readonly EditorShortcutHandler          _shortcutHandler;
    private readonly EditorStartupView              _startupView;
    private readonly EditorOverlayLayer             _editorOverlayLayer;
    private readonly BackdropRenderer               _windowBackdropRenderer;
    private readonly EditorTitleBarIndicatorService _titleBarIndicatorService;
    private readonly TitleBarIconRenderer           _titleBarIconRenderer;
    private readonly TitleBarWindowMetrics          _titleBarWindowMetrics;
    private readonly IDisposable                    _mainWindowRequestSubscription;
    private bool _editorContentDrawnThisFrame;
    private bool _editorContentWasDrawn;
    private IDisposable? _windowBackgroundColorScope;

    public EditorWindow(
        IntonerUiPerformanceService uiPerformance,
        IntonerBuildInfoService buildInfo,
        EditorWorkspaceHost workspaces,
        EditorInteraction interaction,
        EditorSceneOverlay sceneOverlay,
        EditorShortcutHandler shortcutHandler,
        EditorStartupView startupView,
        EditorOverlayLayer editorOverlayLayer,
        IObjectSceneView sceneView,
        IHistoryCoordinator historyCoordinator,
        EditorLoadCoordinator editorLoad,
        IShortcutService shortcuts,
        IIntonerConfigurationService configurationService,
        IIntonerMediator mediator,
        BackdropRenderer windowBackdropRenderer,
        TitleBarIconRenderer titleBarIconRenderer,
        EditorTitleBarIndicatorService titleBarIndicatorService,
        TitleBarWindowMetrics titleBarWindowMetrics)
        : base(buildInfo.WindowName, uiPerformance)
    {
        _workspaces                    = workspaces;
        _sceneView                     = sceneView;
        _historyCoordinator            = historyCoordinator;
        _editorLoad                    = editorLoad;
        _shortcuts                     = shortcuts;
        _configurationService          = configurationService;
        _sceneOverlay                  = sceneOverlay;
        _shortcutHandler               = shortcutHandler;
        _startupView                   = startupView;
        _editorOverlayLayer            = editorOverlayLayer;
        _interaction                   = interaction;
        _windowBackdropRenderer        = windowBackdropRenderer;
        _titleBarIconRenderer          = titleBarIconRenderer;
        _titleBarIndicatorService      = titleBarIndicatorService;
        _titleBarWindowMetrics         = titleBarWindowMetrics;
        _mainWindowRequestSubscription = mediator.Subscribe<IntonerMainWindowRequest>(this, HandleMainWindowRequest);

        RespectCloseHotkey = true;
        ShowCloseButton = true;
        WindowBuilder.For(this)
            .SetSizeConstraints(new Vector2(930f, 695f), new Vector2(1200f, 760f))
            .AddFlags(ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
            .AddTitleBarButton(FontAwesomeIcon.Book, "Show Splash Screen", _startupView.ShowSplashScreen)
            .AddTitleBarButton(FontAwesomeIcon.ArrowsAlt, "Toolbar Dock Position", _workspaces.OpenToolbarDockMenu)
            .Apply();
    }

    internal void UpdateShortcutsWithoutEditor()
    {
        if (_editorContentDrawnThisFrame)
        {
            return;
        }

        if (_configurationService.Current.Ui.EnableShortcutsWithoutEditor)
        {
            _shortcutHandler.HandleEditorShortcuts();
            return;
        }

        _shortcuts.DeactivateAll();
    }

    internal void DrawSceneToolsWithoutEditor()
    {
        EditorSceneFrame frame = _sceneOverlay.PrepareFrame(false, processPointer: false);
        _sceneOverlay.DrawFrame(frame, GetEditorWindowArea(), drawGizmo: true);
    }

    private EditorScreenArea? GetEditorWindowArea()
    {
        if (!IsOpen)
        {
            return null;
        }

        ImGuiWindowPtr window = ImGuiP.FindWindowByName(WindowName);
        return window.IsNull
            ? null
            : new EditorScreenArea(window.Pos, window.Pos + window.Size);
    }

    public override void OnOpen()
    {
        _shortcuts.DeactivateAll();
        base.OnOpen();
    }

    public override void OnClose()
    {
        _editorContentDrawnThisFrame = false;
        _editorContentWasDrawn = false;
        _shortcuts.DeactivateAll();
        _sceneOverlay.Deactivate();
        _interaction.Dialog.Dismiss();
        _startupView.DismissSplashScreen();
        _startupView.SetSplashScreenStatus(null, isError: false);
        base.OnClose();
    }

    public void Dispose()
    {
        _mainWindowRequestSubscription.Dispose();
        _sceneOverlay.Deactivate();
        _windowBackgroundColorScope?.Dispose();
        _windowBackgroundColorScope = null;
    }

    private void HandleMainWindowRequest(IntonerMainWindowRequest request)
    {
        switch (request.Kind)
        {
            case IntonerMainWindowRequestKind.Toggle:
                Toggle();
                return;
            case IntonerMainWindowRequestKind.OpenSettings:
                OpenWorkspace(WorkspaceMode.Settings);
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(request), request.Kind, null);
        }
    }

    private void OpenWorkspace(WorkspaceMode workspaceMode)
    {
        IsOpen = true;
        _workspaces.SetWorkspaceMode(workspaceMode);
        _startupView.DismissSplashScreen();
    }

    public override void Update()
    {
        _editorContentDrawnThisFrame = false;
        RespectCloseHotkey = !_sceneOverlay.IsSelecting;
    }

    public override void PreDraw()
    {
        base.PreDraw();
        _editorOverlayLayer.BackgroundColor = ThemeColors.WindowBg;
        _windowBackgroundColorScope?.Dispose();
        _windowBackgroundColorScope = ImRaii.PushColor(ImGuiCol.WindowBg, Vector4.Zero);
    }

    public override void PostDraw()
    {
        if (!_editorContentDrawnThisFrame && _editorContentWasDrawn)
        {
            _sceneOverlay.Deactivate();
        }

        _editorContentWasDrawn = _editorContentDrawnThisFrame;

        DrawTitleBar();

        _windowBackgroundColorScope?.Dispose();
        _windowBackgroundColorScope = null;

        base.PostDraw();
    }

    protected override void DrawContent()
    {
        _editorContentDrawnThisFrame = true;

        if (!ImGui.IsAnyItemActive())
        {
            _historyCoordinator.CommitPendingInspectorEdits();
        }

        bool dialogOpen = _interaction.Dialog.IsOpen;
        using (ImRaii.Disabled(dialogOpen))
        {
            DrawEditorContent();
        }

        if (_interaction.Dialog.IsOpen && EditorLayout.TryGetWindowBodyArea(out EditorOverlayArea area))
        {
            _interaction.Dialog.Draw(_editorOverlayLayer, area);
        }
    }

    private void DrawEditorContent()
    {
        Vector2 editorWindowMin = ImGui.GetWindowPos();
        var editorWindowArea = new EditorScreenArea(editorWindowMin, editorWindowMin + ImGui.GetWindowSize());

        _editorLoad.EnsureLoaded();
        if (!_editorLoad.TryGetData(out EditorLoadData? loadData, out var loadStatus))
        {
            _sceneOverlay.Deactivate();
            _shortcuts.DeactivateAll();
            DrawWindowBackgroundGlass();
            _startupView.DrawEditorLoadingScreen(loadStatus);
            return;
        }

        ObjectCatalogData catalog = loadData.Catalog;

        bool showSplashScreen = _startupView.ShouldShowSplashScreen();
        _workspaces.RefreshContext();
        if (!showSplashScreen && !_interaction.Dialog.IsOpen)
        {
            _shortcutHandler.HandleEditorShortcuts();
        }
        else
        {
            _shortcuts.DeactivateAll();
        }

        bool sceneInteractionAllowed = !showSplashScreen && !_interaction.Dialog.IsOpen;
        EditorSceneFrame sceneFrame = _sceneOverlay.PrepareFrame(sceneInteractionAllowed);
        DrawWindowBackgroundGlass();

        using (IntonerTooltip.Suppress(!sceneInteractionAllowed))
        {
            _workspaces.Draw(sceneFrame, catalog, showSplashScreen);
        }

        if (showSplashScreen)
        {
            _startupView.DrawSplashScreen();
            return;
        }

        _sceneOverlay.DrawFrame(sceneFrame, editorWindowArea, !_interaction.Dialog.IsOpen);
    }

    private void DrawTitleBar()
    {
        if (!_titleBarWindowMetrics.TryCreateContext(this, out TitleBarRenderContext context))
        {
            return;
        }

        float titleBarIconRightEdge = _titleBarIconRenderer.Draw(context);
        DrawTitleBarIndicators(context, titleBarIconRightEdge);
    }

    private void DrawTitleBarIndicators(TitleBarRenderContext context, float titleBarIconRightEdge)
    {
        IReadOnlyList<ObjectSnapshot> objects = _sceneView.GetPlacedObjectSnapshots();
        TitleBarIndicatorContext indicatorContext = TitleBarIndicatorContext.Create(objects);
        IReadOnlyList<TitleBarIndicator> indicators = _titleBarIndicatorService.Build(indicatorContext);
        TitleBarIndicatorRenderer.Draw(
            context,
            indicators,
            reservedLeftEdge: titleBarIconRightEdge);
    }

    private static GlassEffect.Style CreateWindowBodyGlassStyle(Vector4 backgroundColor)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var windowTint = ThemeColors.WithAlpha(backgroundColor, 1f);
        var blurBase = ThemeColors.Color(0.45f, 0.45f, 0.45f, 1f);
        var blurTint = Vector4.Lerp(blurBase, windowTint, 0.30f);
        var edgeBase = Vector4.Lerp(blurTint, ThemeColors.AccentBlue, 0.08f);
        return new GlassEffect.Style
        {
            TintColor = new Vector4(blurTint.X, blurTint.Y, blurTint.Z, 0.94f),
            EdgeColor = new Vector4(edgeBase.X, edgeBase.Y, edgeBase.Z, 0.30f),
            BlurMix = 0.94f,
            DistortionStrength = 16f * scale,
            HighlightStrength = 0.34f,
            FrostStrength = 0.18f,
            NoiseAmount = 0.0035f,
            ShadowStrength = 0.26f,
            EdgeBand = 34f * scale,
            ChromaticAberration = 4.8f * scale,
        };
    }

    private void DrawWindowBackgroundGlass()
    {
        if (!EditorLayout.TryGetWindowBodyArea(out EditorOverlayArea area))
        {
            return;
        }

        var drawList = ImGui.GetWindowDrawList();

        drawList.PushClipRect(area.Min, area.Max, false);
        try
        {
            if (IsWindowBackgroundBlurActive())
            {
                _windowBackdropRenderer.GetEffect<GlassEffect>().DrawRegion(
                    drawList,
                    area.Min,
                    area.Max,
                    area.Rounding,
                    CreateWindowBodyGlassStyle(_editorOverlayLayer.BackgroundColor),
                    area.RoundingFlags);
            }
            else
            {
                drawList.AddRectFilled(
                    area.Min,
                    area.Max,
                    ImGui.GetColorU32(_editorOverlayLayer.BackgroundColor),
                    area.Rounding,
                    area.RoundingFlags);
            }
        }
        finally
        {
            drawList.PopClipRect();
        }
    }

    private static bool IsWindowBackgroundBlurActive()
    {
        var currentViewport = ImGui.GetWindowViewport();
        var mainViewport = ImGui.GetMainViewport();
        return !currentViewport.IsNull
            && !mainViewport.IsNull
            && currentViewport.ID == mainViewport.ID;
    }
}
