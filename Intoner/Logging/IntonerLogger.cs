using Dalamud.Plugin.Services;
using Microsoft.Extensions.Logging;

namespace Intoner.Logging;

internal sealed class IntonerLogger : ILogger
{
    private readonly IntonerLogCategory _category;
    private readonly IPluginLog _pluginLog;
    private readonly IIntonerLogLevelService _logLevelService;
    private readonly IntonerLogOptions _options;
    private readonly IntonerTraceFileSink _traceFileSink;
    private readonly Func<IExternalScopeProvider> _scopeProvider;

    public IntonerLogger(
        string categoryName,
        IPluginLog pluginLog,
        IIntonerLogLevelService logLevelService,
        IntonerLogOptions options,
        IntonerTraceFileSink traceFileSink,
        Func<IExternalScopeProvider> scopeProvider)
    {
        _category = IntonerLogCategoryFormatter.Create(categoryName, options.CategoryWidth);
        _pluginLog = pluginLog;
        _logLevelService = logLevelService;
        _options = options;
        _traceFileSink = traceFileSink;
        _scopeProvider = scopeProvider;
    }

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull
        => _scopeProvider().Push(state);

    public bool IsEnabled(LogLevel logLevel)
        => logLevel != LogLevel.None
            && (ShouldWriteTraceFile(logLevel) || ShouldWriteDalamud(logLevel));

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);
        if (logLevel is LogLevel.None)
        {
            return;
        }

        bool writeTraceFile = ShouldWriteTraceFile(logLevel);
        bool writeDalamud = ShouldWriteDalamud(logLevel);
        if (!writeTraceFile && !writeDalamud)
        {
            return;
        }

        string message = formatter(state, exception);
        if (message.Length == 0 && exception is null)
        {
            return;
        }

        var scopes = IntonerLogFormatter.CaptureScopes(_scopeProvider());
        if (writeTraceFile)
        {
            WriteTraceFile(logLevel, eventId, message, exception, scopes);
        }

        if (writeDalamud)
        {
            WriteDalamud(logLevel, eventId, message, exception, scopes);
        }
    }

    private bool ShouldWriteTraceFile(LogLevel logLevel)
        => logLevel >= _options.TraceMinimumLevel;

    private bool ShouldWriteDalamud(LogLevel logLevel)
        => logLevel >= _logLevelService.DalamudMinimumLevel;

    private void WriteTraceFile(
        LogLevel logLevel,
        EventId eventId,
        string message,
        Exception? exception,
        IReadOnlyList<string> scopes)
    {
        try
        {
            _traceFileSink.WriteLine(IntonerLogFormatter.FormatTraceLine(
                DateTimeOffset.Now,
                logLevel,
                _category,
                eventId,
                message,
                exception,
                scopes));
        }
        catch
        {
            // logging must not break plugin work
        }
    }

    private void WriteDalamud(
        LogLevel logLevel,
        EventId eventId,
        string message,
        Exception? exception,
        IReadOnlyList<string> scopes)
    {
        var output = IntonerLogFormatter.FormatDalamudMessage(_category, eventId, message, scopes);
        switch (logLevel)
        {
            case LogLevel.Trace:
                _pluginLog.Verbose(exception, "{Message}", output);
                break;
            case LogLevel.Debug:
                _pluginLog.Debug(exception, "{Message}", output);
                break;
            case LogLevel.Information:
                _pluginLog.Information(exception, "{Message}", output);
                break;
            case LogLevel.Warning:
                _pluginLog.Warning(exception, "{Message}", output);
                break;
            case LogLevel.Error:
                _pluginLog.Error(exception, "{Message}", output);
                break;
            case LogLevel.Critical:
                _pluginLog.Fatal(exception, "{Message}", output);
                break;
            default:
                return;
        }
    }
}
