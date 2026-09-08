using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Intoner.Objects.Catalog;
using Intoner.Objects.Models;
using Intoner.Objects.Runtime;
using Intoner.Objects.UI.Components;
using Intoner.Objects.UI.Docking;
using Intoner.Objects.UI.Services;
using Intoner.Objects.UI.Settings;
using Intoner.Services.Configuration;
using Intoner.Services.Input;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Intoner.Objects.UI;

internal sealed class EditorWorkspaceHost
{
    private readonly IShortcutService             _shortcuts;
    private readonly IIntonerConfigurationService _configurationService;
    private readonly SettingsPage                 _settingsPage;
    private readonly EditorInteraction            _interaction;
    private readonly LayoutWorkspace              _layouts;
    private readonly HistoryWorkspace             _history;
    private readonly CreateWorkspace              _create;
    private readonly ObjectCatalogBrowser         _catalogBrowser;
    private readonly CollectionsWorkspace         _collections;
    private readonly SceneWorkspace               _sceneWorkspace;
    private readonly SceneItemInspector           _inspector;
    private readonly EditorToolbar                _toolbar;
    private readonly DebugWorkspace               _debug;
    private readonly EditorOverlayLayer           _editorOverlayLayer;
    private readonly CreateBrowserSelection       _browserSelection;
    private readonly IObjectKindService           _objectKindService;
    private readonly IObjectLayoutManager         _layoutManager;
    private static readonly WorkspaceModeAction[] WorkspaceModeActions =
    [
        new("##objectModeCatalogCreate", FontAwesomeIcon.FolderOpen, "Catalog + Create", WorkspaceMode.CatalogCreate),
        new("##objectModePlacedInspector", FontAwesomeIcon.Edit, "Placed + Edit", WorkspaceMode.PlacedInspector),
        new("##objectModeLayouts", FontAwesomeIcon.Folder, "Layouts", WorkspaceMode.LayoutManager),
        new("##objectModeCollections", FontAwesomeIcon.Swatchbook, "Collections", WorkspaceMode.Collections),
        new("##objectModeHistory", FontAwesomeIcon.History, "History", WorkspaceMode.History, FocusCurrentHistoryEntry: true),
        new("##objectModeSettings", FontAwesomeIcon.Cog, "Settings", WorkspaceMode.Settings),
    ];
    private static readonly WorkspaceModeAction[] WorkspaceModeActionsWithDebug =
    [
        .. WorkspaceModeActions,
        new("##objectModeDebug", FontAwesomeIcon.Bug, "Debug", WorkspaceMode.Debug),
    ];
    private WorkspaceMode _workspaceMode = WorkspaceMode.CatalogCreate;
    private UiConfiguration.SplitRatios _workspaceSplits;
    private bool _workspaceSplitRatioDirty;

    public EditorWorkspaceHost(
        IShortcutService shortcuts,
        IIntonerConfigurationService configurationService,
        SettingsPage settingsPage,
        EditorInteraction interaction,
        LayoutWorkspace layouts,
        HistoryWorkspace history,
        CreateWorkspace create,
        ObjectCatalogBrowser catalogBrowser,
        CollectionsWorkspace collections,
        SceneWorkspace sceneWorkspace,
        SceneItemInspector inspector,
        EditorToolbar toolbar,
        DebugWorkspace debug,
        EditorOverlayLayer editorOverlayLayer,
        CreateBrowserSelection browserSelection,
        IObjectKindService objectKindService,
        IObjectLayoutManager layoutManager)
    {
        _shortcuts            = shortcuts;
        _configurationService = configurationService;
        _settingsPage         = settingsPage;
        _interaction          = interaction;
        _layouts              = layouts;
        _history              = history;
        _create               = create;
        _catalogBrowser       = catalogBrowser;
        _collections          = collections;
        _sceneWorkspace       = sceneWorkspace;
        _inspector            = inspector;
        _toolbar              = toolbar;
        _debug                = debug;
        _editorOverlayLayer   = editorOverlayLayer;
        _browserSelection     = browserSelection;
        _objectKindService    = objectKindService;
        _layoutManager        = layoutManager;
        _workspaceSplits      = configurationService.Current.Ui.WorkspaceSplits;
    }

