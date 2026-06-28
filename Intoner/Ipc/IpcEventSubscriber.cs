using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Microsoft.Extensions.Logging;

namespace Intoner.Ipc;

/// <summary> disposable subscriber for fire and forget ipc events </summary>
internal sealed class EventSubscriber : IDisposable
{
    private readonly string _label;
    private readonly ILogger _logger;
    private readonly Dictionary<Action, Action> _delegates = [];
    private ICallGateSubscriber<object?>? _subscriber;
    private bool _disabled;

    public EventSubscriber(IDalamudPluginInterface pluginInterface, ILogger logger, string label, params Action[] actions)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _label = label;
        _logger = logger;
        try
        {
            _subscriber = pluginInterface.GetIpcSubscriber<object?>(label);
            foreach (var action in actions)
            {
                Event += action;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error registering IPC subscriber for {Label}", label);
        }
    }

    public void Enable()
    {
        if (!_disabled || _subscriber is null)
        {
            return;
        }

        foreach (var action in _delegates.Values)
        {
            _subscriber.Subscribe(action);
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
            foreach (var action in _delegates.Values)
            {
                _subscriber.Unsubscribe(action);
            }
        }

        _disabled = true;
    }

    public event Action Event
    {
        add
        {
            if (_subscriber is null || _delegates.ContainsKey(value))
            {
                return;
            }

            void Wrapped()
            {
                try
                {
                    value();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Exception invoking IPC event {Label}", _label);
                }
            }

            if (_delegates.TryAdd(value, Wrapped) && !_disabled)
            {
                _subscriber.Subscribe(Wrapped);
            }
        }
        remove
        {
            if (_subscriber is not null && _delegates.Remove(value, out var action))
            {
                _subscriber.Unsubscribe(action);
            }
        }
    }

    public void Dispose()
    {
        Disable();
        _subscriber = null;
        _delegates.Clear();
    }
}
