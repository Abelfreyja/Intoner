using Intoner.Objects.Api;
using Intoner.Objects.Assets;
using Intoner.Objects.Assets.Cache;
using Intoner.Objects.Catalog;
using Intoner.Objects.Collections;
using Intoner.Objects.Filesystem.Configuration;
using Intoner.Objects.Filesystem.Layouts;
using Intoner.Objects.Filesystem.Storage;
using Intoner.Objects.Filesystem.Watching;
using Intoner.Objects.Interop;
using Intoner.Objects.Interop.Ipc;
using Intoner.Objects.Preview;
using Intoner.Objects.Preview.Assets;
using Intoner.Objects.Preview.Rendering;
using Intoner.Objects.Rendering.Drawing;
using Intoner.Objects.Rendering.Primitives;
using Intoner.Objects.Resources;
using Intoner.Objects.Runtime;
using Intoner.Objects.UI;
using Intoner.Objects.UI.Services;
using Intoner.Objects.UI.Services.Backdrop;
using Intoner.Objects.UI.Services.EdgeGlow;
using Intoner.Objects.UI.TitleBar;
using Intoner.Services;
using Intoner.UI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Intoner.Objects;

internal static class ObjectServiceCollectionExtensions
{
    public static IServiceCollection AddObjectServices(this IServiceCollection services)
        => services
            .AddObjectStorageServices()
            .AddObjectAssetServices()
            .AddObjectCollectionServices()
            .AddObjectResourceServices()
            .AddObjectLayoutServices()
            .AddObjectRuntimeServices()
            .AddObjectPlacementServices()
            .AddObjectRenderingServices()
            .AddObjectApiServices()
            .AddObjectEditorServices();

    private static IServiceCollection AddObjectStorageServices(this IServiceCollection services)
    {
        services.AddSingleton<IObjectStoragePathService, ObjectStoragePathService>();
        services.AddSingleton<IObjectFileSystem, ObjectFileSystem>();
        services.AddSingleton<IObjectFileWatcherService, ObjectFileWatcherService>();
        services.AddSingleton<IObjectConfigurationService, ObjectConfigurationService>();
        services.AddScoped<IObjectLayoutStore, ObjectLayoutStore>();
        services.AddScoped<IObjectLayoutFileService, ObjectLayoutFileService>();
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
        services.AddSingleton<ObjectAssetCacheSerializer>();
        services.AddSingleton<ObjectAssetCachePayloadReader>();
        services.AddSingleton<IObjectAssetCacheService, ObjectAssetCacheService>();
        services.AddSingleton<IObjectAssetIndex, ObjectAssetIndex>();
        services.AddSingleton<IObjectCatalogService, ObjectCatalogService>();
        services.AddSingleton<PreviewAssetService>();
        services.AddScoped<FurnitureCatalogResolver>();
        services.AddScoped<IFurnitureStainService, FurnitureStainService>();
        services.AddSingleton<IObjectPathResolver, ObjectPathResolver>();
        return services;
    }

    private static IServiceCollection AddObjectCollectionServices(this IServiceCollection services)
    {
        services.AddSingleton<IObjectPenumbraIpc, ObjectPenumbraIpc>();
        services.AddSingleton<IObjectModDataSource, PenumbraObjectModDataSource>();
        services.AddSingleton<IObjectCollectionResolver, ObjectCollectionResolver>();
        services.AddSingleton<IObjectCollectionManager, ObjectCollectionManager>();
        services.AddSingleton<IObjectResolvedCollectionStore, ObjectResolvedCollectionStore>();
        services.AddScoped<IObjectTemporaryCollectionService, ObjectTemporaryCollectionService>();
        services.AddScoped<Func<IObjectTemporaryCollectionService>>(provider => () => provider.GetRequiredService<IObjectTemporaryCollectionService>());
        return services;
    }

    private static IServiceCollection AddObjectResourceServices(this IServiceCollection services)
    {
        services.AddSingleton<ObjectTextureLodService>();
        services.AddSingleton<ObjectResourceLoadScope>();
        services.AddSingleton<IObjectMemoryResourceService, ObjectMemoryResourceService>();
        services.AddSingleton<Func<IObjectMemoryResourceService>>(provider => () => provider.GetRequiredService<IObjectMemoryResourceService>());
        services.AddSingleton<IObjectFileReadService, ObjectFileReadService>();
        services.AddSingleton<IObjectResourceTracker, ObjectResourceTracker>();
        services.AddSingleton<IObjectResourceLoader, ObjectResourceLoader>();
        services.AddSingleton<Func<IObjectFileReadService>>(provider => () => provider.GetRequiredService<IObjectFileReadService>());
        services.AddSingleton<Func<IObjectResourceLoader>>(provider => () => provider.GetRequiredService<IObjectResourceLoader>());
        return services;
    }

