using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using Intoner.Logging;
using Intoner.Services.Interop;
using Intoner.Utils;
using Microsoft.Extensions.Logging;
using Framework = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework;
using RenderManager = FFXIVClientStructs.FFXIV.Client.Graphics.Render.Manager;

namespace Intoner.Scene.Rendering;

/// <summary> schedules scene phases at the game's native UI render boundaries </summary>
internal sealed unsafe class SceneRenderScheduler : IDisposable
{
    private const int CommandListStride = 0x18;
    private const int CommandListCursorOffset = 0x20;
    private const int MainSceneViewIndex = (int)RenderManager.RenderViews.Main;

    private const uint GameUi3DTargetBindCommandKey = 0xCDFFFFFF;
    private const uint FinalTargetBindCommandKey = 0xE0000000;
    private const uint AfterGameUiTargetBindCommandKey = 0xEC000005;

    private readonly ILogger _logger;
    private readonly IGameInteropProvider _gameInteropProvider;
    private readonly Func<SceneRenderPhase, bool> _hasWork;
    private readonly ExecuteCommand _execute;
    private readonly Lock _lifecycleLock = new();
    private readonly Lock _queueLock = new();
    private readonly DisposalState _disposeState = new();
    private readonly SceneRenderCommandTracker _trackedCommands = new();

    private readonly InteropHookCollection _hooks;
    private Hook<Context.Delegates.SetRenderTargets>? _recordTargetHook;
    private Hook<ImmediateContext.Delegates.DoSetTargetCommand>? _bindTargetHook;
    private bool _startFailed;
    private int _active;
    private int _loggedDetourFailure;
    private int _loggedDispatchFailure;
    private int _loggedQueueFailure;
    private int _loggedTargetLookupFailure;
    private int _loggedTrackingOverflow;
    private (nint Context, SceneRenderCommand Command) _beforeUi3D;

    public delegate void ExecuteCommand(in SceneRenderCommand command);

    public SceneRenderScheduler(
        ILogger logger,
        IGameInteropProvider gameInteropProvider,
        Func<SceneRenderPhase, bool> hasWork,
        ExecuteCommand execute)
    {
        _logger = logger;
        _hooks = new InteropHookCollection(logger);
        _gameInteropProvider = gameInteropProvider;
        _hasWork = hasWork;
        _execute = execute;
    }

