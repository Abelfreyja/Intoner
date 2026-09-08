using Dalamud.Plugin.Services;
using Intoner.Utils;
using Microsoft.Extensions.Logging;

namespace Intoner.Scene.Rendering;

internal sealed class SceneRenderService : IDisposable
{
    private readonly ILogger<SceneRenderService> _logger;
    private readonly SceneRenderScheduler _scheduler;
    private readonly Lock _stateLock = new();
    private readonly DisposalState _disposeState = new();
    private readonly SceneDepthViewCache _sceneDepth = new();
    private readonly List<ISceneRenderPass> _passes = [];
    private readonly List<ISceneRenderPass> _activePasses = [];
    private readonly HashSet<Type> _loggedPassFailures = [];

    private bool _schedulerStopInProgress;
    private int _loggedFrameFailure;
    private int _loggedCallbackFailure;

    public SceneRenderService(
        ILogger<SceneRenderService> logger,
        IGameInteropProvider gameInteropProvider)
    {
        _logger = logger;
        _scheduler = new SceneRenderScheduler(
            logger,
            gameInteropProvider,
            HasPhaseWork,
            ExecuteDrawCommand);
    }

    public Registration? TryRegister(ISceneRenderPass pass)
    {
        ArgumentNullException.ThrowIfNull(pass);
        lock (_stateLock)
        {
            if (_disposeState.IsDisposing
                || _schedulerStopInProgress
                || ContainsPass(pass))
            {
                return null;
            }

            InsertPass(pass);
            bool keepPass = false;
            try
            {
                if (!_scheduler.TryStart())
                {
                    return null;
                }

                keepPass = true;
                return new Registration(this, pass);
            }
            finally
            {
                if (!keepPass)
                {
                    RemovePass(pass);
                }
            }
        }
    }

    public void Dispose()
    {
        if (!_disposeState.TryBeginDispose())
        {
            return;
        }

        lock (_stateLock)
        {
            _passes.Clear();
            _activePasses.Clear();
        }

        try
        {
            _scheduler.Dispose();
        }
        finally
        {
            lock (_stateLock)
            {
                _sceneDepth.Dispose();
            }
        }
    }

    private void Unregister(ISceneRenderPass pass)
    {
        bool stopScheduler;
        lock (_stateLock)
        {
            RemovePass(pass);
            stopScheduler = _passes.Count == 0;
            _schedulerStopInProgress = stopScheduler;
        }

        if (!stopScheduler)
        {
            return;
        }

        try
        {
            _scheduler.Stop();
        }
        finally
        {
            lock (_stateLock)
            {
                _schedulerStopInProgress = false;
                if (_passes.Count == 0)
                {
                    _sceneDepth.Dispose();
                }
            }
        }
    }

    private bool HasPhaseWork(SceneRenderPhase phase)
    {
        if (_disposeState.IsDisposing)
        {
            return false;
        }

        lock (_stateLock)
        {
            if (_disposeState.IsDisposing)
            {
                return false;
            }

            foreach (ISceneRenderPass pass in _passes)
            {
                if (TryResolveRequest(pass, phase, out _))
                {
                    return true;
                }
            }

            return false;
        }
    }

    private void ExecuteDrawCommand(SceneRenderPhase phase)
    {
        try
        {
            ExecuteDrawCommandUnsafe(phase);
        }
        catch (Exception ex)
        {
            if (Interlocked.Exchange(ref _loggedCallbackFailure, 1) == 0)
            {
                _logger.LogWarning(ex, "scene render callback failed");
            }
        }
    }

    private void ExecuteDrawCommandUnsafe(SceneRenderPhase phase)
    {
        if (_disposeState.IsDisposing)
        {
            return;
        }

        lock (_stateLock)
        {
            if (_disposeState.IsDisposing || _passes.Count == 0)
            {
                return;
            }

            _activePasses.Clear();
            try
            {
                SceneRenderFeatures features = SceneRenderFeatures.None;
                foreach (ISceneRenderPass pass in _passes)
                {
                    if (TryResolveRequest(pass, phase, out SceneRenderFeatures requestedFeatures))
                    {
                        _activePasses.Add(pass);
                        features |= requestedFeatures;
                    }
                }

                if (_activePasses.Count == 0)
                {
                    return;
                }

                if (!SceneRenderFrame.TryCapture(
                        _sceneDepth,
                        features,
                        out SceneRenderFrame frame))
                {
                    if (Interlocked.Exchange(ref _loggedFrameFailure, 1) == 0)
                    {
                        _logger.LogWarning("scene render callback skipped because the final scene target was unavailable");
                    }

                    return;
                }

                _loggedFrameFailure = 0;
                foreach (ISceneRenderPass pass in _activePasses)
                {
                    try
                    {
                        pass.Draw(phase, frame);
                    }
                    catch (Exception ex)
                    {
                        LogPassFailure(pass, ex);
                    }
                }
            }
            finally
            {
                _activePasses.Clear();
            }
        }
    }

    private bool TryResolveRequest(
        ISceneRenderPass pass,
        SceneRenderPhase phase,
        out SceneRenderFeatures features)
    {
        try
        {
            return pass.TryGetRequest(phase, out features);
        }
        catch (Exception ex)
        {
            features = SceneRenderFeatures.None;
            LogPassFailure(pass, ex);
            return false;
        }
    }

    private void LogPassFailure(ISceneRenderPass pass, Exception exception)
    {
        Type passType = pass.GetType();
        if (_loggedPassFailures.Add(passType))
        {
            _logger.LogWarning(exception, "scene render pass {PassType} failed", passType.Name);
        }
    }

    private bool ContainsPass(ISceneRenderPass pass)
        => _passes.Exists(candidate => ReferenceEquals(candidate, pass));

    private void InsertPass(ISceneRenderPass pass)
    {
        int index = _passes.Count;
        while (index > 0 && _passes[index - 1].Order > pass.Order)
        {
            --index;
        }

        _passes.Insert(index, pass);
    }

    private void RemovePass(ISceneRenderPass pass)
    {
        for (int index = 0; index < _passes.Count; ++index)
        {
            if (ReferenceEquals(_passes[index], pass))
            {
                _passes.RemoveAt(index);
                return;
            }
        }
    }

    internal sealed class Registration : IDisposable
    {
        private SceneRenderService? _owner;
        private readonly ISceneRenderPass _pass;

        public Registration(SceneRenderService owner, ISceneRenderPass pass)
        {
            _owner = owner;
            _pass = pass;
        }

        public void Dispose()
            => Interlocked.Exchange(ref _owner, null)?.Unregister(_pass);
    }
}
