using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin;
using Intoner.Objects.Api;
using Intoner.Objects.Assets;
using Intoner.Objects.Catalog;
using Intoner.Objects.Collections;
using Intoner.Objects.Filesystem.Configuration;
using Intoner.Objects.Filesystem.Layouts;
using Intoner.Objects.Interop;
using Intoner.Objects.Models;
using Intoner.Objects.Preview;
using Intoner.Objects.Preview.Rendering;
using Intoner.Objects.Rendering.Drawing;
using Intoner.Objects.Runtime;
using Intoner.Objects.UI.Components;
using Intoner.Objects.UI.Docking;
using Intoner.Objects.UI.Services;
using Intoner.Objects.UI.Services.Backdrop;
using Intoner.Objects.UI.Services.EdgeGlow;
using Intoner.Objects.UI.Settings;
using Intoner.Objects.UI.TitleBar;
using Intoner.Services;
using Intoner.UI;
using Intoner.UI.Performance;
using Intoner.UI.Windows;
using Microsoft.Extensions.Logging;
using System.Numerics;
using System.Text.Json;
using ObjectOutlineColorModel = Intoner.Objects.Models.ObjectOutlineColor;

namespace Intoner.Objects.UI;

internal sealed partial class EditorWindow : IntonerWindow, IGizmoHost, IDisposable
{
    private const string FolderEditorPopupId = "##objectFolderEditorPopup";
    private const string ObjectCollectionCreatePopupId = "##objectCollectionCreatePopup";
    private enum FolderEditorMode
    {
        Create,
        Rename,
    }

    private sealed class BgObjectCreateState
    {
        public string ModelPath = string.Empty;
        public string CatalogFilter = string.Empty;
        public string SourceFilter = string.Empty;
        public CatalogLayoutMode CatalogLayout = CatalogLayoutMode.Grid;
        public PreviewState Preview = new();
        public Vector3 Scale = Vector3.One;
        public bool Visible = true;
        public float Transparency;
        public Vector4 DyeColor = Vector4.One;
        public bool IsCoveredFromRain;
    }

    private sealed class FurnitureCreateState
    {
        public string SharedGroupPath = string.Empty;
        public uint HousingRowId;
        public uint ItemRowId;
        public string CatalogFilter = string.Empty;
        public string CategoryFilter = string.Empty;
        public string StainFilter = string.Empty;
        public CatalogLayoutMode CatalogLayout = CatalogLayoutMode.List;
        public PreviewState Preview = new();
        public Vector3 Scale = Vector3.One;
        public bool Visible = true;
        public float Transparency;
        public byte StainId;
        public bool UseCustomColor;
        public Vector4 CustomColor = Vector4.One;
        public ObjectOutlineColorModel OutlineColor;
    }

    private sealed class VfxCreateState
    {
        public string VfxPath = string.Empty;
        public string CatalogFilter = string.Empty;
        public string SourceFilter = string.Empty;
        public Vector3 Scale = Vector3.One;
        public bool Visible = true;
        public Vector4 Color = Vector4.One;
        public bool Loop;
        public int LoopIntervalSeconds = VfxModel.DefaultLoopIntervalSeconds;
    }

    private sealed class PreviewState
    {
        private const float DefaultYaw = -0.85f;
        private const float DefaultPitch = 0.34f;
        private const float DefaultZoom = 1.00f;

        public string AssetPath { get; private set; } = string.Empty;
        public float Yaw { get; private set; } = DefaultYaw;
        public float Pitch { get; private set; } = DefaultPitch;
        public float Zoom { get; private set; } = DefaultZoom;
        public PreviewRender.BackgroundStyle BackgroundStyle { get; set; } = PreviewRender.BackgroundStyle.White;

