using Dalamud.Plugin.Services;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace Intoner.Logging;

[ProviderAlias("Intoner")]
internal sealed class IntonerLoggingProvider : ILoggerProvider, ISupportExternalScope
{
    private readonly ConcurrentDictionary<string, IntonerLogger> _loggers = new(StringComparer.Ordinal);
    private readonly IPluginLog _pluginLog;
    private readonly IIntonerLogLevelService _logLevelService;
    private readonly IntonerLogOptions _options;
    private readonly IntonerTraceFileSink _traceFileSink;
    private IExternalScopeProvider _scopeProvider = new LoggerExternalScopeProvider();
    private int _disposed;

    public IntonerLoggingProvider(
        IPluginLog pluginLog,
        IIntonerLogLevelService logLevelService,
        IntonerLogOptions options)
    {
        _pluginLog = pluginLog;
        _logLevelService = logLevelService;
        _options = options;
        _traceFileSink = new IntonerTraceFileSink(options);
    }

    public ILogger CreateLogger(string categoryName)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return _loggers.GetOrAdd(categoryName, CreateLoggerCore);
    }

    public void SetScopeProvider(IExternalScopeProvider scopeProvider)
    {
        ArgumentNullException.ThrowIfNull(scopeProvider);
        _scopeProvider = scopeProvider;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _loggers.Clear();
        _traceFileSink.Dispose();
    }

    private IntonerLogger CreateLoggerCore(string categoryName)
        => new(categoryName, _pluginLog, _logLevelService, _options, _traceFileSink, () => _scopeProvider);
}
