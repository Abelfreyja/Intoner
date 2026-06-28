using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Microsoft.Extensions.Logging;

namespace Intoner.Ipc;

/// <summary> disposable provider for fire and forget ipc events </summary>
internal sealed class EventProvider : IDisposable
{
    private readonly string _label;
    private readonly ILogger _logger;
    private ICallGateProvider<object?>? _provider;

    public EventProvider(IDalamudPluginInterface pluginInterface, ILogger logger, string label)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _label = label;
        _logger = logger;
        try
        {
            _provider = pluginInterface.GetIpcProvider<object?>(label);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error registering IPC provider for {Label}", label);
        }
    }

    public void Invoke()
    {
        try
        {
            _provider?.SendMessage();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception thrown on IPC event {Label}", _label);
        }
    }

    public void Dispose()
        => _provider = null;
}