        public void SyncAsset(string assetPath)
        {
            var normalizedPath = GameAssetPathRules.NormalizeGamePath(assetPath);
            if (string.Equals(AssetPath, normalizedPath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            AssetPath = normalizedPath;
            ResetView();
        }

        public void Orbit(Vector2 delta)
        {
            Yaw += delta.X * 0.014f;
            Pitch = Math.Clamp(Pitch - (delta.Y * 0.014f), -1.10f, 1.10f);
        }

        public void ZoomBy(float wheelDelta)
        {
            var scale = 1f - (wheelDelta * 0.10f);
            if (scale <= 0.05f)
            {
                scale = 0.05f;
            }

            Zoom = Math.Clamp(Zoom * scale, 0.65f, 2.40f);
        }

        public void ResetView()
        {
            Yaw = DefaultYaw;
            Pitch = DefaultPitch;
            Zoom = DefaultZoom;
        }

        public PreviewRender.Request CreateRequest(int width, int height, PreviewRender.Mode mode = PreviewRender.Mode.Detail)
            => new(
                Math.Clamp(width, 96, 640),
                Math.Clamp(height, 96, 480),
                (int)MathF.Round(Yaw * 100f),
                (int)MathF.Round(Pitch * 100f),
                (int)MathF.Round(Zoom * 100f),
                BackgroundStyle,
                mode);
    }

    private sealed class LightCreateState
    {
        public string CatalogFilter = string.Empty;
        public bool Visible = true;
        public LightModel Model = new();
    }

    private readonly record struct WorkspaceModeAction(string Id, FontAwesomeIcon Icon, string Label, WorkspaceMode Mode, bool FocusCurrentHistoryEntry = false);

    private static readonly WorkspaceModeAction[] WorkspaceModeActions =
    [
        new("##objectModeCatalogCreate", FontAwesomeIcon.FolderOpen, "Catalog + Create", WorkspaceMode.CatalogCreate),
        new("##objectModePlacedInspector", FontAwesomeIcon.Edit, "Placed + Edit", WorkspaceMode.PlacedInspector),
        new("##objectModeLayouts", FontAwesomeIcon.Folder, "Layouts", WorkspaceMode.LayoutManager),
        new("##objectModeCollections", FontAwesomeIcon.Swatchbook, "Collections", WorkspaceMode.Collections),
        new("##objectModeHistory", FontAwesomeIcon.History, "History", WorkspaceMode.History, FocusCurrentHistoryEntry: true),
        new("##objectModeSettings", FontAwesomeIcon.Cog, "Settings", WorkspaceMode.Settings),
        new("##objectModeDebug", FontAwesomeIcon.Bug, "Debug", WorkspaceMode.Debug),
    ];

    private readonly IDalamudPluginInterface               _pluginInterface;
    private readonly ILogger<EditorWindow>                 _logger;
    private readonly IObjectManager                        _objectManager;
    private readonly IObjectFolderService                  _objectFolderService;
    private readonly IObjectMutationService                _mutationService;
    private readonly IObjectSceneView                      _sceneView;
    private readonly IObjectSelectionService               _objectSelectionService;
    private readonly IObjectHistoryManager                 _objectHistoryManager;
    private readonly IHistoryCoordinator                   _historyCoordinator;
    private readonly IObjectLayoutManager                  _layoutManager;
    private readonly IObjectLayoutFileService              _objectLayoutFileService;
    private readonly IObjectLayoutRecoveryService          _objectLayoutRecoveryService;
    private readonly IObjectKindService                    _objectKindService;
    private readonly IObjectHousingModePolicy              _housingModePolicy;
    private readonly PlacementValidationService            _placementValidationService;
    private readonly PlacementFixExecutor                  _placementFixExecutor;
    private readonly IObjectCatalogService                 _objectCatalog;
    private readonly IFurnitureStainService                _furnitureStainService;
    private readonly IObjectCollectionManager              _objectCollectionManager;
    private readonly IObjectModDataSource                  _objectModDataSource;
    private readonly IClipboardExportService               _clipboardExportService;
    private readonly IObjectConfigurationService           _objectConfigurationService;
    private readonly SettingsPage                          _settingsPage;
    private readonly PreviewService                        _previewService;
    private readonly ViewportService                       _viewportService;
    private readonly DrawManager                           _drawManager;
    private readonly Gizmo                                 _gizmo;
    private readonly EdgeGlowRenderer                      _edgeGlowRenderer;
    private readonly BackdropRenderer                      _windowBackdropRenderer;
    private readonly UiSharedService                       _uiSharedService;
    private readonly EditorTitleBarIndicatorService        _titleBarIndicatorService;
    private readonly TitleBarIconRenderer                  _titleBarIconRenderer;
    private readonly TitleBarWindowMetrics                 _titleBarWindowMetrics;
    private readonly IDisposable                           _mainWindowRequestSubscription;
    private IReadOnlyList<FurnitureStainOption>            _furnitureStains = [];
    private IReadOnlyDictionary<Guid, PlacementEvaluation> _placementEvaluations =
        new Dictionary<Guid, PlacementEvaluation>();

    private readonly BgObjectCreateState          _bgObjectCreate = new();
    private readonly FurnitureCreateState         _furnitureCreate = new();
    private readonly VfxCreateState               _vfxCreate = new();
    private readonly LightCreateState             _lightCreate = new();
    private readonly SelectionService             _editorSelection = new();
    private readonly Dictionary<string, float>    _catalogFilterScroll = new(StringComparer.Ordinal);
    private readonly Dictionary<string, float>    _catalogFilterScrollMax = new(StringComparer.Ordinal);
    private readonly Dictionary<string, float>    _pendingCatalogFilterScroll = new(StringComparer.Ordinal);
    private readonly HashSet<string>              _collapsedPlacedFolders = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string>              _expandedCollectionModRows = new(StringComparer.OrdinalIgnoreCase);

    private bool _scrollableCreateCardPreviewHovered;

    private static readonly IReadOnlyList<LightCatalogEntry> LightCatalogEntries =
    [
        new(LightType.WorldLight, "World Light", FontAwesomeIcon.Globe, "world", "Light that affects the entire zone globally."),
        new(LightType.AreaLight, "Area Light", FontAwesomeIcon.BorderAll, "area", "Light that emits spherically from a single point."),
        new(LightType.SpotLight, "Spot Light", FontAwesomeIcon.Crosshairs, "spot", "Light that emits in the shape of a cone with a spherical base."),
        new(LightType.FlatLight, "Flat Light", FontAwesomeIcon.GripLines, "flat", "Light that emits in a flat box-like shape."),
    ];

    private DraftKind _draftKind = DraftKind.Furniture;
    private WorkspaceMode _workspaceMode = WorkspaceMode.CatalogCreate;
    private ToolbarDockPosition _toolbarDockPosition = ToolbarDockPosition.Top;
    private UiConfiguration.SplitRatios _workspaceSplits;
    private bool _openToolbarDockPopupNextFrame;
    private bool _workspaceSplitRatioDirty;
    private bool _openFolderEditorPopupNextFrame;
    private bool _openObjectCollectionCreatePopupNextFrame;
    private bool _openObjectCollectionAddModPopupNextFrame;
    private bool _objectSelectionLeftMouseWasDown;
    private IDisposable? _windowBackgroundColorScope;
    private string _objectFilter = string.Empty;
    private ObjectKind? _objectKindFilter;
    private string _createPlacementFolderPath = string.Empty;
    private FolderEditorMode _folderEditorMode;
    private string _folderEditorSourcePath = string.Empty;
    private string _folderEditorInput = string.Empty;
    private string _inspectorFurnitureStainFilter = string.Empty;
    private string _layoutFileDialogDirectory = string.Empty;
    private string _layoutFileStatusMessage = string.Empty;
    private string _splashScreenStatusMessage = string.Empty;
    private Guid? _selectedLayoutId;
    private string _layoutName = string.Empty;
    private bool _layoutFileStatusIsError;
    private bool _splashScreenStatusIsError;
    private bool _splashScreenLayoutPickerOpen;
    private Vector4 _windowBodyBackgroundColor;
    private string _selectedObjectCollectionId = string.Empty;
    private string _objectCollectionNameDraft = string.Empty;
    private string _objectCollectionNameCommitted = string.Empty;
    private string _objectCollectionNameDraftCollectionId = string.Empty;
    private string _editingObjectCollectionNameId = string.Empty;
    private string _objectCollectionCreateInput = string.Empty;
    private string _objectCollectionAddModPopupCollectionId = string.Empty;
    private string _objectCollectionModFilter = string.Empty;
    private bool _focusObjectCollectionNameEdit;
    private Vector2 _objectCollectionAddModPopupAnchorMin;
    private Vector2 _objectCollectionAddModPopupAnchorMax;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        IncludeFields = true,
    };