    public bool TryStart()
    {
        lock (_lifecycleLock)
        {
            if (_disposeState.IsDisposing || _startFailed)
            {
                return false;
            }

            if (Volatile.Read(ref _active) != 0)
            {
                return true;
            }

            try
            {
                if (_recordTargetHook is null)
                {
                    if (Framework.Addresses.Instance.Value == nint.Zero)
                    {
                        throw new InvalidOperationException("framework address for scene render frame tracking was unavailable");
                    }

                    _recordTargetHook = _hooks.CreateRequired<Context.Delegates.SetRenderTargets>(
                        _gameInteropProvider, IntonerSignatures.SceneRenderSetRenderTargets.Address,
                        SetRenderTargetsDetour, IntonerSignatures.SceneRenderSetRenderTargets.Label);
                    _bindTargetHook = _hooks.CreateRequired<ImmediateContext.Delegates.DoSetTargetCommand>(
                        _gameInteropProvider, IntonerSignatures.SceneRenderSetTargetCommand.Address,
                        SetTargetCommandDetour, IntonerSignatures.SceneRenderSetTargetCommand.Label);
                }

                if (!_hooks.Enable())
                {
                    throw new InvalidOperationException("scene render hooks could not be enabled");
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write(() => _logger.LogWarning(ex, "scene rendering disabled because final target hooks could not be started"));
                try
                {
                    Dispose();
                }
                catch (Exception cleanupException)
                {
                    DiagnosticLog.Write(() => _logger.LogError(cleanupException, "failed to dispose scene render hooks after startup failed"));
                }

                return false;
            }

            Volatile.Write(ref _active, 1);
            DiagnosticLog.Write(() => _logger.LogInformation("scene rendering enabled through native game UI boundaries"));
            return true;
        }
    }

    public void Stop()
    {
        lock (_lifecycleLock)
        {
            if (Interlocked.Exchange(ref _active, 0) == 0)
            {
                return;
            }

            try
            {
                _hooks.Disable();
            }
            catch
            {
                _startFailed = true;
                throw;
            }
            finally
            {
                ClearCommands();
            }
        }
    }

    public void Dispose()
    {
        lock (_lifecycleLock)
        {
            _disposeState.TryBeginDispose();
            Volatile.Write(ref _active, 0);
            try
            {
                _hooks.Dispose();
            }
            finally
            {
                ClearCommands();
            }
        }
    }

    private void ClearCommands()
    {
        lock (_queueLock)
        {
            _beforeUi3D = default;
            _trackedCommands.Clear();
        }
    }

    private void SetRenderTargetsDetour(
        Context* context,
        int targetCount,
        Texture** targetSlots,
        Texture* depthTarget,
        short colorMip,
        short depthMip,
        short colorSlice,
        short depthSlice)
    {
        using InteropHookCollection.CallbackScope callback = _hooks.EnterCallback();
        Context.Delegates.SetRenderTargets original = _recordTargetHook!.OriginalDisposeSafe;

        if (!callback.IsActive || Volatile.Read(ref _active) == 0)
        {
            original(context, targetCount, targetSlots, depthTarget, colorMip, depthMip, colorSlice, depthSlice);
            return;
        }

        uint sortKey = context != null ? context->SortKey : 0;
        uint frameNumber = 0;
        SceneRenderTarget target = default;
        RenderCommandBufferGroup* previousCursor = null;
        try
        {
            if ((sortKey is GameUi3DTargetBindCommandKey or FinalTargetBindCommandKey)
                && context->ViewIndex == MainSceneViewIndex
                && targetCount == 1 && targetSlots != null && depthTarget == null
                && TryGetFrameNumber(out frameNumber)
                && SceneRenderTargets.TryCaptureFinal(*targetSlots, out target))
            {
                previousCursor = GetCommandCursor(context);
            }
        }
        catch (Exception ex)
        {
            LogDetourFailure(ex);
            target = default;
        }

        original(context, targetCount, targetSlots, depthTarget, colorMip, depthMip, colorSlice, depthSlice);
        if (target.Address == nint.Zero)
        {
            return;
        }

        try
        {
            RenderCommand* appended = FindAppendedCommand(context, previousCursor, sortKey);
            nint address = SceneRenderTargets.Matches(target, (RenderCommandSetTarget*)appended) ? (nint)appended : nint.Zero;
            SceneRenderPhase phase = sortKey == GameUi3DTargetBindCommandKey
                ? SceneRenderPhase.BeforeGameUi3D
                : SceneRenderPhase.AfterGameUi3D;
            QueueBoundary(context, original, new SceneRenderCommand(address, frameNumber, phase, target));
        }
        catch (Exception ex)
        {
            LogDetourFailure(ex);
        }
    }

    private void SetTargetCommandDetour(ImmediateContext* context, RenderCommandSetTarget* command)
    {
        using InteropHookCollection.CallbackScope callback = _hooks.EnterCallback();
        _bindTargetHook!.OriginalDisposeSafe(context, command);
        if (!callback.IsActive || Volatile.Read(ref _active) == 0)
        {
            return;
        }

        try
        {
            if (_trackedCommands.TryTake((nint)command, out SceneRenderCommand draw)
                && TryGetFrameNumber(out uint frameNumber)
                && draw.IsFromActiveFrame(frameNumber)
                && SceneRenderTargets.Matches(draw.Target, command))
            {
                _execute(draw);
            }
        }
        catch (Exception ex)
        {
            if (Interlocked.Exchange(ref _loggedDispatchFailure, 1) == 0)
            {
                DiagnosticLog.Write(() => _logger.LogWarning(ex, "scene render phase dispatch failed"));
            }
        }
    }

    private static bool TryGetFrameNumber(out uint frameNumber)
    {
        Framework* framework = Framework.Instance();
        frameNumber = framework != null ? Volatile.Read(ref framework->FrameCounter) : 0;
        return framework != null;
    }

    private void QueueBoundary(Context* context, Context.Delegates.SetRenderTargets original, in SceneRenderCommand boundary)
    {
        lock (_queueLock)
        {
            if (Volatile.Read(ref _active) == 0)
            {
                return;
            }

            bool isBeforeUi3D = boundary.Phase == SceneRenderPhase.BeforeGameUi3D;
            SceneRenderCommand beforeUi3D = !isBeforeUi3D
                && _beforeUi3D.Context == (nint)context
                && _beforeUi3D.Command.FrameNumber == boundary.FrameNumber
                ? _beforeUi3D.Command
                : default;
            _beforeUi3D = default;
            bool drawAtBoundary = _hasWork(boundary.Phase);
            bool drawOverUi = !isBeforeUi3D && _hasWork(SceneRenderPhase.AfterGameUi);
            if (boundary.Address == nint.Zero)
            {
                if (drawAtBoundary || drawOverUi || (!isBeforeUi3D && beforeUi3D.Address != nint.Zero))
                {
                    LogMissingTargetCommand(isBeforeUi3D ? "game UI" : "final");
                }
                return;
            }

            if (beforeUi3D.Address != nint.Zero)
            {
                TrackCommand(beforeUi3D);
            }

            if (!drawAtBoundary && !drawOverUi)
            {
                return;
            }

            SceneRenderCommand draw = boundary with { Projection = SceneProjection.Capture(boundary.Target.Viewport) };
            if (isBeforeUi3D)
            {
                _beforeUi3D = ((nint)context, draw);
                return;
            }

            if (drawAtBoundary)
            {
                TrackCommand(draw);
            }

            if (drawOverUi)
            {
                QueueAfterGameUi(context, original, draw);
            }
        }
    }

    private void QueueAfterGameUi(Context* context, Context.Delegates.SetRenderTargets original, in SceneRenderCommand draw)
    {
        uint sortKey = context->SortKey;
        int viewIndex = context->ViewIndex;
        try
        {
            context->ViewIndex = MainSceneViewIndex;
            RenderCommandBufferGroup* previousCursor = GetCommandCursor(context);
            context->SortKey = AfterGameUiTargetBindCommandKey;
            Texture* colorTarget = (Texture*)draw.Target.Address;
            original(context, 1, &colorTarget, null, 0, 0, 0, 0);
            RenderCommand* bind = FindAppendedCommand(context, previousCursor, AfterGameUiTargetBindCommandKey);
            if (SceneRenderTargets.Matches(draw.Target, (RenderCommandSetTarget*)bind))
            {
                TrackCommand(draw with { Address = (nint)bind, Phase = SceneRenderPhase.AfterGameUi });
            }
            else
            {
                LogMissingTargetCommand("after game UI");
            }
        }
        catch (Exception ex)
        {
            if (Interlocked.Exchange(ref _loggedQueueFailure, 1) == 0)
            {
                DiagnosticLog.Write(() => _logger.LogWarning(ex, "scene render command queue failed"));
            }
        }
        finally
        {
            context->SortKey = sortKey;
            context->ViewIndex = viewIndex;
        }
    }

    private static RenderCommandBufferGroup* GetCommandCursor(Context* context)
        => *(RenderCommandBufferGroup**)((byte*)context + MainSceneViewIndex * CommandListStride + CommandListCursorOffset);

    private static RenderCommand* FindAppendedCommand(Context* context, RenderCommandBufferGroup* previousCursor, uint sortKey)
    {
        RenderCommandBufferGroup* cursor = GetCommandCursor(context);
        if (cursor == null || cursor == previousCursor)
        {
            return null;
        }

        RenderCommandBufferGroup* record = cursor - 1;
        return *(uint*)record == sortKey ? record->Command : null;
    }

    private void TrackCommand(in SceneRenderCommand command)
    {
        if (_trackedCommands.Track(command)
            && Interlocked.Exchange(ref _loggedTrackingOverflow, 1) == 0)
        {
            DiagnosticLog.Write(() => _logger.LogWarning("scene render command tracking reset after its pending command limit was exceeded"));
        }
    }

    private void LogMissingTargetCommand(string boundary)
    {
        if (Interlocked.Exchange(ref _loggedTargetLookupFailure, 1) == 0)
        {
            DiagnosticLog.Write(() => _logger.LogWarning("scene render {Boundary} target command could not be identified", boundary));
        }
    }

    private void LogDetourFailure(Exception exception)
    {
        if (Interlocked.Exchange(ref _loggedDetourFailure, 1) == 0)
        {
            DiagnosticLog.Write(() => _logger.LogWarning(exception, "scene render target detour failed"));
        }
    }
}
