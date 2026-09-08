using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Intoner.Ipc;
using Intoner.Objects.Api;
using Intoner.Objects.Api.Ipc;
using Intoner.Objects.Runtime;
using Intoner.Utils;
using Microsoft.Extensions.Logging;

namespace Intoner.Objects.Interop.Ipc;

/// <summary>owns intoner's IPC endpoint registrations and revision events</summary>
internal sealed class IntonerIpcHost : IDisposable
{
    private readonly ILogger<IntonerIpcHost> _logger;
    private readonly IFramework _framework;
    private readonly IObjectRevisionTracker _revisionTracker;
    private readonly IObjectSceneView _sceneView;
    private readonly ObjectApi _api;
    private readonly CallGateProviderCollection _providers;
    private readonly EventProvider<ObjectApiStateChanged> _stateChanged;
    private readonly EventProvider<ObjectSceneChanged> _sceneChanged;
    private readonly EventProvider<PersistentObjectSceneChanged> _persistentSceneChanged;
    private readonly EventProvider<SavedObjectLayoutsChanged> _savedLayoutsChanged;

    private int _pendingSceneChanged;
    private int _pendingPersistentSceneChanged;
    private int _pendingSavedLayoutsChanged;
    private bool _subscribed;
    private int _disposed;

    public IntonerIpcHost(
        ILogger<IntonerIpcHost> logger,
        IDalamudPluginInterface pluginInterface,
        IFramework framework,
        IObjectRevisionTracker revisionTracker,
        IObjectSceneView sceneView,
        ObjectApi api)
    {
        _logger = logger;
        _framework = framework;
        _revisionTracker = revisionTracker;
        _sceneView = sceneView;
        _api = api;
        _providers = new CallGateProviderCollection(pluginInterface, logger);

        try
        {
            _stateChanged = _providers.Register(ObjectIpcEndpoints.Events.StateChanged);
            _sceneChanged = _providers.Register(ObjectIpcEndpoints.Events.SceneChanged);
            _persistentSceneChanged = _providers.Register(ObjectIpcEndpoints.Events.PersistentSceneChanged);
            _savedLayoutsChanged = _providers.Register(ObjectIpcEndpoints.Events.SavedLayoutsChanged);
            RegisterEndpoints();

            Subscribe();
            _stateChanged.Publish(new ObjectApiStateChanged(_api.PluginState.GetInfo()));
            _logger.LogInformation(
                "Registered Intoner API v{Breaking}.{Feature} providers",
                ObjectApiVersions.Breaking,
                ObjectApiVersions.Feature);
        }
        catch
        {
            Unsubscribe();
            _providers.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        ObjectApiInfo disposingInfo = _api.PluginState.BeginDisposing();
        Unsubscribe();
        _stateChanged.Publish(new ObjectApiStateChanged(disposingInfo));
        _providers.Dispose();
        _logger.LogInformation("Unregistered Intoner API providers");
    }

    private void RegisterEndpoints()
    {
        _providers.Register(ObjectIpcEndpoints.State.GetInfo, _api.PluginState.GetInfo);

        _providers.Register(ObjectIpcEndpoints.Layouts.GetAll, _api.Layouts.GetAll);
        _providers.Register(ObjectIpcEndpoints.Layouts.Get, _api.Layouts.Get);
        _providers.Register(ObjectIpcEndpoints.Layouts.GetDefault, _api.Layouts.GetDefault);
        _providers.RegisterOnFramework(ObjectIpcEndpoints.Layouts.Create, _framework, _api.Layouts.Create);
        _providers.RegisterOnFramework(ObjectIpcEndpoints.Layouts.SaveCurrent, _framework, _api.Layouts.SaveCurrent);
        _providers.RegisterOnFramework(ObjectIpcEndpoints.Layouts.SetDefault, _framework, _api.Layouts.SetDefault);
        _providers.RegisterOnFramework(ObjectIpcEndpoints.Layouts.ClearDefault, _framework, _api.Layouts.ClearDefault);
        _providers.RegisterOnFramework(ObjectIpcEndpoints.Layouts.Delete, _framework, _api.Layouts.Delete);

        _providers.Register(
            ObjectIpcEndpoints.TemporarySources.GetAll,
            context => IpcSourceOwnership.TryCreateOwnerPrefix(context, out string prefix)
                ? _api.TemporarySources.GetSources(prefix)
                : []);
        RegisterTemporaryMutation(
            ObjectIpcEndpoints.TemporarySources.Apply,
            "temporary source apply request is invalid",
            static request => request.SourceId,
            static (api, sourceKey, request) => api.ApplySource(sourceKey, request));
        RegisterTemporaryMutation(
            ObjectIpcEndpoints.TemporarySources.ApplyObjectChanges,
            "temporary object change request is invalid",
            static request => request.SourceId,
            static (api, sourceKey, request) => api.ApplyObjectChanges(sourceKey, request));
        RegisterTemporaryMutation(
            ObjectIpcEndpoints.TemporarySources.Remove,
            "temporary source removal request is invalid",
            static request => request.SourceId,
            static (api, sourceKey, request) => api.RemoveSource(sourceKey, request));
        _providers.Register(ObjectIpcEndpoints.Sharing.BuildSource, _api.Sharing.BuildTemporarySource);

        _providers.Register(ObjectIpcEndpoints.Scene.GetSnapshot, _api.Scene.GetSceneSnapshot);
        _providers.Register(ObjectIpcEndpoints.Scene.GetObject, _api.Scene.GetObject);
        _providers.Register(ObjectIpcEndpoints.Scene.GetLoadedLayouts, _api.Scene.GetLoadedLayouts);
        _providers.Register(ObjectIpcEndpoints.PersistentScene.GetSnapshot, _api.PersistentScene.GetSnapshot);
        _providers.Register(ObjectIpcEndpoints.PersistentScene.GetObject, _api.PersistentScene.GetObject);
        _providers.RegisterOnFramework(ObjectIpcEndpoints.PersistentScene.Apply, _framework, _api.PersistentScene.Apply);

        _providers.RegisterOnFramework(ObjectIpcEndpoints.Objects.Create, _framework, _api.Objects.Create);
        _providers.RegisterOnFramework(ObjectIpcEndpoints.Objects.Import, _framework, _api.Objects.Import);
        _providers.RegisterOnFramework(ObjectIpcEndpoints.Objects.Update, _framework, _api.Objects.Update);
        _providers.RegisterOnFramework(ObjectIpcEndpoints.Objects.Patch, _framework, _api.Objects.Patch);
        _providers.RegisterOnFramework(ObjectIpcEndpoints.Objects.Remove, _framework, _api.Objects.Remove);
        _providers.RegisterOnFramework(ObjectIpcEndpoints.Objects.Duplicate, _framework, _api.Objects.Duplicate);

        _providers.Register(ObjectIpcEndpoints.Runtime.GetAll, _api.Runtime.GetStates);
        _providers.Register(ObjectIpcEndpoints.Runtime.Get, _api.Runtime.GetState);
    }

    private void Subscribe()
    {
        _subscribed = true;
        _framework.Update += HandleFrameworkUpdate;
        _revisionTracker.SceneChanged += HandleSceneChanged;
        _revisionTracker.PersistentSceneChanged += HandlePersistentSceneChanged;
        _revisionTracker.SavedLayoutsChanged += HandleSavedLayoutsChanged;
    }

    private void Unsubscribe()
    {
        if (!_subscribed)
        {
            return;
        }

        _framework.Update -= HandleFrameworkUpdate;
        _revisionTracker.SceneChanged -= HandleSceneChanged;
        _revisionTracker.PersistentSceneChanged -= HandlePersistentSceneChanged;
        _revisionTracker.SavedLayoutsChanged -= HandleSavedLayoutsChanged;
        _subscribed = false;
    }

    private void RegisterTemporaryMutation<TRequest>(
        IpcEndpoint<TRequest, TemporarySourceMutationResult> endpoint,
        string invalidRequestMessage,
        Func<TRequest, string> getSourceId,
        Func<TemporarySourceApi, string, TRequest, TemporarySourceMutationResult> invoke)
        where TRequest : class
    {
        _providers.Register(
            endpoint,
            (context, request) =>
            {
                if (request is null)
                {
                    return _api.TemporarySources.InvalidRequest(invalidRequestMessage);
                }

                return IpcSourceOwnership.TryCreateSourceKey(
                        context,
                        getSourceId(request),
                        out string sourceKey)
                    ? FrameworkThreadUtility.Run(
                        _framework,
                        () => invoke(_api.TemporarySources, sourceKey, request))
                    : _api.TemporarySources.OwnershipFailure();
            });
    }

    private void HandleSceneChanged()
        => Interlocked.Exchange(ref _pendingSceneChanged, 1);

    private void HandlePersistentSceneChanged()
        => Interlocked.Exchange(ref _pendingPersistentSceneChanged, 1);

    private void HandleSavedLayoutsChanged()
        => Interlocked.Exchange(ref _pendingSavedLayoutsChanged, 1);

    private void HandleFrameworkUpdate(IFramework _)
    {
        bool sceneChanged = Interlocked.Exchange(ref _pendingSceneChanged, 0) != 0;
        bool persistentSceneChanged = Interlocked.Exchange(ref _pendingPersistentSceneChanged, 0) != 0;
        bool savedLayoutsChanged = Interlocked.Exchange(ref _pendingSavedLayoutsChanged, 0) != 0;
        if (!sceneChanged && !persistentSceneChanged && !savedLayoutsChanged)
        {
            return;
        }

        ObjectApiInfo info = _api.PluginState.GetInfo();
        if (sceneChanged)
        {
            _sceneChanged.Publish(new ObjectSceneChanged(info.InstanceId, _sceneView.GetSceneRevision()));
        }

        if (persistentSceneChanged)
        {
            _persistentSceneChanged.Publish(new PersistentObjectSceneChanged(
                info.InstanceId,
                _sceneView.GetPersistentSceneRevision()));
        }

        if (savedLayoutsChanged)
        {
            _savedLayoutsChanged.Publish(new SavedObjectLayoutsChanged(
                info.InstanceId,
                _revisionTracker.GetSavedLayoutsRevision()));
        }
    }
}