    public EditorWindow(
        IntonerUiPerformanceService uiPerformance,
        IntonerBuildInfoService buildInfo,
        IDalamudPluginInterface pluginInterface,
        ILogger<EditorWindow> logger,
        IGameInputSuppressionService gameInputSuppressionService,
        IObjectManager objectManager,
        IObjectFolderService objectFolderService,
        IObjectMutationService mutationService,
        IObjectSceneView sceneView,
        IObjectSelectionService objectSelectionService,
        IObjectHistoryManager objectHistoryManager,
        IHistoryCoordinator historyCoordinator,
        IObjectLayoutManager layoutManager,
        IObjectLayoutFileService objectLayoutFileService,
        IObjectLayoutRecoveryService objectLayoutRecoveryService,
        IObjectPlacementResolver placementResolver,
        IObjectSurfaceTargetService surfaceTargetService,
        SurfacePlacementService surfacePlacementService,
        SurfaceAttachmentService surfaceAttachmentService,
        IObjectKindService objectKindService,
        IObjectHousingModePolicy housingModePolicy,
        PlacementValidationService placementValidationService,
        PlacementFixExecutor placementFixExecutor,
        IObjectCatalogService objectCatalog,
        IFurnitureStainService furnitureStainService,
        IObjectCollectionManager objectCollectionManager,
        IObjectModDataSource objectModDataSource,
        IClipboardExportService clipboardExportService,
        IObjectConfigurationService objectConfigurationService,
        IObjectHousingCullingService housingCullingService,
        IIntonerMediator mediator,
        PreviewService previewService,
        ViewportService viewportService,
        IRenderer renderer,
        EdgeGlowRenderer edgeGlowRenderer,
        BackdropRenderer windowBackdropRenderer,
        UiSharedService uiSharedService,
        TitleBarIconRenderer titleBarIconRenderer,
        EditorTitleBarIndicatorService titleBarIndicatorService,
        TitleBarWindowMetrics titleBarWindowMetrics)
        : base(buildInfo.WindowName, uiPerformance)
    {
        _pluginInterface                = pluginInterface;
        _logger                         = logger;
        _objectManager                  = objectManager;
        _objectFolderService            = objectFolderService;
        _mutationService                = mutationService;
        _sceneView                      = sceneView;
        _objectSelectionService         = objectSelectionService;
        _objectHistoryManager           = objectHistoryManager;
        _historyCoordinator             = historyCoordinator;
        _layoutManager                  = layoutManager;
        _objectLayoutFileService        = objectLayoutFileService;
        _objectLayoutRecoveryService    = objectLayoutRecoveryService;
        _objectKindService              = objectKindService;
        _housingModePolicy              = housingModePolicy;
        _placementValidationService     = placementValidationService;
        _placementFixExecutor           = placementFixExecutor;
        _objectCatalog                  = objectCatalog;
        _furnitureStainService          = furnitureStainService;
        _objectCollectionManager        = objectCollectionManager;
        _objectModDataSource            = objectModDataSource;
        _clipboardExportService         = clipboardExportService;
        _objectConfigurationService     = objectConfigurationService;
        _workspaceSplits                = objectConfigurationService.Current.Ui.WorkspaceSplits;
        _settingsPage                   = new SettingsPage(objectConfigurationService, housingCullingService, housingModePolicy, _editorOverlayLayer);
        _previewService                 = previewService;
        _viewportService                = viewportService;
        _drawManager                    = new DrawManager(renderer);
        _gizmo                          = new Gizmo(
            this,
            _drawManager,
            gameInputSuppressionService,
            mutationService,
            placementResolver,
            surfaceTargetService,
            surfacePlacementService,
            surfaceAttachmentService);
        _edgeGlowRenderer               = edgeGlowRenderer;
        _windowBackdropRenderer         = windowBackdropRenderer;
        _uiSharedService                = uiSharedService;
        _splashScreenVersionLabel       = buildInfo.SplashScreenVersion;
        _titleBarIconRenderer           = titleBarIconRenderer;
        _titleBarIndicatorService       = titleBarIndicatorService;
        _titleBarWindowMetrics          = titleBarWindowMetrics;
        _mainWindowRequestSubscription  = mediator.Subscribe<IntonerMainWindowRequest>(this, HandleMainWindowRequest);

        RespectCloseHotkey = true;
        ShowCloseButton = true;
        _historyCoordinator.ConnectSelectionHandlers(CaptureCurrentSelectionIds, ApplyHistorySelection);
        WindowBuilder.For(this)
            .SetSizeConstraints(new Vector2(930f, 695f), new Vector2(1200f, 760f))
            .AddTitleBarButton(FontAwesomeIcon.Book, "Show Splash Screen", ShowSplashScreen)
            .AddTitleBarButton(FontAwesomeIcon.ArrowsAlt, "Toolbar Dock Position", () => _openToolbarDockPopupNextFrame = true)
            .Apply();
    }

    public void Dispose()
    {
        _mainWindowRequestSubscription.Dispose();
        _historyCoordinator.DisconnectSelectionHandlers();
        DisposeObjectIpcTester();
        _gizmo.Dispose();
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
        _workspaceMode = workspaceMode;
        DismissSplashScreen();
    }

    public override void PreDraw()
    {
        base.PreDraw();
        _windowBodyBackgroundColor = EditorColors.WindowBg;
        _windowBackgroundColorScope?.Dispose();
        _windowBackgroundColorScope = ImRaii.PushColor(ImGuiCol.WindowBg, Vector4.Zero);
    }

    public override void PostDraw()
    {
        DrawTitleBar();

        _windowBackgroundColorScope?.Dispose();
        _windowBackgroundColorScope = null;

        base.PostDraw();
    }

