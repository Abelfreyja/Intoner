using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Intoner.Logging;

internal static class IntonerLoggingBuilderExtensions
{
    public static ILoggingBuilder AddIntonerLogging(
        this ILoggingBuilder builder,
        IDalamudPluginInterface pluginInterface,
        IPluginLog pluginLog)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(pluginInterface);
        ArgumentNullException.ThrowIfNull(pluginLog);

        IntonerLogOptions options = CreateOptions(pluginInterface);

        builder.ClearProviders();
        builder.SetMinimumLevel(options.TraceMinimumLevel);
        builder.Services.AddSingleton<ILoggerProvider>(provider => new IntonerLoggingProvider(
            pluginLog,
            provider.GetRequiredService<IIntonerLogLevelService>(),
            options));
        return builder;
    }

    private static IntonerLogOptions CreateOptions(IDalamudPluginInterface pluginInterface)
        => new()
        {
            TraceDirectory = Path.Combine(pluginInterface.ConfigDirectory.FullName, "tracelog"),
        };
}
