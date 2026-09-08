using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using Intoner.Objects.Api.Ipc;
using Intoner.Utils;
using Microsoft.Extensions.Logging;

namespace Intoner.Ipc;

/// <summary> owns a related set of typed IPC providers </summary>
internal sealed class CallGateProviderCollection : IDisposable
{
    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly ILogger _logger;
    private readonly List<IDisposable> _registrations = [];
    private bool _disposed;

    public CallGateProviderCollection(IDalamudPluginInterface pluginInterface, ILogger logger)
    {
        _pluginInterface = pluginInterface;
        _logger = logger;
    }

    public EventProvider<T> Register<T>(IpcEventEndpoint<T> endpoint)
        => Add(new EventProvider<T>(_pluginInterface, _logger, endpoint));

    public void Register<TReturn>(IpcEndpoint<TReturn> endpoint, Func<TReturn> handler)
        => RegisterCore(
            endpoint.Name,
            () => _pluginInterface.GetIpcProvider<TReturn>(endpoint.Name),
            provider => provider.RegisterFunc(handler),
            static provider => provider.UnregisterFunc());

    public void Register<TReturn>(IpcEndpoint<TReturn> endpoint, Func<IpcContext?, TReturn> handler)
    {
        RegisterCore(
            endpoint.Name,
            () => _pluginInterface.GetIpcProvider<TReturn>(endpoint.Name),
            provider => provider.RegisterFunc(() => handler(provider.GetContext())),
            static provider => provider.UnregisterFunc());
    }

    public void Register<T, TReturn>(IpcEndpoint<T, TReturn> endpoint, Func<T, TReturn> handler)
        => RegisterCore(
            endpoint.Name,
            () => _pluginInterface.GetIpcProvider<T, TReturn>(endpoint.Name),
            provider => provider.RegisterFunc(handler),
            static provider => provider.UnregisterFunc());

    public void Register<T, TReturn>(
        IpcEndpoint<T, TReturn> endpoint,
        Func<IpcContext?, T, TReturn> handler)
    {
        RegisterCore(
            endpoint.Name,
            () => _pluginInterface.GetIpcProvider<T, TReturn>(endpoint.Name),
            provider => provider.RegisterFunc(value => handler(provider.GetContext(), value)),
            static provider => provider.UnregisterFunc());
    }

    public void Register<T1, T2, TReturn>(
        IpcEndpoint<T1, T2, TReturn> endpoint,
        Func<T1, T2, TReturn> handler)
        => RegisterCore(
            endpoint.Name,
            () => _pluginInterface.GetIpcProvider<T1, T2, TReturn>(endpoint.Name),
            provider => provider.RegisterFunc(handler),
            static provider => provider.UnregisterFunc());

    public void RegisterOnFramework<T, TReturn>(
        IpcEndpoint<T, TReturn> endpoint,
        IFramework framework,
        Func<T, TReturn> handler)
        => Register(endpoint, value => FrameworkThreadUtility.Run(framework, () => handler(value)));

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        for (int index = _registrations.Count - 1; index >= 0; --index)
        {
            try
            {
                _registrations[index].Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error unregistering IPC provider");
            }
        }

        _registrations.Clear();
    }

    private void RegisterCore<TProvider>(
        string label,
        Func<TProvider> create,
        Action<TProvider> register,
        Action<TProvider> unregister)
        where TProvider : class
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        TProvider? provider = null;
        try
        {
            provider = create();
            register(provider);
            Add(new IpcRegistration(() => unregister(provider)));
        }
        catch (Exception ex)
        {
            if (provider is not null)
            {
                try
                {
                    unregister(provider);
                }
                catch (Exception cleanupException)
                {
                    _logger.LogWarning(cleanupException, "Error cleaning up IPC provider for {Label}", label);
                }
            }

            throw new InvalidOperationException($"Could not register IPC provider {label}", ex);
        }
    }

    private T Add<T>(T registration)
        where T : IDisposable
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _registrations.Add(registration);
        return registration;
    }

    private sealed class IpcRegistration(Action unregister) : IDisposable
    {
        private Action? _unregister = unregister;

        public void Dispose()
            => Interlocked.Exchange(ref _unregister, null)?.Invoke();
    }
}