    private void DrawToolbarLayout(EditorFrame frame)
    {
        EditorDockPanel toolbarPanel = new(
            "##intonerCommandSurfaceDockPanel",
            _toolbar.ResolveToolbarDockSlot(),
            _toolbar.ResolveToolbarPanelSize,
            context => _toolbar.DrawToolbarPanel(frame.Scene.Objects, frame.SelectedItem, frame.SceneFrame.ActiveSelection, context));
        EditorDockShell.Draw(
            [toolbarPanel],
            () => DrawDockCenterContent(frame));
    }

    private void DrawSettingsWorkspace()
        => _settingsPage.Draw();

    public void RefreshContext()
    {
        _history.RefreshHistoryContext();
        _browserSelection.NormalizeDraftKindForHousingMode();
    }

    public void Draw(EditorSceneFrame scene, ObjectCatalogData catalog, bool showSplashScreen)
    {
        _toolbar.DrawToolbarDockPopup();
        using (ImRaii.PushStyle(ImGuiStyleVar.DisabledAlpha, 1f))
        using (ImRaii.Disabled(showSplashScreen))
        {
            IReadOnlyList<ObjectLayoutSnapshot> layouts = _layoutManager.GetLayouts();
            _layouts.RefreshContext(layouts);
            EditorFrame frame = new(scene, catalog, _objectKindService.GetKindInfos(), layouts, _layoutManager.GetDefaultLayoutId());
            DrawToolbarLayout(frame);
        }
    }

    public void OpenToolbarDockMenu()
        => _toolbar.OpenDockMenu();

    [StructLayout(LayoutKind.Auto)]
    internal readonly record struct WorkspaceModeAction(string Id, FontAwesomeIcon Icon, string Label, WorkspaceMode Mode, bool FocusCurrentHistoryEntry = false);

    internal void SetWorkspaceMode(WorkspaceMode workspaceMode)
    {
        bool showDebugTab = _configurationService.Current.Ui.ShowDebugTab;
        if (!IsWorkspaceModeAvailable(workspaceMode, showDebugTab) || _workspaceMode == workspaceMode)
        {
            return;
        }

        _interaction.CommitPendingHistory();
        _shortcuts.DeactivateAll();
        _workspaceMode = workspaceMode;
    }

    private void DrawDockCenterContent(EditorFrame frame)
    {
        bool showDebugTab = _configurationService.Current.Ui.ShowDebugTab;
        NormalizeWorkspaceModeAvailability(showDebugTab);
        DrawWorkspaceStrip(showDebugTab);
        DrawBody(frame);
    }

    private static bool IsWorkspaceModeAvailable(WorkspaceMode workspaceMode, bool showDebugTab)
        => workspaceMode != WorkspaceMode.Debug || showDebugTab;

    private void NormalizeWorkspaceModeAvailability(bool showDebugTab)
    {
        if (!IsWorkspaceModeAvailable(_workspaceMode, showDebugTab))
        {
            SetWorkspaceMode(WorkspaceMode.CatalogCreate);
        }
    }

    private void DrawWorkspaceStrip(bool showDebugTab)
    {
        DrawWorkspaceModeActions(showDebugTab);
        DrawWorkspaceToolbarDivider();
    }

