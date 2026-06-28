using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using Intoner.Objects.Interop;
using Intoner.Objects.Utils;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using RenderManager = FFXIVClientStructs.FFXIV.Client.Graphics.Render.Manager;

namespace Intoner.Objects.Rendering.Primitives;

internal sealed unsafe class PrimitiveService : IDisposable
{
    private const int DrawCallbackCommandType = 0x0E;
    private const int ContextCommandKeyOffset = 0x08;
    private const int ContextViewIndexOffset = 0x0C;
    private const int MainSceneViewIndex = (int)RenderManager.RenderViews.Main;

    private const uint FinalTargetBindCommandKey = 0xE0000000;
    private const uint BehindGameUiPrimitiveTargetBindCommandKey = FinalTargetBindCommandKey + 1;
    private const uint BehindGameUiPrimitiveCallbackCommandKey = FinalTargetBindCommandKey + 2;
    private const uint OverGameUiPrimitiveTargetBindCommandKey = 0xEC000004;
    private const uint OverGameUiPrimitiveCallbackCommandKey = 0xEC000005;

    private static readonly TimeSpan SubmittedCommandLifetime = TimeSpan.FromMilliseconds(250);
    private static readonly ConcurrentDictionary<nint, PrimitiveService> ActiveServices = new();
    private static long NextServiceId;

    private readonly ILogger<PrimitiveService> _logger;
    private readonly IGameInteropProvider      _gameInteropProvider;
    private readonly ISigScanner               _sigScanner;
    private readonly PrimitiveCallbackRenderer _renderer;
    private readonly Lock                      _stateLock          = new();
    private readonly ObjectDisposalState       _disposeState       = new();
    private readonly PrimitiveCommandStore     _commands           = new(SubmittedCommandLifetime);
    private readonly PrimitiveCommandList      _drawCommands       = new();
    private readonly nint _serviceId;

    private PrimitiveCommandBindings? _bindings;
    private Hook<ContextSetRenderTargetsDelegate>? _setRenderTargetsHook;
    private ContextSetRenderTargetsDelegate? _setRenderTargetsOriginal;
    private PrimitiveDrawState _drawState;
    private bool _hookDisposeInProgress;
    private bool _resolveFailed;
    private bool _hookFailed;
    private int _loggedDrawFailure;
    private int _loggedQueueFailure;
    private int _loggedDrawSuccess;

    public PrimitiveService(
        ILogger<PrimitiveService> logger,
        IGameInteropProvider gameInteropProvider,
        ISigScanner sigScanner,
        PrimitiveCallbackRenderer renderer)
    {
        _logger                    = logger;
        _gameInteropProvider       = gameInteropProvider;
        _sigScanner                = sigScanner;
        _renderer                  = renderer;
        _serviceId                 = (nint)Interlocked.Increment(ref NextServiceId);
        ActiveServices[_serviceId] = this;
    }

    public bool Commit(
        PrimitiveCommandList commands,
        PrimitiveDrawState state)
    {
        if (_disposeState.IsDisposing)
        {
            return false;
        }

        return _commands.Commit(commands, state) && TryEnableHook();
    }

    public void Dispose()
    {
        if (!_disposeState.TryBeginDispose())
        {
            return;
        }

        ActiveServices.TryRemove(_serviceId, out _);
        ReleaseDetachedState(DetachState());
    }

    public void Deactivate()
    {
        if (_disposeState.IsDisposing)
        {
            return;
        }

        ReleaseDetachedState(DetachState());
    }

    public void DeactivateIfIdle()
    {
        if (_disposeState.IsDisposing || _commands.HasLiveCommands())
        {
            return;
        }

        ReleaseDetachedState(DetachState());
    }

