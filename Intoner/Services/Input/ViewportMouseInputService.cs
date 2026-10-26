using Dalamud.Hooking;
using Dalamud.Interface;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.Input;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Intoner.Objects.Utils;
using Intoner.Scene;
using Intoner.Services.Interop;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Intoner.Services.Input;

/// <summary> one viewport sample, with the initial press retained until the editor reads it </summary>
[StructLayout(LayoutKind.Auto)]
internal readonly record struct ViewportMouseInput(Vector2 Position, ScenePointerButtons Buttons, bool Pressed, bool Control, bool Cancelled);

/// <summary> observes editor left mouse inputs and optionally captures drags before viewport handling </summary>
internal interface IViewportMouseInputService
{
    /// <summary> installs input observation once and awaits framework initialization </summary>
    Task StartAsync();

    /// <summary> gets whether a captured input or its final unread sample still owns the pointer </summary>
    bool IsCapturing { get; }

    /// <summary> publishes eligibility for the next press; viewport or capture mode changes cancel an existing input </summary>
    /// <param name="viewportPosition"> the screen position of the game client viewport </param>
    /// <param name="viewportSize"> the client viewport size in pixels </param>
    /// <param name="canStart"> whether the editor permits a new input at the current pointer </param>
    /// <param name="captureDrag"> whether new inputs suppress viewport input for box selection </param>
    void Configure(Vector2 viewportPosition, Vector2 viewportSize, bool canStart, bool captureDrag);

    /// <summary> reads the initial press before the latest motion or release; haflway motion is not queued </summary>
    /// <param name="input"> the next viewport sample in screen coordinates </param>
    /// <returns> true when a sample was available </returns>
    bool TryRead(out ViewportMouseInput input);

    /// <summary> disables new capture and discards pending samples while suppressing the captured left button until release </summary>
    void Cancel();
}

/// <summary> tracks one editor input and optionally filters input after game UI processing, before camera processing </summary>
internal sealed unsafe class ViewportMouseInputService : FrameworkNativeService, IViewportMouseInputService
{
    internal enum InputFilter
    {
        None,
        LeftButton,
        Viewport,
    }

    private readonly IUiBuilder _uiBuilder;
    private readonly IGameInteropProvider? _gameInteropProvider;
    private Hook<AtkModule.Delegates.HandleInput>? _handleInputHook;
    private readonly Lock _stateLock = new();
    private Vector2 _viewportPosition;
    private Vector2 _viewportSize;
    private ViewportMouseInput? _press;
    private ViewportMouseInput? _latest;
    private bool _canStart;
    private bool _captureDrag;
    private bool _capturedLeft;
    private bool _ownsLeft;
    private bool _cancelled;
    private bool _disposed;

    public ViewportMouseInputService(
        IUiBuilder uiBuilder,
        IGameInteropProvider gameInteropProvider,
        ILogger<ViewportMouseInputService> logger,
        IFramework framework)
        : this(uiBuilder, logger, framework)
    {
        _gameInteropProvider = gameInteropProvider;
    }

    internal ViewportMouseInputService(IUiBuilder uiBuilder, IFramework framework)
        : this(uiBuilder, NullLogger<ViewportMouseInputService>.Instance, framework)
    {
    }

    private ViewportMouseInputService(IUiBuilder uiBuilder, ILogger<ViewportMouseInputService> logger, IFramework framework)
        : base(framework, logger)
    {
        _uiBuilder = uiBuilder;
        _uiBuilder.HideUi += Cancel;
    }

    protected override void InitializeCore()
    {
        _handleInputHook = Hooks.Add(_gameInteropProvider!.HookFromAddress<AtkModule.Delegates.HandleInput>(
            (nint)AtkModule.MemberFunctionPointers.HandleInput, HandleInput));
        Hooks.Enable();
    }

    public bool IsCapturing
    {
        get
        {
            lock (_stateLock)
            {
                return _capturedLeft && (_ownsLeft || _press.HasValue || _latest.HasValue);
            }
        }
    }

    public void Configure(Vector2 viewportPosition, Vector2 viewportSize, bool canStart, bool captureDrag)
    {
        lock (_stateLock)
        {
            if (_viewportPosition != viewportPosition || _viewportSize != viewportSize || _captureDrag != captureDrag)
            {
                CancelUnsafe();
                if (_ownsLeft)
                {
                    _latest = new(default, ScenePointerButtons.Left, false, false, true);
                }
            }

            _viewportPosition = viewportPosition;
            _viewportSize = viewportSize;
            _captureDrag = captureDrag;
            _canStart = !_disposed && canStart && NumericsUtility.IsFinite(viewportPosition)
                && NumericsUtility.IsFinite(viewportSize) && viewportSize.X > 0 && viewportSize.Y > 0;
        }
    }

