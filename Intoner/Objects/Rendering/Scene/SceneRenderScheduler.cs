using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.STD;
using Intoner.Services.Interop;
using Intoner.Utils;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Context = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Context;
using RenderCommandBufferGroup = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.RenderCommandBufferGroup;
using RenderCommandSetTarget = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.RenderCommandSetTarget;
using RenderCommandType = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.RenderCommandType;
using RenderManager = FFXIVClientStructs.FFXIV.Client.Graphics.Render.Manager;

namespace Intoner.Scene.Rendering;

/// <summary> schedules scene phases at the game's native UI render boundaries </summary>
internal sealed unsafe class SceneRenderScheduler : IDisposable
{
    private const int DrawCallbackCommandType = 0x0E;
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
    private readonly Action<SceneRenderPhase> _execute;
    private readonly Lock _lifecycleLock = new();
    private readonly Lock _queueLock = new();
    private readonly DisposalState _disposeState = new();
    private readonly SceneRenderCommandTracker _trackedCommands = new();

    private Hook<ContextSetRenderTargetsDelegate>? _setRenderTargetsHook;
    private Hook<ImmediateContextSetTargetCommandDelegate>? _setTargetCommandHook;
    private ContextSetRenderTargetsDelegate? _setRenderTargetsOriginal;
    private ImmediateContextSetTargetCommandDelegate? _setTargetCommandOriginal;
    private bool _startFailed;
    private int _active;
    private int _loggedDetourFailure;
    private int _loggedDispatchFailure;
    private int _loggedQueueFailure;
    private int _loggedStopFailure;
    private int _loggedTargetLookupFailure;
    private int _loggedTrackingOverflow;
    private nint _callbackId;
    private TargetCommand _pendingBeforeGameUi3DCommand;