    private void DrawBody(EditorFrame frame)
    {
        if (_workspaceMode == WorkspaceMode.History)
        {
            _history.DrawHistoryWorkspace();
            return;
        }

        if (_workspaceMode == WorkspaceMode.Collections)
        {
            _collections.DrawCollectionsWorkspace(frame.Scene.ActiveObjectIds);
            return;
        }

        if (_workspaceMode == WorkspaceMode.Settings)
        {
            DrawSettingsWorkspace();
            return;
        }

        if (_workspaceMode == WorkspaceMode.Debug)
        {
            _debug.DrawDebugWorkspace();
            return;
        }

        WorkspaceMode splitWorkspace = _workspaceMode;
        EditorSplitPane.Update splitUpdate = EditorSplitPane.Draw(
            "##objectEditorBody",
            ImGui.GetContentRegionAvail(),
            new EditorSplitPane.Options(
                ResolveWorkspaceSplitRatio(splitWorkspace),
                UiConfiguration.SplitRatios.DefaultRatio,
                EditorLayout.Scaled(320f),
                EditorLayout.Scaled(360f),
                ThemeColors.AccentPrimary),
            () => DrawPrimaryWorkspacePane(frame),
            () => DrawSecondaryWorkspacePane(frame));
        ApplyWorkspaceSplitUpdate(splitWorkspace, splitUpdate);
    }

    private void DrawPrimaryWorkspacePane(EditorFrame frame)
    {
        switch (_workspaceMode)
        {
            case WorkspaceMode.CatalogCreate:
                _editorOverlayLayer.DrawChildPanel("##objectCatalogPanel", Vector2.Zero, true, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse, () => _catalogBrowser.DrawCatalogPanel(frame.Catalog), transparentBackground: false);
                break;
            case WorkspaceMode.PlacedInspector:
                _editorOverlayLayer.DrawChildPanel("##objectListPanel", Vector2.Zero, false, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse, () => _sceneWorkspace.DrawObjectListPanel(frame.Scene.Objects, frame.Scene.Displays, frame.Scene.ActiveObjectIds));
                break;
            case WorkspaceMode.LayoutManager:
                _editorOverlayLayer.DrawChildPanel("##objectLayoutListPanel", Vector2.Zero, false, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse, () => _layouts.DrawLayoutListPanel(frame.Layouts, frame.DefaultLayoutId));
                break;
        }
    }

    private void DrawSecondaryWorkspacePane(EditorFrame frame)
    {
        switch (_workspaceMode)
        {
            case WorkspaceMode.CatalogCreate:
                _editorOverlayLayer.DrawChildPanel("##objectCreatePanel", Vector2.Zero, false, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse, () => _create.DrawCreatePanel(frame.KindInfos));
                break;
            case WorkspaceMode.PlacedInspector:
                _inspector.DrawInspectorPanel(frame.Scene.Objects, frame.Scene.Displays, frame.Scene.ActiveObjectIds);
                break;
            case WorkspaceMode.LayoutManager:
                _editorOverlayLayer.DrawChildPanel(
                    "##objectLayoutInspectorPanel",
                    Vector2.Zero,
                    false,
                    ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse,
                    () => _layouts.DrawLayoutInspectorPanel(frame.Scene.Objects, frame.Scene.ActiveObjects, frame.Layouts, frame.DefaultLayoutId));
                break;
        }
    }

    private float ResolveWorkspaceSplitRatio(WorkspaceMode workspaceMode)
        => workspaceMode switch
        {
            WorkspaceMode.CatalogCreate    => _workspaceSplits.CatalogCreate,
            WorkspaceMode.PlacedInspector  => _workspaceSplits.PlacedInspector,
            WorkspaceMode.LayoutManager    => _workspaceSplits.LayoutManager,
            _                              => throw new ArgumentOutOfRangeException(nameof(workspaceMode), workspaceMode, null),
        };

