using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.STD;
using Intoner.Logging;
using Intoner.Services.Interop;
using Intoner.Utils;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Context = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Context;
using ImmediateContext = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.ImmediateContext;
using RenderCommandBufferGroup = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.RenderCommandBufferGroup;
using RenderCommandCallback = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.RenderCommandCallback;
using RenderCommandSetTarget = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.RenderCommandSetTarget;
using RenderCommandType = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.RenderCommandType;
using RenderManager = FFXIVClientStructs.FFXIV.Client.Graphics.Render.Manager;
using Texture = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Texture;

namespace Intoner.Scene.Rendering;

/// <summary> schedules scene phases at the game's native UI render boundaries </summary>
internal sealed unsafe class SceneRenderScheduler : IDisposable
{
    private const int ContextCommandListsOffset = 0x18;
    private const int MainSceneViewIndex = (int)RenderManager.RenderViews.Main;

    private const uint GameUi3DTargetBindCommandKey = 0xCDFFFFFF;
    private const uint FinalTargetBindCommandKey = 0xE0000000;
    private const uint AfterGameUiTargetBindCommandKey = 0xEC000004;
    private const uint AfterGameUiCallbackCommandKey = 0xEC000005;

    private static readonly ConcurrentDictionary<nint, SceneRenderScheduler> Callbacks = new();
    private static long NextCallbackId;

    private readonly ILogger _logger;
    private readonly IGameInteropProvider _gameInteropProvider;
    private readonly Func<SceneRenderPhase, bool> _hasWork;
    private readonly Action<SceneRenderCommand> _execute;
    private readonly Lock _lifecycleLock = new();
    private readonly Lock _queueLock = new();
    private readonly DisposalState _disposeState = new();
    private readonly SceneRenderCommandTracker _trackedCommands = new();

    private InteropHookCollection? _hooks;
    private Context.Delegates.SetRenderTargets? _setRenderTargetsOriginal;
    private bool _startFailed;
    private int _active;
    private int _loggedDetourFailure;
    private int _loggedDispatchFailure;
    private int _loggedQueueFailure;
    private int _loggedTargetLookupFailure;
    private int _loggedTrackingOverflow;
    private nint _callbackId;
    private TargetCommand _pendingBeforeGameUi3DCommand;

    public SceneRenderScheduler(
        ILogger logger,
        IGameInteropProvider gameInteropProvider,
        Func<SceneRenderPhase, bool> hasWork,
        Action<SceneRenderCommand> execute)
    {
        _logger = logger;
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

            if (IntonerSignatures.SceneRenderAllocateCommand.Address == nint.Zero
                || IntonerSignatures.SceneRenderPushBackCommand.Address == nint.Zero)
            {
                _startFailed = true;
                DiagnosticLog.Write(() => _logger.LogWarning("scene rendering disabled because required render command functions were unavailable"));
                return false;
            }

            InteropHookCollection hooks = new(_logger);
            _hooks = hooks;
            Hook<Context.Delegates.SetRenderTargets>? setRenderTargetsHook = null;
            Hook<ImmediateContext.Delegates.DoSetTargetCommand>? setTargetCommandHook = null;
            setRenderTargetsHook = hooks.Add(InteropHookUtility.CreateHookFromAddress<Context.Delegates.SetRenderTargets>(
                _logger, _gameInteropProvider, IntonerSignatures.SceneRenderSetRenderTargets,
                (context, count, slots, depth, left, top, right, bottom) =>
                    SetRenderTargetsDetour(hooks, setRenderTargetsHook!, context, count, slots, depth, left, top, right, bottom)));
            setTargetCommandHook = hooks.Add(InteropHookUtility.CreateHookFromAddress<ImmediateContext.Delegates.DoSetTargetCommand>(
                _logger, _gameInteropProvider, IntonerSignatures.SceneRenderSetTargetCommand,
                (context, command) => SetTargetCommandDetour(hooks, setTargetCommandHook!, context, command)));

            if (setRenderTargetsHook is null || setTargetCommandHook is null)
            {
                Stop();
                _startFailed = true;
                DiagnosticLog.Write(() => _logger.LogWarning("scene rendering disabled because required final target hooks were unavailable"));
                return false;
            }

            _setRenderTargetsOriginal = (context, count, slots, depth, left, top, right, bottom)
                => setRenderTargetsHook.OriginalDisposeSafe(context, count, slots, depth, left, top, right, bottom);
            _callbackId = RegisterCallback();
            try
            {
                hooks.Enable();
            }
            catch (Exception ex)
            {
                Stop();
                _startFailed = true;
                DiagnosticLog.Write(() => _logger.LogWarning(ex, "scene rendering disabled because final target hooks could not be enabled"));
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
            Volatile.Write(ref _active, 0);
            nint callbackId = _callbackId;
            _callbackId = nint.Zero;

            if (callbackId != nint.Zero)
            {
                Callbacks.TryRemove(callbackId, out _);
            }

            try
            {
                _hooks?.Dispose();
            }
            catch
            {
                _startFailed = true;
                throw;
            }

            _hooks = null;
            _setRenderTargetsOriginal = null;
            lock (_queueLock)
            {
                _pendingBeforeGameUi3DCommand = default;
            }

            _trackedCommands.Clear();
        }
    }