    public SceneRenderScheduler(
        ILogger logger,
        IGameInteropProvider gameInteropProvider,
        Func<SceneRenderPhase, bool> hasWork,
        Action<SceneRenderPhase> execute)
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
                _logger.LogWarning("scene rendering disabled because required render command functions were unavailable");
                return false;
            }

            Hook<ContextSetRenderTargetsDelegate>? setRenderTargetsHook =
                InteropHookUtility.CreateHookFromAddress<ContextSetRenderTargetsDelegate>(
                    _logger,
                    _gameInteropProvider,
                    IntonerSignatures.SceneRenderSetRenderTargets,
                    SetRenderTargetsDetour);
            Hook<ImmediateContextSetTargetCommandDelegate>? setTargetCommandHook =
                InteropHookUtility.CreateHookFromAddress<ImmediateContextSetTargetCommandDelegate>(
                    _logger,
                    _gameInteropProvider,
                    IntonerSignatures.SceneRenderSetTargetCommand,
                    SetTargetCommandDetour);
            ContextSetRenderTargetsDelegate? setRenderTargetsOriginal = setRenderTargetsHook?.Original;
            ImmediateContextSetTargetCommandDelegate? setTargetCommandOriginal = setTargetCommandHook?.Original;
            if (setRenderTargetsHook == null || setTargetCommandHook == null)
            {
                CompleteHookRelease(
                    setRenderTargetsHook,
                    setRenderTargetsOriginal,
                    TryDisposeHook(setRenderTargetsHook),
                    ref _setRenderTargetsHook,
                    ref _setRenderTargetsOriginal);
                CompleteHookRelease(
                    setTargetCommandHook,
                    setTargetCommandOriginal,
                    TryDisposeHook(setTargetCommandHook),
                    ref _setTargetCommandHook,
                    ref _setTargetCommandOriginal);
                _startFailed = true;
                _logger.LogWarning("scene rendering disabled because required final target hooks were unavailable");
                return false;
            }

            nint callbackId = RegisterCallback();
            _setRenderTargetsHook = setRenderTargetsHook;
            _setTargetCommandHook = setTargetCommandHook;
            _setRenderTargetsOriginal = setRenderTargetsOriginal;
            _setTargetCommandOriginal = setTargetCommandOriginal;
            _callbackId = callbackId;
            try
            {
                setTargetCommandHook.Enable();
                setRenderTargetsHook.Enable();
            }
            catch (Exception ex)
            {
                Callbacks.TryRemove(callbackId, out _);
                _callbackId = nint.Zero;
                CompleteHookRelease(
                    setRenderTargetsHook,
                    setRenderTargetsOriginal,
                    TryDisposeHook(setRenderTargetsHook),
                    ref _setRenderTargetsHook,
                    ref _setRenderTargetsOriginal);
                CompleteHookRelease(
                    setTargetCommandHook,
                    setTargetCommandOriginal,
                    TryDisposeHook(setTargetCommandHook),
                    ref _setTargetCommandHook,
                    ref _setTargetCommandOriginal);
                _startFailed = true;
                _logger.LogWarning(ex, "scene rendering disabled because final target hooks could not be enabled");
                return false;
            }

            Volatile.Write(ref _active, 1);
            _logger.LogInformation("scene rendering enabled through native game UI boundaries");
            return true;
        }
    }

    public void Stop()
    {
        lock (_lifecycleLock)
        {
            Volatile.Write(ref _active, 0);
            DetachedHooks detached = DetachHooksLocked();

            if (detached.CallbackId != nint.Zero)
            {
                Callbacks.TryRemove(detached.CallbackId, out _);
            }

            bool setRenderTargetsDisposed = TryDisposeHook(detached.SetRenderTargetsHook);
            bool setTargetCommandDisposed = TryDisposeHook(detached.SetTargetCommandHook);
            lock (_queueLock)
            {
                _pendingBeforeGameUi3DCommand = default;
            }

            _trackedCommands.Clear();
            CompleteHookRelease(
                detached.SetRenderTargetsHook,
                detached.SetRenderTargetsOriginal,
                setRenderTargetsDisposed,
                ref _setRenderTargetsHook,
                ref _setRenderTargetsOriginal);
            CompleteHookRelease(
                detached.SetTargetCommandHook,
                detached.SetTargetCommandOriginal,
                setTargetCommandDisposed,
                ref _setTargetCommandHook,
                ref _setTargetCommandOriginal);
        }
    }

    public void Dispose()
    {
        if (_disposeState.TryBeginDispose())
        {
            Stop();
        }
    }

    private nint RegisterCallback()
    {
        nint callbackId = (nint)Interlocked.Increment(ref NextCallbackId);
        Callbacks[callbackId] = this;
        return callbackId;
    }

    private void SetRenderTargetsDetour(
        nint context,
        int targetCount,
        nint targetSlots,
        nint depthTarget,
        short left,
        short top,
        short right,
        short bottom)
    {
        ContextSetRenderTargetsDelegate? original = _setRenderTargetsOriginal;
        if (original == null)
        {
            return;
        }

        uint commandKey = 0;
        bool isRenderBoundary = false;
        RenderCommandBufferGroup* previousWritePointer = null;
        try
        {
            commandKey = context != nint.Zero ? ((Context*)context)->SortKey : 0;
            isRenderBoundary = Volatile.Read(ref _active) != 0
                               && IsRenderBoundary(commandKey)
                               && IsMainSceneFinalTargetBind(context, targetCount, targetSlots, depthTarget);
            if (isRenderBoundary)
            {
                previousWritePointer = GetCommandWritePointer((Context*)context, MainSceneViewIndex);
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
                (Context*)context,
                previousWritePointer,
                commandKey);
            QueueActivePassCommands((Context*)context, targetSlots, commandKey, targetCommand);
        }
        catch (Exception ex)
        {
            LogDetourFailure(ex);
        }
    }

    private void SetTargetCommandDetour(nint immediateContext, RenderCommandSetTarget* command)
    {
        ImmediateContextSetTargetCommandDelegate? original = _setTargetCommandOriginal;
        if (original == null)
        {
            return;
        }

        bool execute = false;
        SceneRenderPhase phase = default;
        try
        {
            execute = Volatile.Read(ref _active) != 0
                      && _trackedCommands.TryTake((nint)command, out phase)
                      && IsFinalTargetCommand(command);
        }
        catch (Exception ex)
        {
            LogDispatchFailure(ex);
        }

        original(immediateContext, command);
        if (execute)
        {
            Execute(phase);
        }
    }

    private void QueueActivePassCommands(
        Context* context,
        nint finalTargetSlots,
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
                QueueAfterGameUiCommand(context, finalTargetSlots);
            }
        }
    }

    private void QueueAfterGameUiCommand(Context* context, nint finalTargetSlots)
    {
        ContextSetRenderTargetsDelegate original = _setRenderTargetsOriginal!;
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
            original((nint)context, 1, finalTargetSlots, nint.Zero, 0, 0, 0, 0);

            context->SortKey = AfterGameUiCallbackCommandKey;
            var command = (NativeCallbackCommand*)context->AllocateCommand((ulong)sizeof(NativeCallbackCommand));
            if (command == null)
            {
                return;
            }

            command->SwitchType = DrawCallbackCommandType;
            command->Callback = &DrawCommandCallback;
            command->Context = callbackId;
            command->Flags = 0;
            context->PushBackCommand(command);
        }
        catch (Exception ex)
        {
            if (Interlocked.Exchange(ref _loggedQueueFailure, 1) == 0)
            {
                _logger.LogWarning(ex, "scene render command queue failed");
            }
        }
        finally
        {
            context->SortKey = contextState.SortKey;
            context->ViewIndex = contextState.ViewIndex;
        }
    }

    private static bool IsMainSceneFinalTargetBind(
        nint context,
        int targetCount,
        nint targetSlots,
        nint depthTarget)
        => context != nint.Zero
           && targetCount == 1
           && targetSlots != nint.Zero
           && depthTarget == nint.Zero
           && ((Context*)context)->ViewIndex == MainSceneViewIndex
           && SceneRenderTarget.IsFinalTargetBind(targetSlots);

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
        return IsFinalTargetCommand(command)
            ? new TargetCommand((nint)command)
            : default;
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
        if (_trackedCommands.Track(targetCommand.Command, phase)
            && Interlocked.Exchange(ref _loggedTrackingOverflow, 1) == 0)
        {
            _logger.LogWarning("scene render command tracking reset after its pending command limit was exceeded");
        }
    }

    private void LogMissingTargetCommand(string boundary)
    {
        if (Interlocked.Exchange(ref _loggedTargetLookupFailure, 1) == 0)
        {
            _logger.LogWarning("scene render {Boundary} target command could not be identified", boundary);
        }
    }

    private void LogDetourFailure(Exception exception)
    {
        if (Interlocked.Exchange(ref _loggedDetourFailure, 1) == 0)
        {
            _logger.LogWarning(exception, "scene render target detour failed");
        }
    }

    private void LogDispatchFailure(Exception exception)
    {
        if (Interlocked.Exchange(ref _loggedDispatchFailure, 1) == 0)
        {
            _logger.LogWarning(exception, "scene render phase dispatch failed");
        }
    }

    private void Execute(SceneRenderPhase phase)
    {
        if (Volatile.Read(ref _active) == 0)
        {
            return;
        }

        try
        {
            _execute(phase);
        }
        catch (Exception ex)
        {
            LogDispatchFailure(ex);
        }
    }

    private DetachedHooks DetachHooksLocked()
    {
        var detached = new DetachedHooks(
            _setRenderTargetsHook,
            _setTargetCommandHook,
            _setRenderTargetsHook != null ? _setRenderTargetsOriginal : null,
            _setTargetCommandHook != null ? _setTargetCommandOriginal : null,
            _callbackId);
        _setRenderTargetsHook = null;
        _setTargetCommandHook = null;
        _callbackId = nint.Zero;
        return detached;
    }

    private bool TryDisposeHook<TDelegate>(Hook<TDelegate>? hook)
        where TDelegate : Delegate
    {
        try
        {
            InteropHookUtility.DisposeHook(hook);
            return true;
        }
        catch (Exception ex)
        {
            if (Interlocked.Exchange(ref _loggedStopFailure, 1) == 0)
            {
                _logger.LogWarning(ex, "scene render hook disposal failed");
            }

            return false;
        }
    }

    private void CompleteHookRelease<TDelegate>(
        Hook<TDelegate>? hook,
        TDelegate? original,
        bool disposed,
        ref Hook<TDelegate>? currentHook,
        ref TDelegate? currentOriginal)
        where TDelegate : Delegate
    {
        if (disposed)
        {
            if (ReferenceEquals(currentHook, hook))
            {
                currentHook = null;
            }

            if (ReferenceEquals(currentOriginal, original))
            {
                currentOriginal = null;
            }

            return;
        }

        if (hook != null)
        {
            currentHook ??= hook;
            currentOriginal ??= original;
            _startFailed = true;
        }
    }

    [UnmanagedCallersOnly]
    private static void DrawCommandCallback(nint callbackId)
    {
        if (Callbacks.TryGetValue(callbackId, out SceneRenderScheduler? scheduler))
        {
            scheduler.Execute(SceneRenderPhase.AfterGameUi);
        }
    }

    [StructLayout(LayoutKind.Auto)]
    private readonly record struct ContextState(uint SortKey, int ViewIndex);

    [StructLayout(LayoutKind.Auto)]
    private readonly record struct TargetCommand(nint Command)
    {
        public bool IsValid
            => Command != nint.Zero;
    }

    [StructLayout(LayoutKind.Auto)]
    private readonly record struct DetachedHooks(
        Hook<ContextSetRenderTargetsDelegate>? SetRenderTargetsHook,
        Hook<ImmediateContextSetTargetCommandDelegate>? SetTargetCommandHook,
        ContextSetRenderTargetsDelegate? SetRenderTargetsOriginal,
        ImmediateContextSetTargetCommandDelegate? SetTargetCommandOriginal,
        nint CallbackId);

    private unsafe delegate void ContextSetRenderTargetsDelegate(
        nint context,
        int targetCount,
        nint targetSlots,
        nint depthTarget,
        short left,
        short top,
        short right,
        short bottom);

    private unsafe delegate void ImmediateContextSetTargetCommandDelegate(
        nint immediateContext,
        RenderCommandSetTarget* command);

    [StructLayout(LayoutKind.Explicit, Size = 0x20)]
    private struct NativeCallbackCommand
    {
        [FieldOffset(0x00)] public int SwitchType;
        [FieldOffset(0x08)] public delegate* unmanaged<nint, void> Callback;
        [FieldOffset(0x10)] public nint Context;
        [FieldOffset(0x18)] public int Flags;
    }
}
