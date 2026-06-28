using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Intoner.Ipc;
using Intoner.Objects.Api;
using Intoner.Objects.Runtime;
using Microsoft.Extensions.Logging;

namespace Intoner.Objects.Interop.Ipc;

internal sealed class ObjectIpcProviders : IDisposable
{
    private readonly ILogger<ObjectIpcProviders> _logger;
    private readonly IFramework                  _framework;
    private readonly EventProvider               _disposedProvider;
    private readonly EventProvider               _initializedProvider;
    private readonly EventProvider               _persistentSceneChangedProvider;
    private readonly IObjectManager              _objectManager;
    private readonly List<IDisposable>           _providers = [];
    private readonly Lock                        _pendingPersistentSceneChangedLock = new();

    private bool _pendingPersistentSceneChanged;
    private int  _disposed;

    public ObjectIpcProviders(
        ILogger<ObjectIpcProviders> logger,
        IDalamudPluginInterface pluginInterface,
        IFramework framework,
        IObjectManager objectManager,
        ObjectApi api)
    {
        _logger                         = logger;
        _framework                      = framework;
        _objectManager                  = objectManager;
        ObjectIpcContext context        = new(pluginInterface, logger);
        _disposedProvider               = ObjectIpcSubscribers.Disposed.Provider(context);
        _initializedProvider            = ObjectIpcSubscribers.Initialized.Provider(context);
        _persistentSceneChangedProvider = ObjectIpcSubscribers.PersistentSceneChanged.Provider(context);
        try
        {
            AddProviders(_providers, context, api);
            _framework.Update += HandleFrameworkUpdate;
            _objectManager.PersistentSceneChanged += HandlePersistentSceneChanged;
            _logger.LogInformation("Registered object IPC providers");
            _initializedProvider.Invoke();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _framework.Update -= HandleFrameworkUpdate;
        _objectManager.PersistentSceneChanged -= HandlePersistentSceneChanged;
        for (var i = _providers.Count - 1; i >= 0; --i)
        {
            _providers[i].Dispose();
        }

        _providers.Clear();
        _persistentSceneChangedProvider.Dispose();
        _initializedProvider.Dispose();
        _disposedProvider.Invoke();
        _disposedProvider.Dispose();
        _logger.LogInformation("Unregistered object IPC providers");
    }

    private static void AddProviders(List<IDisposable> providers, ObjectIpcContext context, ObjectApi api)
    {
        providers.Add(ObjectIpcSubscribers.ApiVersion.Provider(context, api.PluginState));
        providers.Add(ObjectIpcSubscribers.ApiBreakingVersion.Provider(context, api.PluginState));

        providers.Add(ObjectIpcSubscribers.GetLayouts.Provider(context, api.Layout));
        providers.Add(ObjectIpcSubscribers.GetLoadedLayouts.Provider(context, api.Layout));
        providers.Add(ObjectIpcSubscribers.GetDefaultLayout.Provider(context, api.Layout));
        providers.Add(ObjectIpcSubscribers.CreateLayout.Provider(context, api.Layout));
        providers.Add(ObjectIpcSubscribers.SaveCurrentLayout.Provider(context, api.Layout));
        providers.Add(ObjectIpcSubscribers.SetDefaultLayout.Provider(context, api.Layout));
        providers.Add(ObjectIpcSubscribers.ClearDefaultLayout.Provider(context, api.Layout));
        providers.Add(ObjectIpcSubscribers.DeleteLayout.Provider(context, api.Layout));

        providers.Add(ObjectIpcSubscribers.GetTemporaryLayouts.Provider(context, api.TemporaryLayouts));
        providers.Add(ObjectIpcSubscribers.ApplyTemporaryLayout.Provider(context, api.TemporaryLayouts));
        providers.Add(ObjectIpcSubscribers.ApplyTemporaryObjectChanges.Provider(context, api.TemporaryObjects));
        providers.Add(ObjectIpcSubscribers.UpsertTemporaryObject.Provider(context, api.TemporaryObjects));
        providers.Add(ObjectIpcSubscribers.PatchTemporaryObject.Provider(context, api.TemporaryObjects));
        providers.Add(ObjectIpcSubscribers.RemoveTemporaryObject.Provider(context, api.TemporaryObjects));
        providers.Add(ObjectIpcSubscribers.RemoveTemporaryLayout.Provider(context, api.TemporaryLayouts));
        providers.Add(ObjectIpcSubscribers.ApplyTemporaryCollections.Provider(context, api.TemporaryCollections));
        providers.Add(ObjectIpcSubscribers.UpsertTemporaryCollection.Provider(context, api.TemporaryCollections));
        providers.Add(ObjectIpcSubscribers.RemoveTemporaryCollections.Provider(context, api.TemporaryCollections));
        providers.Add(ObjectIpcSubscribers.BuildTemporarySource.Provider(context, api.TemporarySourceBuild));

        providers.Add(ObjectIpcSubscribers.GetSceneSnapshot.Provider(context, api.Query));
        providers.Add(ObjectIpcSubscribers.GetObject.Provider(context, api.Query));

        providers.Add(ObjectIpcSubscribers.CreateObject.Provider(context, api.Mutation));
        providers.Add(ObjectIpcSubscribers.ImportObject.Provider(context, api.Mutation));
        providers.Add(ObjectIpcSubscribers.UpdateObject.Provider(context, api.Mutation));
        providers.Add(ObjectIpcSubscribers.PatchObject.Provider(context, api.Mutation));
        providers.Add(ObjectIpcSubscribers.RemoveObject.Provider(context, api.Mutation));
        providers.Add(ObjectIpcSubscribers.DuplicateObject.Provider(context, api.Mutation));

        providers.Add(ObjectIpcSubscribers.GetRuntimeStates.Provider(context, api.Runtime));
        providers.Add(ObjectIpcSubscribers.GetRuntimeState.Provider(context, api.Runtime));
    }

    private void HandlePersistentSceneChanged()
    {
        lock (_pendingPersistentSceneChangedLock)
        {
            _pendingPersistentSceneChanged = true;
        }
    }

    private void HandleFrameworkUpdate(IFramework framework)
    {
        _ = framework;

        var shouldPublish = false;
        lock (_pendingPersistentSceneChangedLock)
        {
            if (_pendingPersistentSceneChanged)
            {
                _pendingPersistentSceneChanged = false;
                shouldPublish = true;
            }
        }

        if (shouldPublish)
        {
            _persistentSceneChangedProvider.Invoke();
        }
    }
}
