using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Intoner.Objects.Api.Ipc;
using Microsoft.Extensions.Logging;

namespace Intoner.Ipc;

/// <summary> publishes one typed IPC event </summary>
internal sealed class EventProvider<T> : IDisposable
{
    private readonly string _label;
    private readonly ILogger _logger;
    private ICallGateProvider<T, object?>? _provider;

    public EventProvider(
        IDalamudPluginInterface pluginInterface,
        ILogger logger,
        IpcEventEndpoint<T> endpoint)
    {
        _label = endpoint.Name;
        _logger = logger;
        _provider = pluginInterface.GetIpcProvider<T, object?>(endpoint.Name);
    }

    public void Publish(T value)
    {
        try
        {
            _provider?.SendMessage(value);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception thrown on IPC event {Label}", _label);
        }
    }

    public void Dispose()
        => _provider = null;
}

/// <summary> subscribes handlers to one typed IPC event </summary>
internal sealed class EventSubscriber<T> : IDisposable
{
    private readonly string _label;
    private readonly ILogger _logger;
    private readonly Dictionary<Action<T>, Action<T>> _handlers = [];
    private ICallGateSubscriber<T, object?>? _subscriber;
    private bool _disabled;

    public EventSubscriber(
        IDalamudPluginInterface pluginInterface,
        ILogger logger,
        IpcEventEndpoint<T> endpoint,
        params Action<T>[] handlers)
    {
        _label = endpoint.Name;
        _logger = logger;
        _subscriber = pluginInterface.GetIpcSubscriber<T, object?>(endpoint.Name);
        foreach (Action<T> handler in handlers)
        {
            Event += handler;
        }
    }

    public void Enable()
    {
        if (!_disabled || _subscriber is null)
        {
            return;
        }

        foreach (Action<T> handler in _handlers.Values)
        {
            _subscriber.Subscribe(handler);
        }

        _disabled = false;
    }

    public void Disable()
    {
        if (_disabled)
        {
            return;
        }

        if (_subscriber is not null)
        {
            foreach (Action<T> handler in _handlers.Values)
            {
                _subscriber.Unsubscribe(handler);
            }
        }

        _disabled = true;
    }

    public event Action<T> Event
    {
        add
        {
            if (_subscriber is null || _handlers.ContainsKey(value))
            {
                return;
            }

            void Invoke(T payload)
            {
                try
                {
                    value(payload);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Exception invoking IPC event {Label}", _label);
                }
            }

            if (_handlers.TryAdd(value, Invoke) && !_disabled)
            {
                _subscriber.Subscribe(Invoke);
            }
        }
        remove
        {
            if (_subscriber is not null && _handlers.Remove(value, out Action<T>? handler))
            {
                _subscriber.Unsubscribe(handler);
            }
        }
    }

    public void Dispose()
    {
        Disable();
        _subscriber = null;
        _handlers.Clear();
    }
}