    private static IServiceCollection AddObjectLayoutServices(this IServiceCollection services)
    {
        services.AddScoped<ObjectStateLock>();
        services.AddScoped<IObjectKindService, ObjectKindService>();
        services.AddScoped<IObjectRevisionTracker, ObjectRevisionTracker>();
        services.AddScoped<IObjectHousingModePolicy, ObjectHousingModePolicy>();
        services.AddScoped<IObjectLayoutManager, ObjectLayoutManager>();
        services.AddScoped<IObjectFolderService, ObjectFolderService>();
        services.AddScoped<IObjectPersistenceState, ObjectPersistenceState>();
        services.AddScoped<IObjectSceneState, ObjectSceneState>();
        services.AddScoped<IObjectSceneSnapshotResolver, ObjectSceneSnapshotResolver>();
        services.AddScoped<IObjectMutationService, ObjectMutationService>();
        services.AddScoped<IObjectScene, ObjectScene>();
        services.AddScoped<IObjectSceneView, ObjectSceneView>();
        services.AddScoped<IObjectTemporaryScene, ObjectTemporaryScene>();
        services.AddScoped<IObjectManager, ObjectManager>();
        services.AddScoped<IObjectHistoryManager, ObjectHistoryManager>();
        services.AddScoped<IHistoryCoordinator, HistoryCoordinator>();
        services.AddScoped<IClipboardExportService, ClipboardExportService>();
        return services;
    }

    private static IServiceCollection AddObjectRuntimeServices(this IServiceCollection services)
    {
        services.AddSingleton<PrimitiveCallbackRenderer>();
        services.AddSingleton<PrimitiveService>();
        services.AddSingleton<ObjectNativeBindings>();
        services.AddSingleton<FurnitureEmoteGuard>();
        services.AddScoped<ObjectHousingRuntimeContextResolver>();
        services.AddScoped<IObjectRuntimeLocationService, ObjectRuntimeLocationService>();
        services.AddScoped<ObjectSelectionGeometryCache>();
        services.AddScoped<IObjectSelectionService, ObjectSelectionService>();
        services.AddScoped<IObjectSurfaceTargetService, ObjectSurfaceTargetService>();
        services.AddScoped<ISceneObjectFactory, SceneObjectFactory>();
        services.AddScoped<Func<ISceneObjectFactory>>(provider => () => provider.GetRequiredService<ISceneObjectFactory>());
        services.AddScoped<ObjectHousingCullingService>();
        services.AddScoped<IObjectHousingCullingService>(provider => provider.GetRequiredService<ObjectHousingCullingService>());
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
        return services;
    }

    private static IServiceCollection AddObjectRenderingServices(this IServiceCollection services)
    {
        services.AddScoped<IRenderer, NativeRenderer>();
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

    private static IServiceCollection AddObjectApiServices(this IServiceCollection services)
    {
        services.AddScoped<IObjectTemporarySourceBuilder, ObjectTemporarySourceBuilder>();
        services.AddScoped<ObjectPluginStateApi>();
        services.AddScoped<ObjectLayoutApi>();
        services.AddScoped<ObjectTemporaryLayoutApi>();
        services.AddScoped<ObjectTemporaryObjectApi>();
        services.AddScoped<ObjectTemporaryCollectionApi>();
        services.AddScoped<ObjectTemporarySourceBuildApi>();
        services.AddScoped<ObjectQueryApi>();
        services.AddScoped<ObjectMutationApi>();
        services.AddScoped<ObjectRuntimeApi>();
        services.AddScoped<ObjectApi>();
        services.AddScoped<ObjectIpcProviders>();
        services.AddScoped<MakePlaceColorMapper>();
        services.AddScoped<MakePlaceImportMapper>();
        services.AddScoped<MakePlaceExportMapper>();
        return services;
    }

    private static IServiceCollection AddObjectEditorServices(this IServiceCollection services)
    {
        services.AddScoped<IGameInputSuppressionService, GameInputSuppressionService>();
        services.AddScoped<HousingContextIndicatorProvider>();
        services.AddScoped<HousingFurnitureLimitIndicatorProvider>();
        services.AddScoped<EditorTitleBarIndicatorService>(provider => new EditorTitleBarIndicatorService(
            provider.GetRequiredService<HousingFurnitureLimitIndicatorProvider>(),
            provider.GetRequiredService<HousingContextIndicatorProvider>()));
        services.AddScoped(provider =>
        {
            var buildInfo = provider.GetRequiredService<IntonerBuildInfoService>();
            return new TitleBarIconRenderer(
                provider.GetRequiredService<ILogger<TitleBarIconRenderer>>(),
                provider.GetRequiredService<UiSharedService>(),
                TitleBarIconOptions.Embedded(buildInfo.TitleBarText, "intoner.png"));
        });
        services.AddScoped<TitleBarWindowMetrics>();
        services.AddScoped<EditorWindow>();
        return services;
    }
}