    private void ApplyWorkspaceSplitUpdate(WorkspaceMode workspaceMode, EditorSplitPane.Update update)
    {
        if (update.Changed)
        {
            float ratio = UiConfiguration.SplitRatios.ClampRatio(update.Ratio);
            _workspaceSplits = workspaceMode switch
            {
                WorkspaceMode.CatalogCreate   => _workspaceSplits with { CatalogCreate = ratio },
                WorkspaceMode.PlacedInspector => _workspaceSplits with { PlacedInspector = ratio },
                WorkspaceMode.LayoutManager   => _workspaceSplits with { LayoutManager = ratio },
                _                             => throw new ArgumentOutOfRangeException(nameof(workspaceMode), workspaceMode, null),
            };
            _workspaceSplitRatioDirty = true;
        }

        if (!update.Commit || !_workspaceSplitRatioDirty)
        {
            return;
        }

        UiConfiguration.SplitRatios ratios = _workspaceSplits;
        _ = _configurationService.TryUpdate(configuration => configuration.Ui.WorkspaceSplits = ratios);
        _workspaceSplitRatioDirty = false;
    }

    private void DrawWorkspaceModeActions(bool showDebugTab)
    {
        ReadOnlySpan<WorkspaceModeAction> actions = showDebugTab
            ? WorkspaceModeActionsWithDebug
            : WorkspaceModeActions;
        Span<float> widths = stackalloc float[actions.Length];
        ResolveWorkspaceModeActionWidths(actions, widths, ImGui.GetContentRegionAvail().X);

        for (var actionIndex = 0; actionIndex < actions.Length; actionIndex++)
        {
            var action = actions[actionIndex];
            if (actionIndex > 0)
            {
                ImGui.SameLine();
            }

            if (!DrawIconTextButton(action.Id, action.Icon, action.Label, widths[actionIndex], _workspaceMode == action.Mode))
            {
                continue;
            }

            SetWorkspaceMode(action.Mode);
            if (action.FocusCurrentHistoryEntry)
            {
                _history.FocusCurrentEntry();
            }
        }
    }

    private static void DrawWorkspaceToolbarDivider()
    {
        var drawList = ImGui.GetWindowDrawList();
        var start = ImGui.GetCursorScreenPos();
        var width = MathF.Max(0f, ImGui.GetContentRegionAvail().X);
        var thickness = EditorLayout.ResolveDividerThickness();
        var color = ImGui.GetColorU32(ImGuiCol.Separator);
        var lineCenterY = start.Y + (thickness * 0.5f);

        drawList.AddLine(
            new Vector2(start.X, lineCenterY),
            new Vector2(start.X + width, lineCenterY),
            color,
            thickness);
        ImGui.Dummy(new Vector2(0f, ResolveWorkspaceToolbarDividerHeight()));
    }

    private static float ResolveWorkspaceToolbarDividerHeight()
        => EditorLayout.ResolveDividerThickness() + ImGui.GetStyle().ItemSpacing.Y;

    [StructLayout(LayoutKind.Auto)]
    internal readonly record struct IconTextButtonMetrics(
        float Width,
        float MinimumWidth,
        EditorIcon.Metrics Icon,
        float Spacing);

    private static void ResolveWorkspaceModeActionWidths(ReadOnlySpan<WorkspaceModeAction> actions, Span<float> widths, float availableWidth)
    {
        if (actions.IsEmpty)
        {
            return;
        }

        float spacingWidth = ImGui.GetStyle().ItemSpacing.X * (actions.Length - 1);
        float rowWidth = MathF.Max(actions.Length, availableWidth - spacingWidth);
        Span<float> desiredWidths = stackalloc float[actions.Length];
        Span<float> minimumWidths = stackalloc float[actions.Length];
        float desiredTotal = 0f;
        float minimumTotal = 0f;

        for (int index = 0; index < actions.Length; index++)
        {
            WorkspaceModeAction action = actions[index];
            IconTextButtonMetrics metrics = ResolveIconTextButtonMetrics(action.Icon, action.Label);
            desiredWidths[index] = metrics.Width;
            minimumWidths[index] = metrics.MinimumWidth;
            desiredTotal += metrics.Width;
            minimumTotal += metrics.MinimumWidth;
        }

        float extraWidth = desiredTotal <= rowWidth ? (rowWidth - desiredTotal) / actions.Length : 0f;
        float shrinkCapacity = desiredTotal - minimumTotal;
        float shrinkDeficit = desiredTotal - rowWidth;
        float evenWidth = rowWidth / actions.Length;
        float remainingWidth = rowWidth;

        for (int index = 0; index < actions.Length; index++)
        {
            if (index == actions.Length - 1)
            {
                widths[index] = MathF.Max(1f, remainingWidth);
                return;
            }

            float width;
            if (minimumTotal >= rowWidth)
            {
                width = evenWidth;
            }
            else if (desiredTotal <= rowWidth)
            {
                width = desiredWidths[index] + extraWidth;
            }
            else
            {
                float shrinkShare = (desiredWidths[index] - minimumWidths[index]) / shrinkCapacity;
                width = MathF.Max(minimumWidths[index], desiredWidths[index] - (shrinkDeficit * shrinkShare));
            }

            widths[index] = MathF.Max(1f, width);
            remainingWidth -= widths[index];
        }
    }