    public bool TryRead(out ViewportMouseInput input)
    {
        lock (_stateLock)
        {
            ViewportMouseInput? pending = _press ?? _latest;
            if (_press.HasValue)
            {
                _press = null;
            }
            else
            {
                _latest = null;
            }

            input = pending.GetValueOrDefault();
            return pending.HasValue;
        }
    }

    public void Cancel()
    {
        lock (_stateLock)
        {
            CancelUnsafe();
        }
    }

    protected override void DisposeCore()
    {
        bool firstDispose;
        lock (_stateLock)
        {
            firstDispose = !_disposed;
            if (firstDispose)
            {
                _disposed = true;
                CancelUnsafe();
                _ownsLeft = false;
            }
        }

        try
        {
            if (firstDispose)
            {
                _uiBuilder.HideUi -= Cancel;
            }
        }
        finally
        {
            base.DisposeCore();
        }
    }

    private void CancelUnsafe()
    {
        _canStart = false;
        _cancelled = true;
        _press = null;
        _latest = null;
    }

    private byte HandleInput(AtkModule* module, UIInputData* inputData, bool isPadMouseModeEnabled)
    {
        using InteropHookCollection.CallbackScope callback = Hooks.EnterCallback();
        byte result = _handleInputHook!.OriginalDisposeSafe(module, inputData, isPadMouseModeEnabled);
        if (!callback.IsActive)
        {
            return result;
        }

        InputFilter filter = ProcessInput(in *inputData);
        if (filter == InputFilter.Viewport)
        {
            inputData->FilterUICursorInputs(MouseButtonFlags.LBUTTON);
            inputData->FilterDragInputs();
            return 1;
        }

        if (filter == InputFilter.LeftButton)
        {
            FilterLeftButton(ref *inputData);
        }

        return result;
    }

    internal static void FilterLeftButton(ref UIInputData inputData)
    {
        // preserve camera deltas, wheel input and other buttons while draining the captured press
        ref CursorInputData cursor = ref inputData.UIFilteredCursorInputs;
        cursor.MouseButtonHeldFlags &= ~MouseButtonFlags.LBUTTON;
        cursor.MouseButtonPressedFlags &= ~MouseButtonFlags.LBUTTON;
        cursor.MouseButtonReleasedFlags &= ~MouseButtonFlags.LBUTTON;
        cursor.MouseButtonHeldThrottledFlags &= ~MouseButtonFlags.LBUTTON;
        inputData.CurrentMouseDragButtons &= unchecked((byte)~MouseButtonFlags.LBUTTON);
    }

    internal InputFilter ProcessInput(in UIInputData inputData)
    {
        lock (_stateLock)
        {
            if (IsDisposing || _disposed || (!_canStart && !_ownsLeft))
            {
                return InputFilter.None;
            }

            CursorInputData cursor = inputData.CursorInputs;
            MouseButtonFlags buttons = cursor.MouseButtonHeldFlags | cursor.MouseButtonPressedFlags;
            bool leftDown = (buttons & MouseButtonFlags.LBUTTON) != MouseButtonFlags.None;
            bool auxiliaryButton = (buttons & ~MouseButtonFlags.LBUTTON) != MouseButtonFlags.None;
            bool focused = cursor.IsGameWindowFocused;
            Vector2 position = new(cursor.PositionX, cursor.PositionY);
            ScenePointerButtons sceneButtons = ScenePointerButtons.None;
            if (leftDown) sceneButtons |= ScenePointerButtons.Left;
            if ((buttons & MouseButtonFlags.RBUTTON) != MouseButtonFlags.None) sceneButtons |= ScenePointerButtons.Right;
            if ((buttons & MouseButtonFlags.MBUTTON) != MouseButtonFlags.None) sceneButtons |= ScenePointerButtons.Middle;
            bool control = (inputData.CurrentKeyModifier & KeyModifierFlag.Ctrl) != KeyModifierFlag.None;

            if (!_ownsLeft)
            {
                if (!focused || auxiliaryButton || _press.HasValue || _latest.HasValue
                    || (inputData.UIFilteredCursorInputs.MouseButtonPressedFlags & MouseButtonFlags.LBUTTON) == MouseButtonFlags.None
                    || position.X < 0 || position.Y < 0 || position.X >= _viewportSize.X || position.Y >= _viewportSize.Y)
                {
                    return InputFilter.None;
                }

                _ownsLeft = true;
                _capturedLeft = _captureDrag;
                _cancelled = false;
                _press = new(position + _viewportPosition, sceneButtons, true, control, false);
                return _capturedLeft ? InputFilter.Viewport : InputFilter.None;
            }

            if (!_cancelled)
            {
                _cancelled = !focused || auxiliaryButton;
                _latest = new(position + _viewportPosition, sceneButtons, false, control, _cancelled);
            }

            _ownsLeft = leftDown;
            if (!_capturedLeft)
            {
                return InputFilter.None;
            }

            return _cancelled ? InputFilter.LeftButton : InputFilter.Viewport;
        }
    }
}