    private bool TryEnableHook()
    {
        lock (_stateLock)
        {
            if (_disposeState.IsDisposing || _hookDisposeInProgress || _resolveFailed)
            {
                return false;
            }

            _bindings ??= PrimitiveCommandBindings.Create(_logger, _sigScanner);
            if (_bindings == null)
            {
                _resolveFailed = true;
                _logger.LogWarning("native primitive drawing disabled because required render-command functions were unavailable");
                return false;
            }

            if (_setRenderTargetsHook != null)
            {
                return true;
            }

            if (_hookFailed)
            {
                return false;
            }

            nint setRenderTargetsAddress = ObjectNativeAddressResolver.TryScanSingleTextMatch(
                _logger,
                _sigScanner,
                ObjectSignatures.NativeContextSetRenderTargets.Signature,
                ObjectSignatures.NativeContextSetRenderTargets.Label);
            if (setRenderTargetsAddress == nint.Zero)
            {
                _hookFailed = true;
                _logger.LogWarning("native primitive drawing disabled because the final target hook was unavailable");
                return false;
            }

            Hook<ContextSetRenderTargetsDelegate>? setRenderTargetsHook = ObjectInteropHookUtility.CreateHookFromAddress<ContextSetRenderTargetsDelegate>(
                _logger,
                _gameInteropProvider,
                setRenderTargetsAddress,
                SetRenderTargetsDetour,
                ObjectSignatures.NativeContextSetRenderTargets.Label);
            if (setRenderTargetsHook == null)
            {
                _hookFailed = true;
                _logger.LogWarning("native primitive drawing disabled because the final target hook was unavailable");
                return false;
            }

            _setRenderTargetsHook     = setRenderTargetsHook;
            _setRenderTargetsOriginal = setRenderTargetsHook.Original;
            setRenderTargetsHook.Enable();
            _logger.LogInformation("native primitive drawing enabled through final scene render command");
            return true;
        }
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
        ContextSetRenderTargetsDelegate? setRenderTargetsOriginal = _setRenderTargetsOriginal;
        if (setRenderTargetsOriginal == null)
        {
            return;
        }

        setRenderTargetsOriginal(context, targetCount, targetSlots, depthTarget, left, top, right, bottom);

        if (!_disposeState.IsDisposing
            && IsFinalMainSceneTargetBind(context, targetCount, targetSlots, depthTarget))
        {
            QueueDrawCommand(context, targetSlots);
        }
    }

    private static bool IsFinalMainSceneTargetBind(
        nint context,
        int targetCount,
        nint targetSlots,
        nint depthTarget)
    {
        if (context == nint.Zero
            || targetCount != 1
            || targetSlots == nint.Zero
            || depthTarget != nint.Zero
            || *(int*)(context + ContextViewIndexOffset) != MainSceneViewIndex
            || *(uint*)(context + ContextCommandKeyOffset) != FinalTargetBindCommandKey)
        {
            return false;
        }

        return PrimitiveRenderTargetResolver.IsFinalTargetBind(targetSlots);
    }

    private void QueueDrawCommand(nint context, nint finalTargetSlots)
    {
        PrimitiveCommandBindings? bindings = _bindings;
        ContextSetRenderTargetsDelegate? setRenderTargetsOriginal = _setRenderTargetsOriginal;
        if (bindings == null || setRenderTargetsOriginal == null || !TryResolveCommandKeys(out PrimitiveCommandKeys commandKeys))
        {
            return;
        }

        NativeContextState contextState = CaptureContextState(context);
        try
        {
            *(int*)(context + ContextViewIndexOffset) = MainSceneViewIndex;
            *(uint*)(context + ContextCommandKeyOffset) = commandKeys.TargetBind;
            setRenderTargetsOriginal(context, 1, finalTargetSlots, nint.Zero, 0, 0, 0, 0);

            *(uint*)(context + ContextCommandKeyOffset) = commandKeys.Callback;

            nint commandAddress = bindings.AllocateCommand(context, (ulong)sizeof(NativeCallbackCommand));
            if (commandAddress == nint.Zero)
            {
                return;
            }

            var command = (NativeCallbackCommand*)commandAddress;
            command->SwitchType = DrawCallbackCommandType;
            command->Callback = &DrawCommandCallback;
            command->Context = _serviceId;
            command->Flags = 0;
            bindings.PushBackCommand(context, commandAddress);
        }
        catch (Exception ex) when (Interlocked.Exchange(ref _loggedQueueFailure, 1) == 0)
        {
            _logger.LogWarning(ex, "native primitive render-command queue failed");
        }
        finally
        {
            RestoreContextState(context, contextState);
        }
    }