    public void Dispose()
    {
        _disposeState.TryBeginDispose();
        Stop();
    }

    private nint RegisterCallback()
    {
        nint callbackId = (nint)Interlocked.Increment(ref NextCallbackId);
        Callbacks[callbackId] = this;
        return callbackId;
    }

    private void SetRenderTargetsDetour(
        InteropHookCollection hooks,
        Hook<Context.Delegates.SetRenderTargets> hook,
        Context* context,
        int targetCount,
        Texture** targetSlots,
        Texture* depthTarget,
        short left,
        short top,
        short right,
        short bottom)
    {
        using InteropHookCollection.CallbackScope callback = hooks.EnterCallback();
        Context.Delegates.SetRenderTargets original = hook.OriginalDisposeSafe;

        if (!callback.IsActive || !ReferenceEquals(_hooks, hooks) || Volatile.Read(ref _active) == 0)
        {
            original(context, targetCount, targetSlots, depthTarget, left, top, right, bottom);
            return;
        }

        uint commandKey = 0;
        bool isRenderBoundary = false;
        RenderCommandBufferGroup* previousWritePointer = null;
        try
        {
            commandKey = context != null ? context->SortKey : 0;
            isRenderBoundary = IsRenderBoundary(commandKey)
                               && IsMainSceneFinalTargetBind(context, targetCount, targetSlots, depthTarget);
            if (isRenderBoundary)
            {
                previousWritePointer = GetCommandWritePointer(context, MainSceneViewIndex);
            }
        }
        catch (Exception ex)
        {
            LogDetourFailure(ex);
        }

        original(context, targetCount, targetSlots, depthTarget, left, top, right, bottom);
        if (!isRenderBoundary)
        {
            return;
        }

        try
        {
            TargetCommand targetCommand = GetAppendedFinalTargetCommand(
                context,
                previousWritePointer,
                commandKey);
            QueueActivePassCommands(context, targetSlots, commandKey, targetCommand);
        }
        catch (Exception ex)
        {
            LogDetourFailure(ex);
        }
    }

    private void SetTargetCommandDetour(InteropHookCollection hooks,
        Hook<ImmediateContext.Delegates.DoSetTargetCommand> hook, ImmediateContext* immediateContext, RenderCommandSetTarget* command)
    {
        using InteropHookCollection.CallbackScope callback = hooks.EnterCallback();
        ImmediateContext.Delegates.DoSetTargetCommand original = hook.OriginalDisposeSafe;

        bool execute = false;
        SceneRenderCommand frame = default;
        try
        {
            execute = callback.IsActive && ReferenceEquals(_hooks, hooks)
                      && Volatile.Read(ref _active) != 0
                      && _trackedCommands.TryTake((nint)command, out frame)
                      && IsFinalTargetCommand(command);
        }
        catch (Exception ex)
        {
            LogDispatchFailure(ex);
        }

        original(immediateContext, command);
        if (execute)
        {
            Execute(frame);
        }
    }

