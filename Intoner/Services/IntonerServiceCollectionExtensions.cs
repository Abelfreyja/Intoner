using Dalamud.Interface;
using Dalamud.Interface.ImGuiFileDialog;
using Intoner.Logging;
using Intoner.Services.Gpu;
using Intoner.UI;
using Intoner.UI.Performance;
using Intoner.UI.Theme;
using Intoner.UI.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Intoner.Services;

internal static class IntonerServiceCollectionExtensions
{
    public static IServiceCollection AddIntonerCoreServices(
        this IServiceCollection services,
        IntonerDalamudServices dalamudServices)
    {
        services.AddSingleton<IIntonerLogLevelService, IntonerLogLevelService>();

        services.AddLogging(builder =>
        {
            builder.AddIntonerLogging(dalamudServices.PluginInterface, dalamudServices.Log);
        });

        services.AddSingleton(dalamudServices.PluginInterface);
        services.AddSingleton(dalamudServices.CommandManager);
        services.AddSingleton(dalamudServices.ClientState);
        services.AddSingleton(dalamudServices.Condition);
        services.AddSingleton(dalamudServices.DataManager);
        services.AddSingleton(dalamudServices.Framework);
        services.AddSingleton(dalamudServices.GameInteropProvider);
        services.AddSingleton(dalamudServices.ObjectTable);
        services.AddSingleton(dalamudServices.PlayerState);
        services.AddSingleton(dalamudServices.SigScanner);
        services.AddSingleton(dalamudServices.TextureProvider);
        services.AddSingleton<IUiBuilder>(dalamudServices.PluginInterface.UiBuilder);
        services.AddSingleton<IHostEnvironment>(_ => IntonerHostEnvironment.FromPluginInterface(dalamudServices.PluginInterface));

        services.AddSingleton<FileDialogManager>();
        services.AddSingleton<IntonerBuildInfoService>();
        services.AddSingleton<UiSharedService>();
        services.AddSingleton<GpuProcessingService>();
        services.AddScoped<IntonerMediator>();
        services.AddScoped<IIntonerMediator>(provider => provider.GetRequiredService<IntonerMediator>());
        services.AddScoped<IntonerThemeStyle>();
        services.AddScoped<IntonerUiPerformanceService>();
        services.AddScoped<IntonerWindowService>();

        return services;
    }
}
