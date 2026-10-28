using Intoner.Objects.Filesystem.Layouts;
using Intoner.Objects.Interop;
using Intoner.Objects.Interop.Ipc;
using Intoner.Objects.Runtime;
using Intoner.Objects.UI;
using Intoner.Objects.UI.Services;
using Intoner.UI.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Intoner.Services;

internal sealed class IntonerSessionHost : IAsyncDisposable
{
    private readonly AsyncServiceScope _scope;
    private readonly EditorChatCommands _commands;
    private int _disposed;

    private IntonerSessionHost(AsyncServiceScope scope, EditorChatCommands commands)
    {
        _scope = scope;
        _commands = commands;
    }

    public static async Task<IntonerSessionHost> CreateAsync(IServiceProvider provider)
    {
        AsyncServiceScope scope = provider.CreateAsyncScope();

        try
        {
            IServiceProvider scopedProvider = scope.ServiceProvider;
            await Task.Run(() =>
            {
                _ = scopedProvider.GetRequiredService<IObjectLayoutRecoveryService>().TryPrepareSession();
                _ = scopedProvider.GetRequiredService<FurnitureEmoteGuard>();
                _ = scopedProvider.GetRequiredService<CameraCollisionService>();
            }).ConfigureAwait(false);
            await scopedProvider.GetRequiredService<FurnitureEmoteGuard>().StartAsync().ConfigureAwait(false);
            await scopedProvider.GetRequiredService<CameraCollisionService>().StartAsync().ConfigureAwait(false);
            IntonerSession session = await Task.Run(() => scopedProvider.GetRequiredService<IntonerSession>()).ConfigureAwait(false);
            EditorChatCommands commands = scopedProvider.GetRequiredService<EditorChatCommands>();
            session.Start();
            return new IntonerSessionHost(scope, commands);
        }
        catch
        {
            await scope.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public void ExecuteCommand(IntonerCommand command)
        => _commands.Execute(command);

    public ValueTask DisposeAsync()
        => Interlocked.Exchange(ref _disposed, 1) == 0 ? _scope.DisposeAsync() : ValueTask.CompletedTask;
}

internal sealed class IntonerSession : IDisposable
{
    private readonly ILogger<IntonerSession> _logger;
    private readonly IntonerWindowService    _windows;
    private readonly EditorWindow            _editor;
    private readonly EditorBackgroundWindow  _background;
    private readonly IntonerIpcHost          _ipc;
    private readonly ObjectSceneHost         _sceneHost;
    private bool _started;
    private bool _disposed;

    public IntonerSession(
        ILogger<IntonerSession> logger,
        IntonerWindowService windows,
        EditorWindow editor,
        EditorBackgroundWindow background,
        IntonerIpcHost ipc,
        ObjectSceneHost sceneHost)
    {
        _logger     = logger;
        _windows    = windows;
        _editor     = editor;
        _background = background;
        _ipc        = ipc;
        _sceneHost  = sceneHost;
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_started)
        {
            return;
        }

        _sceneHost.Start();
        _windows.AddWindow(_editor);
        _windows.AddWindow(_background);
        _ipc.Start();
        _windows.Start();
        _started    = true;
        _logger.LogInformation("Intoner session services initialized");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed   = true;
        _windows.Stop();
        _logger.LogInformation("Intoner session services shutting down");
    }
}