    private void QueueActivePassCommands(
        Context* context,
        Texture** finalTargetSlots,
        uint commandKey,
        TargetCommand targetCommand)
    {
        lock (_queueLock)
        {
            if (Volatile.Read(ref _active) == 0 || _setRenderTargetsOriginal == null)
            {
                return;
            }

            if (commandKey == GameUi3DTargetBindCommandKey)
            {
                RemoveTrackedCommand(targetCommand);
                _pendingBeforeGameUi3DCommand = default;
                if (!_hasWork(SceneRenderPhase.BeforeGameUi3D))
                {
                    return;
                }

                if (!targetCommand.IsValid)
                {
                    LogMissingTargetCommand("game UI");
                    return;
                }

                _pendingBeforeGameUi3DCommand = targetCommand;
                return;
            }

            if (commandKey != FinalTargetBindCommandKey)
            {
                return;
            }

            RemoveTrackedCommand(targetCommand);
            TargetCommand beforeGameUi3D = _pendingBeforeGameUi3DCommand;
            _pendingBeforeGameUi3DCommand = default;
            if (beforeGameUi3D.IsValid && targetCommand.IsValid)
            {
                TrackCommand(beforeGameUi3D, SceneRenderPhase.BeforeGameUi3D);
            }

            if (_hasWork(SceneRenderPhase.AfterGameUi3D))
            {
                if (!targetCommand.IsValid)
                {
                    LogMissingTargetCommand("final");
                }
                else
                {
                    TrackCommand(targetCommand, SceneRenderPhase.AfterGameUi3D);
                }
            }

            if (_hasWork(SceneRenderPhase.AfterGameUi))
            {
                QueueAfterGameUiCommand(context, finalTargetSlots, targetCommand.Projection);
            }
        }
    }

    private void QueueAfterGameUiCommand(Context* context, Texture** finalTargetSlots, SceneProjection? projection)
    {
        Context.Delegates.SetRenderTargets original = _setRenderTargetsOriginal!;
        nint callbackId = Volatile.Read(ref _callbackId);
        if (callbackId == nint.Zero)
        {
            return;
        }

        ContextState contextState = new(context->SortKey, context->ViewIndex);
        try
        {
            context->ViewIndex = MainSceneViewIndex;
            context->SortKey = AfterGameUiTargetBindCommandKey;
            original(context, 1, finalTargetSlots, null, 0, 0, 0, 0);

            context->SortKey = AfterGameUiCallbackCommandKey;
            var command = (SceneCallbackCommand*)context->AllocateCommand((ulong)sizeof(SceneCallbackCommand));
            if (command == null)
            {
                return;
            }

            command->Header.Type = RenderCommandType.Callback;
            command->Header.Function = &DrawCommandCallback;
            command->Header.Argument = command;
            command->Header.Flags = 0;
            command->CallbackId = callbackId;
            TrackCommand(new TargetCommand((nint)command, projection), SceneRenderPhase.AfterGameUi);
            context->PushBackCommand(command);
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
            context->SortKey = contextState.SortKey;
            context->ViewIndex = contextState.ViewIndex;
        }
    }

    private static bool IsMainSceneFinalTargetBind(
        Context* context,
        int targetCount,
        Texture** targetSlots,
        Texture* depthTarget)
        => context != null
           && targetCount == 1
           && targetSlots != null
           && depthTarget == null
           && context->ViewIndex == MainSceneViewIndex
           && SceneRenderTarget.IsFinalTargetBind((nint)targetSlots);

    private static bool IsRenderBoundary(uint commandKey)
        => commandKey is GameUi3DTargetBindCommandKey or FinalTargetBindCommandKey;

