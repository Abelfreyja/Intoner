using Dalamud.Interface;
using Dalamud.Interface.ImGuiFileDialog;
using Intoner.Displays;
using Intoner.Logging;
using Intoner.Objects.Api;
using Intoner.Objects.Assets;
using Intoner.Objects.Assets.Cache;
using Intoner.Objects.Catalog;
using Intoner.Objects.Collections;
using Intoner.Objects.Filesystem.Layouts;
using Intoner.Objects.Filesystem.Watching;
using Intoner.Objects.Interop;
using Intoner.Objects.Interop.Ipc;
using Intoner.Objects.Library;
using Intoner.Objects.Preview;
using Intoner.Objects.Preview.Assets;
using Intoner.Objects.Preview.Rendering;
using Intoner.Objects.Rendering.Drawing;
using Intoner.Objects.Rendering.Primitives;
using Intoner.Objects.Resources;
using Intoner.Objects.Runtime;
using Intoner.Objects.UI;
using Intoner.Objects.UI.Components;
using Intoner.Objects.UI.Dependencies;
using Intoner.Objects.UI.Services;
using Intoner.Objects.UI.Services.Backdrop;
using Intoner.Objects.UI.Services.EdgeGlow;
using Intoner.Objects.UI.Settings;
using Intoner.Objects.UI.TitleBar;
using Intoner.Scene;
using Intoner.Scene.Rendering;
using Intoner.Services.Configuration;
using Intoner.Services.Dependencies;
using Intoner.Services.Gpu;
using Intoner.Services.Input;
using Intoner.Services.Storage;
using Intoner.UI;
using Intoner.UI.Performance;
using Intoner.UI.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Intoner.Services;

internal static class IntonerServiceCollectionExtensions
{
    public static IServiceCollection AddIntonerServices(
        this IServiceCollection services,
        IntonerDalamudServices dalamudServices)
        => services
            .AddHostServices(dalamudServices)
            .AddStorageServices()
            .AddSettingsServices()
            .AddDependencyServices()
            .AddObjectAssetServices()
            .AddObjectCollectionServices()
            .AddObjectResourceServices()
            .AddObjectDomainServices()
            .AddObjectRuntimeServices()
            .AddSceneServices()
            .AddObjectPlacementServices()
            .AddRenderingServices()
            .AddDisplayServices()
            .AddObjectApiServices()
            .AddShortcutServices()
            .AddEditorServices();

    internal static IServiceCollection AddSettingsServices(this IServiceCollection services)
    {
        services.AddScoped<CoreSettingFactory>();
        services.AddScoped<ISettingsProvider, CoreSettings>();
        services.AddScoped<SettingsCatalog>();
        return services;
    }

    internal static IServiceCollection AddDependencyServices(this IServiceCollection services)
    {
        services.AddDependency<IPenumbraDependency, PenumbraDependency>();
        services.AddSingleton<IDependencyService, DependencyService>();
        services.AddScoped<ISettingsProvider, DependencySettings>();
        return services;
    }

    internal static IServiceCollection AddDependency<TDependency, TImplementation>(
        this IServiceCollection services)
        where TDependency : class, IDependency
        where TImplementation : class, TDependency
    {
        services.AddSingleton<TDependency, TImplementation>();
        services.AddSingleton(provider => new DependencyRegistration(
            provider.GetRequiredService<TDependency>()));
        return services;
    }

    private static IServiceCollection AddHostServices(
        this IServiceCollection services,
        IntonerDalamudServices dalamudServices)
    {
        services.AddSingleton<IIntonerLogLevelService, IntonerLogLevelService>();

        services.AddLogging(builder =>
        {
            builder.AddIntonerLogging(dalamudServices.PluginInterface, dalamudServices.Log);
        });

        services.AddSingleton(dalamudServices.PluginInterface);
        services.AddSingleton(dalamudServices.CommandManager);
        services.AddSingleton(dalamudServices.ChatGui);
        services.AddSingleton(dalamudServices.ClientState);
        services.AddSingleton(dalamudServices.Condition);
        services.AddSingleton(dalamudServices.DataManager);
        services.AddSingleton(dalamudServices.Framework);
        services.AddSingleton(dalamudServices.GameInteropProvider);
        services.AddSingleton(dalamudServices.ObjectTable);
        services.AddSingleton(dalamudServices.PlayerState);
        services.AddSingleton(dalamudServices.SigScanner);
        services.AddSingleton(dalamudServices.TextureProvider);
        services.AddSingleton<IUiBuilder>(dalamudServices.PluginInterface.UiBuilder);
        services.AddSingleton<IHostEnvironment>(_ => IntonerHostEnvironment.FromPluginInterface(dalamudServices.PluginInterface));

        services.AddSingleton<IntonerBuildInfoService>();
        services.AddSingleton<IntonerPluginRuntime>();
        services.AddScoped<IntonerSession>();
        services.AddScoped<IIntonerMediator, IntonerMediator>();

        return services;
    }