    private bool TryResolveCommandKeys(out PrimitiveCommandKeys commandKeys)
    {
        if (!_commands.TryGetLiveDrawOverGameUi(out var drawOverGameUi))
        {
            commandKeys = default;
            return false;
        }

        commandKeys = drawOverGameUi
            ? new PrimitiveCommandKeys(OverGameUiPrimitiveTargetBindCommandKey, OverGameUiPrimitiveCallbackCommandKey)
            : new PrimitiveCommandKeys(BehindGameUiPrimitiveTargetBindCommandKey, BehindGameUiPrimitiveCallbackCommandKey);
        return true;
    }

    [UnmanagedCallersOnly]
    private static void DrawCommandCallback(nint serviceId)
    {
        if (ActiveServices.TryGetValue(serviceId, out PrimitiveService? service))
        {
            service.ExecuteDrawCommand();
        }
    }

    private void ExecuteDrawCommand()
    {
        if (_disposeState.IsDisposing)
        {
            return;
        }

        try
        {
            if (!_commands.TryCopyLiveTo(_drawCommands, out _drawState))
            {
                return;
            }

            if (_renderer.Draw(_drawCommands.Lines, _drawCommands.Points, _drawCommands.Screens, _drawState)
                && Interlocked.Exchange(ref _loggedDrawSuccess, 1) == 0)
            {
                _logger.LogInformation(
                    "native primitive drawing submitted first callback batch with {LineCount} lines, {PointCount} points, {ScreenCount} screen primitives, {DepthMode} depth mode, and {AntiAliasing} anti aliasing",
                    _drawCommands.LineCount,
                    _drawCommands.PointCount,
                    _drawCommands.ScreenCount,
                    _drawState.DepthMode,
                    _drawState.AntiAliasing);
            }
        }
        catch (Exception ex) when (Interlocked.Exchange(ref _loggedDrawFailure, 1) == 0)
        {
            _logger.LogWarning(ex, "native primitive draw command failed");
        }
    }

    private static NativeContextState CaptureContextState(nint context)
        => new(*(uint*)(context + ContextCommandKeyOffset), *(int*)(context + ContextViewIndexOffset));

    private static void RestoreContextState(nint context, NativeContextState state)
    {
        *(uint*)(context + ContextCommandKeyOffset) = state.CommandKey;
        *(int*)(context + ContextViewIndexOffset) = state.ViewIndex;
    }

    private DetachedPrimitiveState DetachState()
    {
        lock (_stateLock)
        {
            var detached = new DetachedPrimitiveState(_setRenderTargetsHook, _setRenderTargetsOriginal);
            _setRenderTargetsHook = null;
            _hookDisposeInProgress = detached.SetRenderTargetsHook != null;
            _commands.Clear();
            _drawCommands.Clear();
            return detached;
        }
    }

    private void ReleaseDetachedState(DetachedPrimitiveState detached)
    {
        ObjectInteropHookUtility.DisposeHook(detached.SetRenderTargetsHook);

        lock (_stateLock)
        {
            if (ReferenceEquals(_setRenderTargetsOriginal, detached.SetRenderTargetsOriginal))
            {
                _setRenderTargetsOriginal = null;
            }

            _hookDisposeInProgress = false;
        }
    }

    private readonly record struct DetachedPrimitiveState(
        Hook<ContextSetRenderTargetsDelegate>? SetRenderTargetsHook,
        ContextSetRenderTargetsDelegate? SetRenderTargetsOriginal);

    private readonly record struct PrimitiveCommandKeys(uint TargetBind, uint Callback);
    private readonly record struct NativeContextState(uint CommandKey, int ViewIndex);
}