    // clientstructs does not expose any per view command list vectors for 'Context' and I'm afraid to try to PR it lol
    private static RenderCommandBufferGroup* GetCommandWritePointer(Context* context, int viewIndex)
    {
        var commandLists = (StdVector<RenderCommandBufferGroup>*)((byte*)context + ContextCommandListsOffset);
        return commandLists[viewIndex].Last;
    }

    private static TargetCommand GetAppendedFinalTargetCommand(
        Context* context,
        RenderCommandBufferGroup* previousWritePointer,
        uint expectedCommandKey)
    {
        RenderCommandBufferGroup* writePointer = GetCommandWritePointer(context, MainSceneViewIndex);
        if (writePointer == null || writePointer == previousWritePointer)
        {
            return default;
        }

        RenderCommandBufferGroup* commandRecord = writePointer - 1;
        // the first private group field is the sort key stamped by PushBackCommand
        if (*(uint*)commandRecord != expectedCommandKey)
        {
            return default;
        }

        RenderCommandSetTarget* command = commandRecord->SetTargetCommand;
        if (!IsFinalTargetCommand(command))
        {
            return default;
        }

        // capture on the producer thread before the next framework camera update
        SceneProjection? projection = SceneProjection.TryCapture(out SceneProjection captured) ? captured : null;
        return new TargetCommand((nint)command, projection);
    }

    private static bool IsFinalTargetCommand(RenderCommandSetTarget* command)
        => command != null
           && *(RenderCommandType*)command == RenderCommandType.SetTarget
           && command->RenderTargetCount == 1
           && command->DepthBuffer == null
           && SceneRenderTarget.IsFinalTarget(command->RenderTargets[0].Value);

    private void RemoveTrackedCommand(TargetCommand targetCommand)
    {
        if (!targetCommand.IsValid)
        {
            return;
        }

        _trackedCommands.Remove(targetCommand.Command);
    }

    private void TrackCommand(TargetCommand targetCommand, SceneRenderPhase phase)
    {
        if (_trackedCommands.Track(targetCommand.Command, new SceneRenderCommand(phase, targetCommand.Projection))
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

    private void LogDispatchFailure(Exception exception)
    {
        if (Interlocked.Exchange(ref _loggedDispatchFailure, 1) == 0)
        {
            DiagnosticLog.Write(() => _logger.LogWarning(exception, "scene render phase dispatch failed"));
        }
    }

    private void Execute(SceneRenderCommand command)
    {
        if (Volatile.Read(ref _active) == 0)
        {
            return;
        }

        try
        {
            _execute(command);
        }
        catch (Exception ex)
        {
            LogDispatchFailure(ex);
        }
    }

    [UnmanagedCallersOnly]
    private static void DrawCommandCallback(void* context)
    {
        SceneCallbackCommand* command = (SceneCallbackCommand*)context;
        if (!Callbacks.TryGetValue(command->CallbackId, out SceneRenderScheduler? scheduler))
        {
            return;
        }

        InteropHookCollection? hooks = scheduler._hooks;

        if (hooks is null)
        {
            return;
        }

        using InteropHookCollection.CallbackScope callback = hooks.EnterCallback();
        try
        {
            if (callback.IsActive && ReferenceEquals(scheduler._hooks, hooks)
                && command->CallbackId == Volatile.Read(ref scheduler._callbackId)
                && scheduler._trackedCommands.TryTake((nint)context, out SceneRenderCommand frame))
            {
                scheduler.Execute(frame);
            }
        }
        catch (Exception ex)
        {
            scheduler.LogDispatchFailure(ex);
        }
    }

    [StructLayout(LayoutKind.Auto)]
    private readonly record struct ContextState(uint SortKey, int ViewIndex);

    [StructLayout(LayoutKind.Auto)]
    private readonly record struct TargetCommand(nint Command, SceneProjection? Projection)
    {
        public bool IsValid
            => Command != nint.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SceneCallbackCommand
    {
        public RenderCommandCallback Header;
        public nint CallbackId;
    }
}