    private static IServiceCollection AddStorageServices(this IServiceCollection services)
    {
        services.AddSingleton<IPluginStoragePaths, PluginStoragePaths>();
        services.AddSingleton<IPluginFileSystem, PluginFileSystem>();
        services.AddSingleton<IIntonerConfigurationService, IntonerConfigurationService>();
        services.AddScoped<ISceneStore, SceneStore>();

        services.AddSingleton<IObjectFileWatcherService, ObjectFileWatcherService>();
        services.AddScoped<IObjectLayoutStore, ObjectLayoutStore>();
        services.AddScoped<IObjectLayoutFileService, ObjectLayoutFileService>();
        services.AddScoped<ObjectLayoutTransferCodec>();
        services.AddScoped<ObjectLayoutImportService>();
        services.AddScoped<IObjectLayoutAutoSaveService, ObjectLayoutAutoSaveService>();
        services.AddScoped<IObjectLayoutRecoveryService, ObjectLayoutRecoveryService>();
        return services;
    }

    private static IServiceCollection AddObjectAssetServices(this IServiceCollection services)
    {
        services.AddSingleton<IObjectAssetGameData, DalamudObjectAssetGameData>();
        services.AddSingleton<IObjectAssetGameVersionService, ObjectAssetGameVersionService>();
        services.AddSingleton<ISqpackIndexFingerprintService, SqpackIndexFingerprintService>();
        services.AddSingleton<SqpackIndexStore>();
        services.AddSingleton<GameDataLayoutAssetResolver>();
        services.AddSingleton<GameDataVfxResolver>();
        services.AddSingleton<RootExlResolver>();
        services.AddSingleton<RootExlVfxFamilyResolver>();
        services.AddSingleton<NativeVfxFamilyResolver>();
        services.AddSingleton<ObjectAssetStaticDiscovery>();
        services.AddSingleton<ObjectAssetStandaloneVfxCatalog>();
        services.AddSingleton<ObjectAssetSharedGroupCache>();
        services.AddSingleton<ObjectAssetStateIngestor>();
        services.AddSingleton<ObjectAssetDependencyResolver>();
        services.AddSingleton<IObjectAssetCacheInvalidationService, ObjectAssetCacheInvalidationService>();
        services.AddSingleton<ObjectAssetCachePayloadReader>();
        services.AddSingleton<IObjectAssetCacheService, ObjectAssetCacheService>();
        services.AddSingleton<IObjectAssetIndex, ObjectAssetIndex>();
        services.AddSingleton<ObjectCatalogBuilder>();
        services.AddSingleton<IObjectCatalogService, ObjectCatalogService>();
        services.AddSingleton<PreviewAssetService>();
        services.AddScoped<FurnitureCatalogResolver>();
        services.AddScoped<IFurnitureStainService, FurnitureStainService>();
        services.AddScoped<EditorLoadCoordinator>();
        services.AddSingleton<ObjectPathResolver>();
        return services;
    }

    private static IServiceCollection AddObjectCollectionServices(this IServiceCollection services)
    {
        services.AddSingleton<IObjectModDataSource, PenumbraObjectModDataSource>();
        services.AddSingleton<IObjectCollectionResolver, ObjectCollectionResolver>();
        services.AddScoped<IObjectCollectionManager, ObjectCollectionManager>();
        services.AddSingleton<IObjectResolvedCollectionStore, ObjectResolvedCollectionStore>();
        services.AddScoped<ITemporaryCollectionService, TemporaryCollectionService>();
        return services;
    }