    protected override void DrawContent()
    {
        _objectCatalog.EnsureWarmup();
        _furnitureStainService.EnsureWarmup();

        if (!TryResolveEditorWarmupData(out var catalog, out var warmupStatusText, out var warmupHasFailed))
        {
            DrawWindowBackgroundGlass();
            DrawEditorWarmupScreen(warmupStatusText, warmupHasFailed);
            return;
        }

        bool showSplashScreen = ShouldShowSplashScreen();
        RefreshHistoryContext();
        NormalizeDraftKindForHousingMode();
        var activeObjects = _sceneView.GetObjectSnapshots();
        var objects = _sceneView.GetPlacedObjectSnapshots();
        var objectLookup = objects.ToDictionary(static entry => entry.Id);
        var activeObjectLookup = activeObjects.ToDictionary(static entry => entry.Id);
        var activeObjectIds = activeObjects
            .Select(static entry => entry.Id)
            .ToHashSet();
        var kindInfos = _objectKindService.GetKindInfos();
        var boundsSnapshots = _sceneView.GetObjectBoundsSnapshots();
        _placementEvaluations = _placementValidationService.Evaluate(objects, boundsSnapshots);
        var layouts = _layoutManager.GetLayouts();
        var defaultLayoutId = _layoutManager.GetDefaultLayoutId();
        var selectableObjectIds = objects
            .Where(static snapshot => !snapshot.Locked)
            .Select(static snapshot => snapshot.Id)
            .ToHashSet();
        HandleSelectionChanged(_editorSelection.TryPrune(selectableObjectIds));

        ResolveEditorSelection(objectLookup, activeObjectLookup, out var selectedObjects, out var activeSelectedObjects, out var selected, out var selectedActive);

        if (_selectedLayoutId.HasValue && layouts.All(entry => entry.Id != _selectedLayoutId.Value))
        {
            _selectedLayoutId = null;
        }

        if (!showSplashScreen)
        {
            HandleObjectSelectionInput(activeSelectedObjects, boundsSnapshots);
        }

        ResolveEditorSelection(objectLookup, activeObjectLookup, out selectedObjects, out activeSelectedObjects, out selected, out selectedActive);
        var gizmoSelected = activeSelectedObjects.Count > 0
            ? activeSelectedObjects[^1]
            : null;

        _gizmo.NormalizeMode(activeSelectedObjects);
        DrawWindowBackgroundGlass();
        DrawToolbarDockPopup();

        using (ImRaii.PushStyle(ImGuiStyleVar.DisabledAlpha, 1f))
        using (ImRaii.Disabled(showSplashScreen))
        {
            BeginEditorOverlayFrame();
            DrawToolbarLayout(
                objects,
                activeObjects,
                activeObjectIds,
                kindInfos,
                catalog,
                layouts,
                defaultLayoutId,
                gizmoSelected,
                activeSelectedObjects);
        }

        if (showSplashScreen)
        {
            DrawSplashScreen();
            return;
        }

        _drawManager.BeginFrame();
        SubmitBoundsOverlay(boundsSnapshots);
        SubmitHousingPlacementOverlay(boundsSnapshots, _placementEvaluations);
        DrawCurrentWindowLayer(boundsSnapshots, _placementEvaluations);
        _gizmo.Draw(activeSelectedObjects, boundsSnapshots);
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

    private void DrawDockCenterContent(
        IReadOnlyList<ObjectSnapshot> objects,
        IReadOnlyList<ObjectSnapshot> activeObjects,
        IReadOnlySet<Guid> activeObjectIds,
        IReadOnlyList<ObjectKindInfo> kindInfos,
        ObjectCatalogData catalog,
        IReadOnlyList<ObjectLayoutSnapshot> layouts,
        Guid? defaultLayoutId)
    {
        DrawWorkspaceStrip();
        DrawBody(objects, activeObjects, activeObjectIds, kindInfos, catalog, layouts, defaultLayoutId);
    }

    private void DrawWorkspaceStrip()
    {
        DrawWorkspaceModeActions();
        DrawWorkspaceToolbarDivider();
    }

    private void NormalizeDraftKindForHousingMode()
    {
        if (_housingModePolicy.GetState().IsHousingMode && _draftKind != DraftKind.Furniture)
        {
            _draftKind = DraftKind.Furniture;
        }
    }

    private void DrawBody(
        IReadOnlyList<ObjectSnapshot> objects,
        IReadOnlyList<ObjectSnapshot> activeObjects,
        IReadOnlySet<Guid> activeObjectIds,
        IReadOnlyList<ObjectKindInfo> kindInfos,
        ObjectCatalogData catalog,
        IReadOnlyList<ObjectLayoutSnapshot> layouts,
        Guid? defaultLayoutId)
    {
        if (_workspaceMode == WorkspaceMode.History)
        {
            DrawHistoryWorkspace();
            return;
        }

        if (_workspaceMode == WorkspaceMode.Collections)
        {
            DrawCollectionsWorkspace();
            return;
        }

        if (_workspaceMode == WorkspaceMode.Settings)
        {
            DrawSettingsWorkspace();
            return;
        }

        if (_workspaceMode == WorkspaceMode.Debug)
        {
            DrawDebugWorkspace();
            return;
        }

        WorkspaceMode splitWorkspace = _workspaceMode;
        EditorSplitPane.Update splitUpdate = EditorSplitPane.Draw(
            "##objectEditorBody",
            ImGui.GetContentRegionAvail(),
            new EditorSplitPane.Options(
                ResolveWorkspaceSplitRatio(splitWorkspace),
                UiConfiguration.SplitRatios.DefaultRatio,
                Scaled(320f),
                Scaled(360f),
                EditorColors.AccentPurple),
            () => DrawPrimaryWorkspacePane(objects, activeObjectIds, catalog, layouts, defaultLayoutId),
            () => DrawSecondaryWorkspacePane(objects, activeObjects, activeObjectIds, kindInfos, layouts, defaultLayoutId));
        ApplyWorkspaceSplitUpdate(splitWorkspace, splitUpdate);
    }

    private void DrawPrimaryWorkspacePane(
        IReadOnlyList<ObjectSnapshot> objects,
        IReadOnlySet<Guid> activeObjectIds,
        ObjectCatalogData catalog,
        IReadOnlyList<ObjectLayoutSnapshot> layouts,
        Guid? defaultLayoutId)
    {
        switch (_workspaceMode)
        {
            case WorkspaceMode.CatalogCreate:
                DrawChildPanel("##objectCatalogPanel", Vector2.Zero, true, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse, () => DrawCatalogPanel(catalog), transparentBackground: false);
                break;
            case WorkspaceMode.PlacedInspector:
                DrawChildPanel("##objectListPanel", Vector2.Zero, false, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse, () => DrawObjectListPanel(objects, activeObjectIds));
                break;
            case WorkspaceMode.LayoutManager:
                DrawChildPanel("##objectLayoutListPanel", Vector2.Zero, false, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse, () => DrawLayoutListPanel(layouts, defaultLayoutId));
                break;
        }
    }

    private void DrawSecondaryWorkspacePane(
        IReadOnlyList<ObjectSnapshot> objects,
        IReadOnlyList<ObjectSnapshot> activeObjects,
        IReadOnlySet<Guid> activeObjectIds,
        IReadOnlyList<ObjectKindInfo> kindInfos,
        IReadOnlyList<ObjectLayoutSnapshot> layouts,
        Guid? defaultLayoutId)
    {
        switch (_workspaceMode)
        {
            case WorkspaceMode.CatalogCreate:
                DrawChildPanel("##objectCreatePanel", Vector2.Zero, false, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse, () => DrawCreatePanel(kindInfos));
                break;
            case WorkspaceMode.PlacedInspector:
                DrawInspectorPanel(objects, activeObjectIds);
                break;
            case WorkspaceMode.LayoutManager:
                DrawLayoutInspectorPanel(objects, activeObjects, layouts, defaultLayoutId);
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
        _objectConfigurationService.Update(configuration => configuration.Ui.WorkspaceSplits = ratios);
        _workspaceSplitRatioDirty = false;
    }

    private void DrawChildPanel(string id, Vector2 size, bool border, ImGuiWindowFlags flags, Action draw, bool transparentBackground = true)
    {
        using var childBg = transparentBackground
            ? ImRaii.PushColor(ImGuiCol.ChildBg, Vector4.Zero)
            : default;
        var childFlags = transparentBackground
            ? flags | ImGuiWindowFlags.NoBackground
            : flags;
        using var child = ImRaii.Child(id, size, border, childFlags);
        if (child)
        {
            MarkCurrentWindowAsEditorOverlayTarget();
            draw();
        }
    }

    private bool TryGetWindowBodyRect(out Vector2 backgroundMin, out Vector2 backgroundMax, out float rounding, out ImDrawFlags roundingFlags)
    {
        var style = ImGui.GetStyle();
        var windowPos = ImGui.GetWindowPos();
        var windowSize = ImGui.GetWindowSize();
        var contentMin = ImGui.GetWindowContentRegionMin();
        var titleBarHeight = MathF.Max(0f, contentMin.Y - style.WindowPadding.Y);
        backgroundMin = new Vector2(windowPos.X, windowPos.Y + titleBarHeight);
        backgroundMax = windowPos + windowSize;
        var backgroundSize = backgroundMax - backgroundMin;
        if (backgroundSize.X < 4f || backgroundSize.Y < 4f)
        {
            rounding = 0f;
            roundingFlags = ImDrawFlags.RoundCornersNone;
            return false;
        }

        rounding = MathF.Max(0f, style.WindowRounding);
        roundingFlags = titleBarHeight > 0f
            ? ImDrawFlags.RoundCornersBottom
            : ImDrawFlags.RoundCornersAll;
        return true;
    }

    private static GlassEffect.Style CreateWindowBodyGlassStyle(Vector4 backgroundColor)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var windowTint = EditorColors.WithAlpha(backgroundColor, 1f);
        var blurBase = EditorColors.Color(0.45f, 0.45f, 0.45f, 1f);
        var blurTint = Vector4.Lerp(blurBase, windowTint, 0.30f);
        var edgeBase = Vector4.Lerp(blurTint, EditorColors.AccentBlue, 0.08f);
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
        if (!TryGetWindowBodyRect(out var backgroundMin, out var backgroundMax, out var rounding, out var roundingFlags))
        {
            return;
        }

        var drawList = ImGui.GetWindowDrawList();

        drawList.PushClipRect(backgroundMin, backgroundMax, false);
        try
        {
            if (IsWindowBackgroundBlurActive())
            {
                _windowBackdropRenderer.GetEffect<GlassEffect>().DrawRegion(
                    drawList,
                    backgroundMin,
                    backgroundMax,
                    rounding,
                    CreateWindowBodyGlassStyle(_windowBodyBackgroundColor),
                    roundingFlags);
            }
            else
            {
                drawList.AddRectFilled(
                    backgroundMin,
                    backgroundMax,
                    ImGui.GetColorU32(_windowBodyBackgroundColor),
                    rounding,
                    roundingFlags);
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

    private bool DrawDuplicateSelectedButton()
    {
        var selectedExists = _editorSelection.HasSelection;

        using (ImRaii.Disabled(!selectedExists))
        {
            if (DrawIconButton("objectDuplicate", FontAwesomeIcon.Copy, "Duplicate Selected"))
            {
                var selectedSnapshots = ResolveSelectedCurrentObjects();
                if (selectedSnapshots.Count > 0 && TryDuplicateSelectedObjects(selectedSnapshots))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private bool DrawExportSelectedButton(ObjectSnapshot? selected)
    {
        using (ImRaii.Disabled(selected is null))
        {
            if (DrawIconButton("objectExportClipboard", FontAwesomeIcon.Clipboard, "Export Selected To Clipboard") && selected is not null)
            {
                _clipboardExportService.CopySnapshot(selected);
                return true;
            }
        }

        return false;
    }

    private bool DrawImportClipboardButton()
    {
        if (!DrawAccentIconButton("objectImportClipboard", FontAwesomeIcon.FileImport, "Import Object From Clipboard", EditorColors.AccentBlue))
        {
            return false;
        }

        if (!_clipboardExportService.TryPasteSnapshot(out ObjectSnapshot importedSnapshot))
        {
            return false;
        }

        return TryImportObjectSnapshotWithHistory(importedSnapshot);
    }

    private bool DrawMoveSelectedToPlayerButton(bool selectedActive)
    {
        var selectedId = _editorSelection.PrimaryObjectId;
        var selectedExists = selectedId.HasValue;

        using (ImRaii.Disabled(!selectedExists || !selectedActive))
        {
            if (DrawIconButton("objectMoveToPlayer", FontAwesomeIcon.Running, "Move To Player"))
            {
                if (selectedId.HasValue && selectedActive)
                {
                    return TryMoveObjectToPlayerWithHistory(selectedId.Value);
                }
            }
        }

        return false;
    }

    private bool DrawToggleLockSelectedButton()
    {
        var selectedExists = _editorSelection.HasSelection;

        using (ImRaii.Disabled(!selectedExists))
        {
            var selectedSnapshots = selectedExists
                ? ResolveSelectedCurrentObjects()
                : [];
            var allLocked = selectedSnapshots.Count > 0 && selectedSnapshots.All(static snapshot => snapshot.Locked);
            var icon = allLocked ? FontAwesomeIcon.Unlock : FontAwesomeIcon.Lock;
            var tooltip = allLocked ? "Unlock Selected" : "Lock Selected";

            if (DrawIconButton("objectToggleSelectedLock", icon, tooltip))
            {
                var targetSnapshots = selectedSnapshots
                    .Where(snapshot => snapshot.Locked != !allLocked)
                    .ToList();
                if (targetSnapshots.Count > 0)
                {
                    return TryApplySelectedSnapshotUpdateWithHistory(
                        ObjectHistoryKind.Organization,
                        allLocked ? "Unlock Objects" : "Lock Objects",
                        targetSnapshots,
                        snapshot => snapshot with { Locked = !allLocked });
                }
            }
        }

        return false;
    }

    private bool DrawRemoveSelectedButton()
    {
        var selectedExists = _editorSelection.HasSelection;

        using (ImRaii.Disabled(!selectedExists))
        {
            if (DrawIconButton("objectRemoveSelected", FontAwesomeIcon.Trash, "Remove Selected"))
            {
                var selectedSnapshots = ResolveSelectedCurrentObjects();
                if (selectedSnapshots.Count > 0 && TryRemoveSelectedObjects(selectedSnapshots))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private bool DrawClearAllButton(bool hasPersistedObjects)
    {
        using (ImRaii.Disabled(!hasPersistedObjects))
        {
            if (DrawAccentIconButton("objectClearAll", FontAwesomeIcon.Ban, "Clear All Placed Objects", EditorColors.DimRed))
            {
                return TryClearPlacedObjectsWithHistory();
            }
        }

        return false;
    }

    private void DrawWorkspaceModeActions()
    {
        Span<float> widths = stackalloc float[WorkspaceModeActions.Length];
        ResolveWorkspaceModeActionWidths(WorkspaceModeActions, widths, ImGui.GetContentRegionAvail().X);

        for (var actionIndex = 0; actionIndex < WorkspaceModeActions.Length; actionIndex++)
        {
            var action = WorkspaceModeActions[actionIndex];
            if (actionIndex > 0)
            {
                ImGui.SameLine();
            }

            if (!DrawIconTextButton(action.Id, action.Icon, action.Label, widths[actionIndex], _workspaceMode == action.Mode))
            {
                continue;
            }

            _workspaceMode = action.Mode;
            if (action.FocusCurrentHistoryEntry)
            {
                _focusCurrentHistoryEntry = true;
            }
        }
    }

    private void DrawWorkspaceToolbarDivider()
    {
        var drawList = ImGui.GetWindowDrawList();
        var start = ImGui.GetCursorScreenPos();
        var width = MathF.Max(0f, ImGui.GetContentRegionAvail().X);
        var thickness = ResolveDividerThickness();
        var color = ImGui.GetColorU32(ImGuiCol.Separator);
        var lineCenterY = start.Y + (thickness * 0.5f);

        drawList.AddLine(
            new Vector2(start.X, lineCenterY),
            new Vector2(start.X + width, lineCenterY),
            color,
            thickness);
        ImGui.Dummy(new Vector2(0f, ResolveWorkspaceToolbarDividerHeight()));
    }

    private static float ResolveDividerThickness()
        => MathF.Max(1f, ImGui.GetStyle().FrameBorderSize + 1f);

    private static float ResolveWorkspaceToolbarDividerHeight()
        => ResolveDividerThickness() + ImGui.GetStyle().ItemSpacing.Y;

    private readonly record struct IconTextButtonMetrics(float Width, float MinimumWidth, Vector2 IconSize, float Spacing);

    private static void ResolveWorkspaceModeActionWidths(ReadOnlySpan<WorkspaceModeAction> actions, Span<float> widths, float availableWidth)
    {
        if (actions.IsEmpty)
        {
            return;
        }

        var spacingWidth = ImGui.GetStyle().ItemSpacing.X * (actions.Length - 1);
        var rowWidth = MathF.Max(actions.Length, availableWidth - spacingWidth);
        Span<float> desiredWidths = stackalloc float[actions.Length];
        Span<float> minimumWidths = stackalloc float[actions.Length];
        var desiredTotal = 0f;
        var minimumTotal = 0f;

        for (var index = 0; index < actions.Length; index++)
        {
            var action = actions[index];
            var metrics = ResolveIconTextButtonMetrics(action.Icon, action.Label);
            desiredWidths[index] = metrics.Width;
            minimumWidths[index] = metrics.MinimumWidth;
            desiredTotal += metrics.Width;
            minimumTotal += metrics.MinimumWidth;
        }

        var extraWidth = desiredTotal <= rowWidth ? (rowWidth - desiredTotal) / actions.Length : 0f;
        var shrinkCapacity = desiredTotal - minimumTotal;
        var shrinkDeficit = desiredTotal - rowWidth;
        var evenWidth = rowWidth / actions.Length;
        var remainingWidth = rowWidth;

        for (var index = 0; index < actions.Length; index++)
        {
            if (index == actions.Length - 1)
            {
                widths[index] = MathF.Max(1f, remainingWidth);
                return;
            }

            var width = minimumTotal >= rowWidth
                ? evenWidth
                : desiredTotal <= rowWidth
                    ? desiredWidths[index] + extraWidth
                    : MathF.Max(minimumWidths[index], desiredWidths[index] - (shrinkDeficit * ((desiredWidths[index] - minimumWidths[index]) / shrinkCapacity)));
            widths[index] = MathF.Max(1f, width);
            remainingWidth -= widths[index];
        }
    }

    private static IconTextButtonMetrics ResolveIconTextButtonMetrics(FontAwesomeIcon icon, string text)
    {
        var iconText = icon.ToIconString();
        Vector2 iconSize;
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            iconSize = ImGui.CalcTextSize(iconText);
        }

        var labelSize = ImGui.CalcTextSize(text);
        var spacing = 6f * ImGuiHelpers.GlobalScale;
        var horizontalPadding = (ImGui.GetStyle().FramePadding.X * 2f) + (18f * ImGuiHelpers.GlobalScale);
        return new IconTextButtonMetrics(
            iconSize.X + labelSize.X + spacing + horizontalPadding,
            iconSize.X + horizontalPadding,
            iconSize,
            spacing);
    }

    private static bool DrawIconTextButton(string id, FontAwesomeIcon icon, string text, float width, bool selected = false)
    {
        var buttonSize = new Vector2(MathF.Max(1f, width), 0f);
        using var selectedButton = selected
            ? ImRaii.PushColor(ImGuiCol.Button, ImGui.GetColorU32(ImGuiCol.ButtonActive))
            : default;

        var clicked = ImGui.Button(id, buttonSize);

        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var drawList = ImGui.GetWindowDrawList();
        var iconText = icon.ToIconString();
        var metrics = ResolveIconTextButtonMetrics(icon, text);
        var label = ResolveIconTextButtonLabel(text, metrics, max.X - min.X);
        var labelSize = string.IsNullOrEmpty(label) ? Vector2.Zero : ImGui.CalcTextSize(label);
        var totalWidth = metrics.IconSize.X + (string.IsNullOrEmpty(label) ? 0f : metrics.Spacing + labelSize.X);
        var startX = min.X + MathF.Max(0f, ((max.X - min.X) - totalWidth) * 0.5f);
        var iconY = min.Y + ((max.Y - min.Y - metrics.IconSize.Y) * 0.5f);
        var labelY = min.Y + ((max.Y - min.Y - labelSize.Y) * 0.5f);

        drawList.PushClipRect(min, max, true);

        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            drawList.AddText(
                new Vector2(startX, iconY),
                ImGui.GetColorU32(ImGuiCol.Text),
                iconText);
        }

        if (!string.IsNullOrEmpty(label))
        {
            drawList.AddText(
                new Vector2(startX + metrics.IconSize.X + metrics.Spacing, labelY),
                ImGui.GetColorU32(ImGuiCol.Text),
                label);
        }

        drawList.PopClipRect();

        return clicked;
    }

    private static string ResolveIconTextButtonLabel(string text, IconTextButtonMetrics metrics, float buttonWidth)
    {
        var reservedWidth = metrics.MinimumWidth + metrics.Spacing;
        var availableTextWidth = buttonWidth - reservedWidth;
        return ClipTextToWidth(text, availableTextWidth);
    }

    private static string ClipTextToWidth(string text, float width)
        => EditorTextUtility.ClipTextToWidth(text, width);

    private readonly record struct SquareIconButtonMetrics(float Edge, Vector2 IconSize);

    private static SquareIconButtonMetrics ResolveSquareIconButtonMetrics(string iconText)
    {
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            var iconSize = ImGui.CalcTextSize(iconText);
            var edge = MathF.Max(
                28f * ImGuiHelpers.GlobalScale,
                MathF.Ceiling(MathF.Max(iconSize.X, iconSize.Y) + (12f * ImGuiHelpers.GlobalScale)));
            return new SquareIconButtonMetrics(edge, iconSize);
        }
    }

    private static Vector2 ResolveCenteredSquareIconPosition(string iconText, Vector2 min, Vector2 size)
    {
        if (TryResolveCenteredGlyphPosition(iconText, min, size, out var glyphPosition))
        {
            return glyphPosition;
        }

        var metrics = ResolveSquareIconButtonMetrics(iconText);
        return new Vector2(
            MathF.Round(min.X + ((size.X - metrics.IconSize.X) * 0.5f)),
            MathF.Round(min.Y + ((size.Y - metrics.IconSize.Y) * 0.5f)));
    }

    private static unsafe bool TryResolveCenteredGlyphPosition(string iconText, Vector2 min, Vector2 size, out Vector2 position)
    {
        position = Vector2.Zero;
        if (string.IsNullOrEmpty(iconText))
        {
            return false;
        }

        ImFontGlyphPtr glyph = ImGui.GetFont().FindGlyphNoFallback((ushort)iconText[0]);
        if (glyph.IsNull)
        {
            return false;
        }

        var glyphWidth = glyph.X1 - glyph.X0;
        var glyphHeight = glyph.Y1 - glyph.Y0;
        position = new Vector2(
            MathF.Round(min.X + ((size.X - glyphWidth) * 0.5f) - glyph.X0),
            MathF.Round(min.Y + ((size.Y - glyphHeight) * 0.5f) - glyph.Y0));
        return true;
    }

    private static float ResolveMinimumCardTextWidth()
        => Scaled(40f);

    private static float Scaled(float value)
        => value * ImGuiHelpers.GlobalScale;

    private static Vector2 ScaledVector(float x, float y)
        => new(Scaled(x), Scaled(y));

    private static float Positive(float value)
        => MathF.Max(1f, value);

    private static float ResolveActionStripWidth(float actionEdge, int actionCount)
        => (actionEdge * actionCount) + (ImGui.GetStyle().ItemSpacing.X * MathF.Max(0, actionCount - 1));

    private static float ResolveRemainingRegionHeight(float minHeight = 1f, float bottomInset = 0f)
        => MathF.Max(minHeight, ImGui.GetContentRegionAvail().Y - bottomInset);

    private static float ResolveScrollableCardInnerHeight(float cardHeight, Vector2 padding, float contentStartY)
        => Positive(cardHeight - (padding.Y * 2f) - (ImGui.GetCursorPosY() - contentStartY) - ImGui.GetStyle().ItemSpacing.Y);

    private static Vector2 ResolveObjectListCardPadding()
        => ScaledVector(10f, 8f);

    private static float ResolveObjectListItemSpacingY()
        => Scaled(2f);

    private static float ResolveObjectListCardInnerHeight(Vector2 padding, float itemSpacingY)
        => Positive(ResolveRemainingRegionHeight(bottomInset: ImGui.GetStyle().ItemSpacing.Y) - (padding.Y * 2f) - (itemSpacingY * 2f));

    private static float ResolveObjectListEntryHeight()
        => MathF.Max(Scaled(52f), (ImGui.GetTextLineHeight() * 2f) + Scaled(20f));

    private static bool DrawIconButton(string id, FontAwesomeIcon icon, string tooltip, bool selected = false, float? edgeOverride = null)
    {
        var iconText = icon.ToIconString();
        var metrics = ResolveSquareIconButtonMetrics(iconText);
        var edge = edgeOverride ?? metrics.Edge;
        using var selectedButton = selected
            ? ImRaii.PushColor(ImGuiCol.Button, ImGui.GetColorU32(ImGuiCol.ButtonActive))
            : default;

        bool clicked;
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            clicked = ImGui.Button($"{iconText}##{id}", new Vector2(edge, edge));
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(tooltip);
        }

        return clicked;
    }

    private static bool DrawAccentIconButton(string id, FontAwesomeIcon icon, string tooltip, Vector4 accentColor, float? edgeOverride = null)
    {
        var iconText = icon.ToIconString();
        var metrics = ResolveSquareIconButtonMetrics(iconText);
        var edge = edgeOverride ?? metrics.Edge;
        var size = new Vector2(edge, edge);
        var fill = EditorColors.ButtonDefault with { W = 0.88f };
        var hoverFill = accentColor with { W = 0.16f };
        var activeFill = accentColor with { W = 0.10f };
        var rounding = 6f * ImGuiHelpers.GlobalScale;

        using var button = ImRaii.PushColor(ImGuiCol.Button, fill);
        using var hovered = ImRaii.PushColor(ImGuiCol.ButtonHovered, hoverFill);
        using var active = ImRaii.PushColor(ImGuiCol.ButtonActive, activeFill);
        using var border = ImRaii.PushColor(ImGuiCol.Border, Vector4.Zero);
        using var text = ImRaii.PushColor(ImGuiCol.Text, accentColor);
        using var borderSize = ImRaii.PushStyle(ImGuiStyleVar.FrameBorderSize, 0f);
        using var frameRounding = ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, rounding);
        using var textAlign = ImRaii.PushStyle(ImGuiStyleVar.ButtonTextAlign, new Vector2(0.5f, 0.5f));

        bool clicked;
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            clicked = ImGui.Button($"{iconText}##{id}", size);
        }

        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var drawList = ImGui.GetWindowDrawList();

        drawList.AddRect(min, max, ImGui.GetColorU32(accentColor), rounding, ImDrawFlags.None, 1f * ImGuiHelpers.GlobalScale);

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(tooltip);
        }

        return clicked;
    }

    private static void DrawPanelCard(string id, Vector4 background, Vector4 border, float rounding, Vector2 padding, Action content)
        => Components.EditorCard.DrawPanelCard(id, background, border, rounding, padding, content);

    private static void DrawPanelCard(string id, Vector4 background, Vector4 border, float rounding, Vector2 padding, float? minHeight, Action content)
        => Components.EditorCard.DrawPanelCard(id, background, border, rounding, padding, minHeight, content);

    private void CreateObject(ObjectKind kind, ObjectPlacementOverrides? overrides)
        => _ = TryCreateObjectWithHistory(ResolveCreateObjectHistoryTitle(kind), kind, overrides);

    private static string ResolveCreateObjectHistoryTitle(ObjectKind kind)
        => kind switch
        {
            ObjectKind.BgObject  => "Create BgObject",
            ObjectKind.Furniture => "Create Furniture",
            ObjectKind.Vfx       => "Create VFX",
            ObjectKind.Light     => "Create Light",
            _                    => "Create Object",
        };

    private void ResolveEditorSelection(
        IReadOnlyDictionary<Guid, ObjectSnapshot> objectLookup,
        IReadOnlyDictionary<Guid, ObjectSnapshot> activeObjectLookup,
        out IReadOnlyList<ObjectSnapshot> selectedObjects,
        out IReadOnlyList<ObjectSnapshot> activeSelectedObjects,
        out ObjectSnapshot? selectedObject,
        out bool selectedObjectActive)
    {
        selectedObjects = _editorSelection.ResolveSelectedObjects(objectLookup);
        activeSelectedObjects = _editorSelection.ResolveSelectedObjects(activeObjectLookup);

        selectedObject = _editorSelection.ResolvePrimarySelectedObject(activeObjectLookup)
            ?? _editorSelection.ResolvePrimarySelectedObject(objectLookup);
        selectedObjectActive = selectedObject is not null && activeObjectLookup.ContainsKey(selectedObject.Id);
    }

    private IReadOnlyList<ObjectSnapshot> ResolveSelectedCurrentObjects()
    {
        if (!_editorSelection.HasSelection)
        {
            return [];
        }

        var activeLookup = _sceneView.GetObjectSnapshots().ToDictionary(static entry => entry.Id);
        var placedLookup = _sceneView.GetPlacedObjectSnapshots().ToDictionary(static entry => entry.Id);
        var selectedSnapshots = new List<ObjectSnapshot>(_editorSelection.Count);
        foreach (var objectId in _editorSelection.SelectedObjectIds)
        {
            if (activeLookup.TryGetValue(objectId, out var activeSnapshot))
            {
                selectedSnapshots.Add(activeSnapshot);
                continue;
            }

            if (placedLookup.TryGetValue(objectId, out var placedSnapshot))
            {
                selectedSnapshots.Add(placedSnapshot);
            }
        }

        return selectedSnapshots;
    }

}

