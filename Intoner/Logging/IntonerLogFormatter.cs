using Microsoft.Extensions.Logging;
using System.Text;

namespace Intoner.Logging;

internal static class IntonerLogFormatter
{
    public static string FormatDalamudMessage(
        IntonerLogCategory category,
        EventId eventId,
        string message,
        IReadOnlyList<string> scopes)
    {
        StringBuilder builder = new();
        builder.Append('[').Append(category.DisplayName).Append(']');
        AppendEventId(builder, eventId);
        AppendScopes(builder, scopes);
        builder.Append(' ').Append(message);
        return builder.ToString();
    }

    public static string FormatTraceLine(
        DateTimeOffset timestamp,
        LogLevel level,
        IntonerLogCategory category,
        EventId eventId,
        string message,
        Exception? exception,
        IReadOnlyList<string> scopes)
    {
        StringBuilder builder = new();
        builder.Append(timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff zzz"));
        builder.Append(" | ").Append(GetLevelLabel(level));
        builder.Append(" | ").Append(Environment.CurrentManagedThreadId.ToString("D2"));
        builder.Append(" | [").Append(category.FullName).Append(']');
        AppendEventId(builder, eventId);
        AppendScopes(builder, scopes);
        builder.Append(' ').Append(message);
        AppendException(builder, exception);
        return builder.ToString();
    }

    public static string GetLevelLabel(LogLevel level)
        => level switch
        {
            LogLevel.Trace => "TRC",
            LogLevel.Debug => "DBG",
            LogLevel.Information => "INF",
            LogLevel.Warning => "WRN",
            LogLevel.Error => "ERR",
            LogLevel.Critical => "CRT",
            _ => "NON",
        };

    public static IReadOnlyList<string> CaptureScopes(IExternalScopeProvider? scopeProvider)
    {
        if (scopeProvider is null)
        {
            return [];
        }

        List<string> scopes = [];
        scopeProvider.ForEachScope(static (scope, list) =>
        {
            var text = FormatScope(scope);
            if (text.Length > 0)
            {
                list.Add(text);
            }
        }, scopes);
        return scopes;
    }

    private static string FormatScope(object? scope)
    {
        if (scope is null)
        {
            return string.Empty;
        }

        if (scope is IEnumerable<KeyValuePair<string, object?>> values)
        {
            StringBuilder builder = new();
            foreach (KeyValuePair<string, object?> pair in values)
            {
                if (string.Equals(pair.Key, "{OriginalFormat}", StringComparison.Ordinal))
                {
                    continue;
                }

                if (builder.Length > 0)
                {
                    builder.Append(", ");
                }

                builder.Append(pair.Key).Append('=').Append(pair.Value);
            }

            return builder.ToString();
        }

        return scope.ToString() ?? string.Empty;
    }

    private static void AppendEventId(StringBuilder builder, EventId eventId)
    {
        if (eventId.Id != 0 || !string.IsNullOrWhiteSpace(eventId.Name))
        {
            builder.Append(" event=");
            if (!string.IsNullOrWhiteSpace(eventId.Name))
            {
                builder.Append(eventId.Name).Append('/');
            }

            builder.Append(eventId.Id);
        }
    }

    private static void AppendScopes(StringBuilder builder, IReadOnlyList<string> scopes)
    {
        if (scopes.Count == 0)
        {
            return;
        }

        builder.Append(" scope=[");
        for (var i = 0; i < scopes.Count; ++i)
        {
            if (i > 0)
            {
                builder.Append(" > ");
            }

            builder.Append(scopes[i]);
        }

        builder.Append(']');
    }

    private static void AppendException(StringBuilder builder, Exception? exception)
    {
        if (exception is null)
        {
            return;
        }

        builder.AppendLine();
        builder.Append(exception);
    }
}