    private static IconTextButtonMetrics ResolveIconTextButtonMetrics(FontAwesomeIcon icon, string text)
    {
        EditorIcon.Metrics iconMetrics = EditorIcon.Measure(icon);
        Vector2 labelSize = ImGui.CalcTextSize(text);
        float spacing = 6f * ImGuiHelpers.GlobalScale;
        float horizontalPadding = (ImGui.GetStyle().FramePadding.X * 2f) + (18f * ImGuiHelpers.GlobalScale);
        return new IconTextButtonMetrics(
            iconMetrics.Size.X + labelSize.X + spacing + horizontalPadding,
            iconMetrics.Size.X + horizontalPadding,
            iconMetrics,
            spacing);
    }

    private static bool DrawIconTextButton(string id, FontAwesomeIcon icon, string text, float width, bool selected = false)
    {
        Vector2 buttonSize = new(MathF.Max(1f, width), 0f);
        using var selectedButton = selected
            ? ImRaii.PushColor(ImGuiCol.Button, ImGui.GetColorU32(ImGuiCol.ButtonActive))
            : default;

        bool clicked = ImGui.Button(id, buttonSize);

        Vector2 min = ImGui.GetItemRectMin();
        Vector2 max = ImGui.GetItemRectMax();
        ImDrawListPtr drawList = ImGui.GetWindowDrawList();
        IconTextButtonMetrics metrics = ResolveIconTextButtonMetrics(icon, text);
        string label = ResolveIconTextButtonLabel(text, metrics, max.X - min.X);
        Vector2 labelSize = string.IsNullOrEmpty(label) ? Vector2.Zero : ImGui.CalcTextSize(label);
        float totalWidth = metrics.Icon.Size.X + (string.IsNullOrEmpty(label) ? 0f : metrics.Spacing + labelSize.X);
        float startX = min.X + MathF.Max(0f, ((max.X - min.X) - totalWidth) * 0.5f);
        float iconY = min.Y + ((max.Y - min.Y - metrics.Icon.Size.Y) * 0.5f);
        float labelY = min.Y + ((max.Y - min.Y - labelSize.Y) * 0.5f);

        drawList.PushClipRect(min, max, true);
        EditorIcon.Draw(
            drawList,
            icon,
            metrics.Icon,
            new Vector2(startX, iconY),
            ThemeColors.Style(ImGuiCol.Text));

        if (!string.IsNullOrEmpty(label))
        {
            drawList.AddText(
                new Vector2(startX + metrics.Icon.Size.X + metrics.Spacing, labelY),
                ImGui.GetColorU32(ImGuiCol.Text),
                label);
        }

        drawList.PopClipRect();

        return clicked;
    }

    private static string ResolveIconTextButtonLabel(string text, IconTextButtonMetrics metrics, float buttonWidth)
    {
        float reservedWidth = metrics.MinimumWidth + metrics.Spacing;
        float availableTextWidth = buttonWidth - reservedWidth;
        return EditorTextUtility.ClipTextToWidth(text, availableTextWidth);
    }
}