    private static IServiceCollection AddObjectResourceServices(this IServiceCollection services)
    {
        services.AddSingleton<ObjectTextureLodService>();
        services.AddSingleton<ObjectResourceReadBindings>();
        services.AddSingleton<ObjectResourceLoadScope>();
        services.AddSingleton<ObjectMemoryResourceRegistry>();
        services.AddSingleton<IObjectMemoryResourceService, ObjectMemoryResourceService>();
        services.AddSingleton<IVfxResourceRewriteService, VfxResourceRewriteService>();
        services.AddSingleton<ObjectFileReadService>();
        services.AddSingleton<ObjectResourceTracker>();
        services.AddSingleton<IObjectResourceLoader, ObjectResourceLoader>();
        return services;
    }

    private static IServiceCollection AddObjectDomainServices(this IServiceCollection services)
    {
        services.AddScoped<ObjectStateLock>();
        services.AddScoped<IObjectKindService, ObjectKindService>();
        services.AddScoped<IObjectRevisionTracker, ObjectRevisionTracker>();
        services.AddScoped<IObjectHousingModePolicy, ObjectHousingModePolicy>();
        services.AddScoped<IObjectLibraryStore, ObjectLibraryStore>();
        services.AddScoped<IObjectLibrary, ObjectLibrary>();
        services.AddScoped<ITemporarySourceStore, TemporarySourceStore>();
        services.AddScoped<IObjectLayoutManager, ObjectLayoutManager>();
        services.AddScoped<IObjectFolderService, ObjectFolderService>();
        services.AddScoped<IObjectPersistenceState, ObjectPersistenceState>();
        services.AddScoped<ObjectIdentitySource>();
        services.AddScoped<ISceneIdentitySource>(provider => provider.GetRequiredService<ObjectIdentitySource>());
        services.AddScoped<IObjectIdentityService, ObjectIdentityService>();
        services.AddScoped<IObjectSceneState, ObjectSceneState>();
        services.AddScoped<IObjectSceneSnapshotResolver, ObjectSceneSnapshotResolver>();
        services.AddScoped<ObjectMutationService>();
        services.AddScoped<IObjectMutationService>(provider => provider.GetRequiredService<ObjectMutationService>());
        services.AddScoped<IObjectSceneMutationService>(provider => provider.GetRequiredService<ObjectMutationService>());
        services.AddScoped<IObjectScene, ObjectScene>();
        services.AddScoped<ObjectFrameworkDiagnostics>();
        services.AddScoped<IObjectSceneView, ObjectSceneView>();
        services.AddScoped<ISceneItemDomain, ObjectSceneDomain>();
        services.AddScoped<IObjectOrganizationService, ObjectOrganizationService>();
        services.AddScoped<ITemporarySourceService, TemporarySourceService>();
        services.AddScoped<IObjectManager, ObjectManager>();
        services.AddScoped<ObjectSceneHost>();
        return services;
    }

    private static IServiceCollection AddObjectRuntimeServices(this IServiceCollection services)
    {
        services.AddSingleton<ObjectNativeBindings>();
        services.AddSingleton<FurnitureEmoteGuard>();
        services.AddSingleton<ObjectCollisionOwnership>();
        services.AddSingleton<CameraCollisionBindings>();
        services.AddSingleton<CameraCollisionService>();
        services.AddScoped<ObjectHousingRuntimeContextResolver>();
        services.AddScoped<IObjectRuntimeLocationService, ObjectRuntimeLocationService>();
        services.AddScoped<IObjectRuntimeFactory, ObjectRuntimeFactory>();
        return services;
    }

    private static IServiceCollection AddSceneServices(this IServiceCollection services)
    {
        services.AddSingleton<ISceneLocationService, SceneLocationService>();
        services.AddScoped<ISceneIdentityRegistry, SceneIdentityRegistry>();
        services.AddScoped<ISceneSelectionGeometryProvider, ObjectModelSelectionGeometryCache>();
        services.AddScoped<ISceneItemService, SceneItemService>();
        services.AddScoped<ISceneSurfaceService, SceneSurfaceService>();
        services.AddScoped<ISceneHistoryManager, SceneHistoryManager>();
        services.AddScoped<IObjectWorkspaceHistoryReplay, ObjectWorkspaceHistoryReplay>();
        services.AddScoped<IObjectWorkspaceHistory, ObjectWorkspaceHistory>();
        services.AddScoped<ISceneSelectionService, SceneSelectionService>();
        services.AddScoped<ISceneInputService, SceneInputService>();
        services.AddScoped<ISceneInputRegistry, SceneInputRegistry>();
        return services;
    }

