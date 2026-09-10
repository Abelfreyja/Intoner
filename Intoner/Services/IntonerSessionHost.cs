using Dalamud.Plugin.Services;
using Intoner.Objects.Filesystem.Layouts;
using Intoner.Objects.Interop.Ipc;
using Intoner.Objects.UI;
using Intoner.Services.Interop;
using Intoner.UI.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Intoner.Services;

internal sealed class IntonerSessionHost : IAsyncDisposable
{
    private readonly AsyncServiceScope _scope;
    private readonly ILogger<IntonerSessionHost> _logger;
    private readonly IIntonerMediator _mediator;
    private readonly IntonerWindowService _windowService;
    private int _disposed;

    private IntonerSessionHost(
        AsyncServiceScope scope,
        ILogger<IntonerSessionHost> logger,
        IIntonerMediator mediator,
        IntonerWindowService windowService)
    {
        _scope = scope;
        _logger = logger;
        _mediator = mediator;
        _windowService = windowService;
    }

    public static async Task<IntonerSessionHost> CreateAsync(IServiceProvider provider)
    {
        AsyncServiceScope scope = provider.CreateAsyncScope();
        IntonerSessionHost? host = null;

        try
        {
            IServiceProvider scopedProvider = scope.ServiceProvider;
            _ = scopedProvider.GetRequiredService<IObjectLayoutRecoveryService>().TryPrepareSession();
            IntonerWindowService windowService = scopedProvider.GetRequiredService<IntonerWindowService>();
            EditorWindow editorWindow = scopedProvider.GetRequiredService<EditorWindow>();
            EditorBackgroundWindow editorBackgroundWindow = scopedProvider.GetRequiredService<EditorBackgroundWindow>();
            ILogger<IntonerSessionHost> logger = scopedProvider.GetRequiredService<ILogger<IntonerSessionHost>>();
            IntonerSignatures.Verify(scopedProvider.GetRequiredService<ISigScanner>(), logger);
            host = new IntonerSessionHost(
                scope,
                logger,
                scopedProvider.GetRequiredService<IIntonerMediator>(),
                windowService);

            windowService.AddWindow(editorWindow);
            windowService.AddWindow(editorBackgroundWindow);
            _ = scopedProvider.GetRequiredService<IntonerIpcHost>();
            windowService.Start();
            host._logger.LogInformation("Intoner session services initialized");
            return host;
        }
        catch
        {
            await (host?.DisposeAsync() ?? scope.DisposeAsync()).ConfigureAwait(false);
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

        await using (_scope.ConfigureAwait(false))
        {
            _logger.LogInformation("Intoner session services shutting down");
            _windowService.Stop();
        }
    }
}
