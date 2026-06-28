using Intoner.Objects.Interop;
using Intoner.Objects.Interop.Ipc;
using Intoner.Objects.UI;
using Intoner.Services;
using Intoner.UI.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Intoner.Objects;

internal sealed class ObjectServiceHost : IAsyncDisposable
{
    private static readonly ServiceProviderOptions ProviderOptions = new()
    {
        ValidateOnBuild = true,
        ValidateScopes = true,
    };

    private readonly ServiceProvider _provider;
    private readonly AsyncServiceScope _scope;
    private readonly ILogger<ObjectServiceHost> _logger;
    private readonly IIntonerMediator _mediator;
    private readonly IntonerWindowService _windowService;
    private readonly ObjectHousingCullingService _housingCullingService;
    private int _disposed;

    private ObjectServiceHost(
        ServiceProvider provider,
        AsyncServiceScope scope,
        ILogger<ObjectServiceHost> logger,
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

    public static async Task<ObjectServiceHost> CreateAsync(
        IntonerDalamudServices dalamudServices,
        CancellationToken cancellationToken)
    {
        ServiceCollection services = [];
        services
            .AddIntonerCoreServices(dalamudServices)
            .AddObjectServices();

        ServiceProvider provider = services.BuildServiceProvider(ProviderOptions);
        AsyncServiceScope scope = provider.CreateAsyncScope();
        ObjectServiceHost? host = null;

        try
        {
            IServiceProvider scopedProvider = scope.ServiceProvider;
            IntonerWindowService windowService = scopedProvider.GetRequiredService<IntonerWindowService>();
            EditorWindow editorWindow = scopedProvider.GetRequiredService<EditorWindow>();
            host = new ObjectServiceHost(
                provider,
                scope,
                scopedProvider.GetRequiredService<ILogger<ObjectServiceHost>>(),
                scopedProvider.GetRequiredService<IIntonerMediator>(),
                windowService,
                scopedProvider.GetRequiredService<ObjectHousingCullingService>());

            windowService.AddWindow(editorWindow);
            _ = scopedProvider.GetRequiredService<ObjectIpcProviders>();
            windowService.Start();
            await host._housingCullingService.StartAsync(cancellationToken).ConfigureAwait(false);
            host._logger.LogInformation("Intoner object services initialized");
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
            _logger.LogInformation("Intoner object services shutting down");
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