    private static IServiceCollection AddObjectPlacementServices(this IServiceCollection services)
    {
        services.AddScoped<FurnitureMetadataResolver>();
        services.AddScoped<NativePlacementCollisionQuery>();
        services.AddScoped<NativePlacementAreaQuery>();
        services.AddScoped<NativePlacementQuery>();
        services.AddScoped<PlacementSurfaceRaycaster>();
        services.AddScoped<SurfacePlacementService>();
        services.AddScoped<PlacementSurfaceResolver>();
        services.AddScoped<SurfaceAttachmentService>();
        services.AddScoped<PlacementValidationContextBuilder>();
        services.AddScoped<PlacementFixService>();
        services.AddScoped<PlacementEvaluationFactory>();
        services.AddScoped<IPlacementRule, HousingPolicyRule>();
        services.AddScoped<IPlacementRule, SurfaceRule>();
        services.AddScoped<IPlacementRule, AttachedSurfaceRule>();
        services.AddScoped<IPlacementRule, AreaContainmentRule>();
        services.AddScoped<IPlacementRule, FootprintRule>();
        services.AddScoped<PlacementRuleRunner>();
        services.AddScoped<AttachmentHierarchyRule>();
        services.AddScoped<PlacementValidationService>();
        services.AddScoped<PlacementFixExecutor>();
        services.AddScoped<IObjectPlacementResolver, ObjectPlacementResolver>();
        services.AddScoped<IScenePlacementService, ScenePlacementService>();
        return services;
    }

    private static IServiceCollection AddRenderingServices(this IServiceCollection services)
    {
        services.AddSingleton<GpuProcessingService>();
        services.AddSingleton<GpuFrameTransferService>();
        services.AddSingleton<SceneRenderService>();
        services.AddSingleton<PrimitiveCallbackRenderer>();
        services.AddSingleton<PrimitiveService>();
        services.AddSingleton<WorldSurfaceRenderer>();
        services.AddSingleton<IWorldSurfaceService, WorldSurfaceService>();

        services.AddScoped<IRenderer, NativeRenderer>();
        services.AddScoped<NativeTextTextureCache>();
        services.AddScoped<PreviewService>();
        services.AddScoped<ViewportRenderer>();
        services.AddScoped<ViewportService>();

        services.AddSingleton(_ => new BackdropEffectRegistrationService()
            .Register(static renderer => new BlurEffect(renderer))
            .Register(static renderer => new GlassEffect(renderer))
            .Register(static renderer => new SplashScreenBannerEffect(renderer)));
        services.AddScoped<BackdropRenderer>();
        services.AddScoped<EdgeGlowRenderer>();
        return services;
    }

    private static IServiceCollection AddDisplayServices(this IServiceCollection services)
    {
        services.AddSingleton<WindowsCaptureService>();
        services.AddSingleton<WindowsCaptureTargetService>();
        services.AddScoped<DisplayRuntimeFactory>();
        services.AddScoped<DisplayPersistence>();
        services.AddScoped<DisplayState>();
        services.AddScoped<DisplayRuntimeManager>();
        services.AddScoped<IDisplayService, DisplayService>();
        services.AddScoped<ISceneItemDomain, DisplaySceneDomain>();
        return services;
    }

    private static IServiceCollection AddObjectApiServices(this IServiceCollection services)
    {
        services.AddScoped<ITemporarySourceBuilder, TemporarySourceBuilder>();
        services.AddScoped<ApiState>();
        services.AddScoped<LayoutApi>();
        services.AddScoped<TemporarySourceApi>();
        services.AddScoped<SourceBuilderApi>();
        services.AddScoped<SceneApi>();
        services.AddScoped<PersistentSceneApi>();
        services.AddScoped<ObjectMutationApi>();
        services.AddScoped<RuntimeApi>();
        services.AddScoped<ObjectApi>();
        services.AddScoped<IntonerIpcHost>();
        services.AddScoped<MakePlaceColorMapper>();
        services.AddScoped<MakePlaceImportMapper>();
        services.AddScoped<MakePlaceExportMapper>();
        return services;
    }

