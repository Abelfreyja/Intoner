using Intoner.Scene.Rendering;
using Intoner.Utils;
using Microsoft.Extensions.Logging;

namespace Intoner.Objects.Rendering.Primitives;

internal sealed class PrimitiveService : ISceneRenderPass, IDisposable
{
    private static readonly TimeSpan SubmittedCommandLifetime = TimeSpan.FromMilliseconds(250);

    private readonly ILogger<PrimitiveService> _logger;
    private readonly SceneRenderService _sceneRenderer;
    private readonly PrimitiveCallbackRenderer _renderer;
    private readonly Lock _activationLock = new();
    private readonly DisposalState _disposeState = new();
    private readonly PrimitiveCommandStore _commands = new(SubmittedCommandLifetime);
    private readonly PrimitiveCommandList _drawCommands = new();

    private SceneRenderService.Registration? _registration;
    private int _loggedDrawFailure;
    private int _loggedDrawSuccess;

    public PrimitiveService(
        ILogger<PrimitiveService> logger,
        SceneRenderService sceneRenderer,
        PrimitiveCallbackRenderer renderer)
    {
        _logger = logger;
        _sceneRenderer = sceneRenderer;
        _renderer = renderer;
    }

    public bool Commit(PrimitiveCommandList commands, PrimitiveDrawState state)
    {
        lock (_activationLock)
        {
            if (_disposeState.IsDisposing || !_commands.Commit(commands, state))
            {
                return false;
            }

            if (_registration != null)
            {
                return true;
            }

            _registration = _sceneRenderer.TryRegister(this);
            if (_registration == null)
            {
                _commands.Clear();
            }

            return _registration != null;
        }
    }

    public void Deactivate()
    {
        lock (_activationLock)
        {
            DeactivateLocked();
        }
    }

    public void DeactivateIfIdle()
    {
        lock (_activationLock)
        {
            if (!_commands.HasLiveCommands())
            {
                DeactivateLocked();
            }
        }
    }

    public void Dispose()
    {
        if (!_disposeState.TryBeginDispose())
        {
            return;
        }

        lock (_activationLock)
        {
            DeactivateLocked();
        }
    }

    int ISceneRenderPass.Order
        => 0;

    bool ISceneRenderPass.TryGetRequest(SceneRenderPhase phase, out SceneRenderFeatures features)
    {
        if (!_disposeState.IsDisposing
            && _commands.TryGetLiveRequest(out PrimitiveDrawState state, out bool hasWorldPrimitives))
        {
            if (phase != state.RenderPhase)
            {
                features = SceneRenderFeatures.None;
                return false;
            }

            features = hasWorldPrimitives && state.RequiresSceneDepth
                ? SceneRenderFeatures.SceneDepth
                : SceneRenderFeatures.None;
            return true;
        }

        features = SceneRenderFeatures.None;
        return false;
    }

    void ISceneRenderPass.Draw(SceneRenderPhase phase, in SceneRenderFrame frame)
    {
        if (_disposeState.IsDisposing)
        {
            return;
        }

        try
        {
            if (!_commands.TryCopyLiveTo(_drawCommands, out PrimitiveDrawState drawState))
            {
                return;
            }

            if (drawState.RenderPhase != phase)
            {
                return;
            }

            if (_renderer.Draw(_drawCommands.Lines, _drawCommands.Points, _drawCommands.Screens, drawState, frame)
                && Interlocked.Exchange(ref _loggedDrawSuccess, 1) == 0)
            {
                _logger.LogInformation(
                    "native primitive drawing submitted first callback batch with {LineCount} lines, {PointCount} points, {ScreenCount} screen primitives, {DepthMode} depth mode, and {AntiAliasing} anti aliasing",
                    _drawCommands.LineCount,
                    _drawCommands.PointCount,
                    _drawCommands.ScreenCount,
                    drawState.DepthMode,
                    drawState.AntiAliasing);
            }
        }
        catch (Exception ex)
        {
            if (Interlocked.Exchange(ref _loggedDrawFailure, 1) == 0)
            {
                _logger.LogWarning(ex, "native primitive draw command failed");
            }
        }
    }

    private void DeactivateLocked()
    {
        _registration?.Dispose();
        _registration = null;

        _commands.Clear();
        _drawCommands.Clear();
    }
}
