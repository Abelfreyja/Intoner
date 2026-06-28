using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Microsoft.Extensions.Logging;

namespace Intoner.Ipc;

/// <summary> disposable provider for ipc functions </summary>
internal sealed class FuncProvider<TRet> : IDisposable
{
    private ICallGateProvider<TRet>? _provider;

    public FuncProvider(IDalamudPluginInterface pluginInterface, ILogger logger, string label, Func<TRet> func)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ICallGateProvider<TRet>? provider = null;
        try
        {
            provider = pluginInterface.GetIpcProvider<TRet>(label);
            provider.RegisterFunc(func);
            _provider = provider;
        }
        catch (Exception ex)
        {
            provider?.UnregisterFunc();
            logger.LogError(ex, "Error registering IPC provider for {Label}", label);
            _provider = null;
        }
    }

    public void Dispose()
    {
        _provider?.UnregisterFunc();
        _provider = null;
    }
}

/// <inheritdoc cref="FuncProvider{TRet}"/>
internal sealed class FuncProvider<T1, TRet> : IDisposable
{
    private ICallGateProvider<T1, TRet>? _provider;

    public FuncProvider(IDalamudPluginInterface pluginInterface, ILogger logger, string label, Func<T1, TRet> func)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ICallGateProvider<T1, TRet>? provider = null;
        try
        {
            provider = pluginInterface.GetIpcProvider<T1, TRet>(label);
            provider.RegisterFunc(func);
            _provider = provider;
        }
        catch (Exception ex)
        {
            provider?.UnregisterFunc();
            logger.LogError(ex, "Error registering IPC provider for {Label}", label);
            _provider = null;
        }
    }

    public void Dispose()
    {
        _provider?.UnregisterFunc();
        _provider = null;
    }
}