    private static IServiceCollection AddShortcutServices(this IServiceCollection services)
    {
        services.AddSingleton<IShortcutProvider, EditorShortcuts>();
        services.AddSingleton<IKeyboardInputService, KeyboardInputService>();
        services.AddScoped<IShortcutService, ShortcutService>();
        return services;
    }

    private static IServiceCollection AddEditorServices(this IServiceCollection services)
    {
        services.AddScoped<FileDialogManager>();
        services.AddScoped<UiSharedService>();
        services.AddSingleton<TerritoryArtworkService>();
        services.AddScoped<IntonerThemeStyle>();
        services.AddScoped<IntonerUiPerformanceService>();
        services.AddScoped<IntonerWindowService>();
        services.AddSingleton<IViewportMouseInputService, ViewportMouseInputService>();

        services.AddScoped<IHistoryCoordinator, HistoryCoordinator>();
        services.AddScoped<EditorSelectionService>();
        services.AddScoped<IGizmoHost, EditorGizmoHost>();
        services.AddScoped<DrawManager>();
        services.AddScoped<Gizmo>();
        services.AddScoped<EditorInteraction>();
        services.AddScoped<EditorChatCommands>();
        services.AddScoped<EditorSceneState>();
        services.AddScoped<EditorSceneOverlay>();
        services.AddScoped<EditorShortcutHandler>();
        services.AddScoped<EditorStartupView>();
        services.AddScoped<EditorWorkspaceHost>();
        services.AddScoped<SceneEditorCommands>();
        services.AddScoped<EditorOverlayLayer>();
        services.AddScoped<IUiOverlayTarget>(provider => provider.GetRequiredService<EditorOverlayLayer>());
        services.AddScoped<EditorListCard>();
        services.AddScoped<SceneListRow>();
        services.AddScoped<LayoutActions>();
        services.AddScoped<LayoutWorkspace>();
        services.AddScoped<HistoryWorkspace>();
        services.AddScoped<EditorFilterBar>();
        services.AddScoped<SceneTransformEditor>();
        services.AddScoped<ObjectEditorCatalog>();
        services.AddScoped<ObjectBrowserRowRenderer>();
        services.AddScoped<SceneItemControls>();
        services.AddScoped<SceneItemInspector>();
        services.AddScoped<CreateWorkspace>();
        services.AddScoped<CreateBrowserSelection>();
        services.AddScoped<ObjectCreationDraft>();
        services.AddScoped<ObjectPreviewRenderer>();
        services.AddScoped<ObjectCatalogBrowser>();
        services.AddScoped<ObjectLibraryBrowser>();
        services.AddScoped<ObjectCreateActions>();
        services.AddScoped<ObjectBrowserPanel>();
        services.AddScoped<CatalogLibraryStatus>();
        services.AddScoped<CollectionsWorkspace>();
        services.AddScoped<CollectionModSettingsEditor>();
        services.AddScoped<EditorColumnPanel>();
        services.AddScoped<SceneWorkspace>();
        services.AddScoped<EditorToolbar>();
        services.AddScoped<DebugWorkspace>();
        services.AddScoped<DebugIpcSession>();
        services.AddScoped<DebugIpcSamples>();
        services.AddScoped<SettingsPage>();
        services.AddSingleton<IClipboardTextService, ClipboardTextService>();
        services.AddScoped<IObjectClipboardService, ObjectClipboardService>();
        services.AddScoped<WindowVisibilityService>();

        services.AddScoped<IEditorTitleBarIndicatorProvider, HousingFurnitureLimitIndicatorProvider>();
        services.AddScoped<IEditorTitleBarIndicatorProvider, HousingContextIndicatorProvider>();
        services.AddScoped<IEditorTitleBarIndicatorProvider, DependencyIndicatorProvider>();
        services.AddScoped<EditorTitleBarIndicatorService>();
        services.AddSingleton(provider => TitleBarIconOptions.Embedded(
            provider.GetRequiredService<IntonerBuildInfoService>().TitleBarText, "intoner.png"));
        services.AddScoped<TitleBarIconRenderer>();
        services.AddScoped<TitleBarWindowMetrics>();
        services.AddScoped<EditorWindow>();
        services.AddScoped<EditorBackgroundWindow>();
        return services;
    }
}
