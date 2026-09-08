using Dalamud.Plugin.Services;
using Intoner.Objects.Interop;
using Intoner.Objects.Interop.Ipc;
using Intoner.Objects.UI;
using Intoner.Services.Interop;
using Intoner.UI.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Intoner.Services;

internal sealed class IntonerSessionHost : IAsyncDisposable
{
    private static readonly ServiceProviderOptions ProviderOptions = new()
    {
        ValidateOnBuild = true,
        ValidateScopes = true,
    };

    private readonly ServiceProvider _provider;
    private readonly AsyncServiceScope _scope;
    private readonly ILogger<IntonerSessionHost> _logger;
    private readonly IIntonerMediator _mediator;
    private readonly IntonerWindowService _windowService;
    private readonly ObjectHousingCullingService _housingCullingService;
    private int _disposed;

    private IntonerSessionHost(
        ServiceProvider provider,
        AsyncServiceScope scope,
        ILogger<IntonerSessionHost> logger,
        IIntonerMediator mediator,
        IntonerWindowService windowService,
        ObjectHousingCullingService housingCullingService)
    {
        _provider = provider;
        _scope = scope;
        _logger = logger;
        _mediator = mediator;
        _windowService = windowService;
        _housingCullingService = housingCullingService;
    }

    public static async Task<IntonerSessionHost> CreateAsync(
        IntonerDalamudServices dalamudServices,
        CancellationToken cancellationToken)
    {
        ServiceCollection services = [];
        services
            .AddIntonerServices(dalamudServices);

        ServiceProvider provider = services.BuildServiceProvider(ProviderOptions);
        AsyncServiceScope scope = provider.CreateAsyncScope();
        IntonerSessionHost? host = null;

        try
        {
            IServiceProvider scopedProvider = scope.ServiceProvider;
            IntonerWindowService windowService = scopedProvider.GetRequiredService<IntonerWindowService>();
            EditorWindow editorWindow = scopedProvider.GetRequiredService<EditorWindow>();
            EditorBackgroundWindow editorBackgroundWindow = scopedProvider.GetRequiredService<EditorBackgroundWindow>();
            ILogger<IntonerSessionHost> logger = scopedProvider.GetRequiredService<ILogger<IntonerSessionHost>>();
            IntonerSignatures.Verify(scopedProvider.GetRequiredService<ISigScanner>(), logger);
            host = new IntonerSessionHost(
                provider,
                scope,
                logger,
                scopedProvider.GetRequiredService<IIntonerMediator>(),
                windowService,
                scopedProvider.GetRequiredService<ObjectHousingCullingService>());

            windowService.AddWindow(editorWindow);
            windowService.AddWindow(editorBackgroundWindow);
            _ = scopedProvider.GetRequiredService<IntonerIpcHost>();
            windowService.Start();
            await host._housingCullingService.StartAsync(cancellationToken).ConfigureAwait(false);
            host._logger.LogInformation("Intoner session services initialized");
            return host;
        }
        catch
        {
            if (host is not null)
            {
                await host.DisposeAsync().ConfigureAwait(false);
            }
            else
            {
                await scope.DisposeAsync().ConfigureAwait(false);
                await provider.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }
    }

    public void RequestMainWindowToggle()
        => _mediator.Publish(IntonerMainWindowRequest.Toggle);

    public void RequestConfigWindow()
        => _mediator.Publish(IntonerMainWindowRequest.OpenSettings);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            _logger.LogInformation("Intoner session services shutting down");
            _windowService.Stop();
            await _housingCullingService.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            await _scope.DisposeAsync().ConfigureAwait(false);
            await _provider.DisposeAsync().ConfigureAwait(false);
        }
    }
}
