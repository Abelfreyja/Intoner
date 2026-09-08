using Dalamud.Interface;
using Intoner.Services.Input;
using Microsoft.Extensions.Logging;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Intoner.Scene;

[Flags]
internal enum ScenePointerButtons
{
    None = 0,
    Left = 1 << 0,
    Right = 1 << 1,
    Middle = 1 << 2,
}

internal enum ScenePointerButton
{
    None,
    Left,
    Right,
    Middle,
}

internal enum ScenePointerEventKind
{
    Move,
    Leave,
    ButtonDown,
    ButtonUp,
    Wheel,
}

[StructLayout(LayoutKind.Auto)]
internal readonly record struct ScenePointerRay(Vector3 Origin, Vector3 Direction);

[StructLayout(LayoutKind.Auto)]
internal readonly record struct ScenePointerEvent(
    ScenePointerEventKind Kind,
    Vector2 Position,
    ScenePointerButton Button,
    ScenePointerButtons Buttons,
    float WheelSteps);

/// <summary> receives scene pointer input through world geometry </summary>
internal interface ISceneInputTarget
{
    /// <summary> gets the owning scene item id </summary>
    Guid ItemId { get; }

    /// <summary> gets whether the target can currently receive pointer input </summary>
    bool CanReceivePointerInput { get; }

    /// <summary> resolves a world ray against the target and maps the hit into normalized input coordinates </summary>
    bool TryMapPointer(in ScenePointerRay ray, out Vector2 position);

    /// <summary> delivers one normalized pointer event to the target </summary>
    bool TrySendPointer(in ScenePointerEvent pointerEvent);

    /// <summary> releases any native pointer state retained by the target </summary>
    void CancelPointerInput();
}

/// <summary> registers scene runtimes that can receive pointer interaction </summary>
internal interface ISceneInputRegistry
{
    /// <summary> registers a target until the returned ownership handle is disposed </summary>
    bool TryRegister(ISceneInputTarget target, out IDisposable? registration);
}

/// <summary> owns explicit pointer interaction with one active scene target </summary>
internal interface ISceneInputService : IDisposable
{
    /// <summary> gets whether a scene target currently owns pointer interaction </summary>
    bool IsActive { get; }

    /// <summary> gets whether the given scene item owns pointer interaction </summary>
    bool IsActiveFor(Guid itemId);

    /// <summary> gets whether the given scene item can currently receive pointer interaction </summary>
    bool CanActivate(Guid itemId);

    /// <summary> activates pointer interaction for one scene item </summary>
    bool TryActivate(Guid itemId, ScenePointerButtons currentButtons);

    /// <summary> stops the current interaction and releases forwarded buttons </summary>
    void Deactivate();

    /// <summary> processes one pointer sample for the active scene target </summary>
    bool ProcessPointer(
        Vector2 viewportPosition,
        Vector2 viewportSize,
        Vector2 mousePosition,
        ScenePointerButtons buttons,
        float wheelSteps,
        bool allowNewInput);
}

internal sealed class SceneInputService : ISceneInputService, ISceneInputRegistry
{
    private const int HoverMissTolerance = 2;

    private readonly ILogger<SceneInputService> _logger;
    private readonly IUiBuilder _uiBuilder;
    private readonly Lock _targetLock = new();
    private readonly Dictionary<Guid, ISceneInputTarget> _targets = [];

    private ISceneInputTarget? _target;
    private ScenePointerButtons _physicalButtons;
    private ScenePointerButtons _forwardedButtons;
    private Vector2 _lastPosition;
    private int _consecutiveMissCount;
    private bool _hasPosition;
    private bool _isHovering;
    private volatile bool _disposed;

    public SceneInputService(
        ILogger<SceneInputService> logger,
        IUiBuilder uiBuilder)
    {
        _logger = logger;
        _uiBuilder = uiBuilder;
        _uiBuilder.HideUi += HandleUiHidden;
    }

    public bool IsActive
        => _target != null;

    public bool IsActiveFor(Guid itemId)
        => _target?.ItemId == itemId;

    public bool CanActivate(Guid itemId)
        => !_disposed && FindTarget(itemId)?.CanReceivePointerInput == true;

    public bool TryRegister(ISceneInputTarget target, out IDisposable? registration)
    {
        ArgumentNullException.ThrowIfNull(target);
        registration = null;
        lock (_targetLock)
        {
            if (_disposed || !_targets.TryAdd(target.ItemId, target))
            {
                return false;
            }
        }

        registration = new TargetRegistration(this, target);
        return true;
    }

    public bool TryActivate(Guid itemId, ScenePointerButtons currentButtons)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_target?.ItemId == itemId)
        {
            return true;
        }

        Deactivate();
        ISceneInputTarget? target = FindTarget(itemId);
        if (target?.CanReceivePointerInput != true)
        {
            _logger.LogWarning("scene pointer interaction could not start for {ItemId}", itemId);
            return false;
        }

        _target = target;
        _physicalButtons = currentButtons;
        return true;
    }

    public void Deactivate()
    {
        ISceneInputTarget? target = _target;
        ReleaseForwardedButtons();
        target?.CancelPointerInput();
        _target = null;
        _physicalButtons = ScenePointerButtons.None;
        _consecutiveMissCount = 0;
        _hasPosition = false;
        _isHovering = false;
    }

    public bool ProcessPointer(
        Vector2 viewportPosition,
        Vector2 viewportSize,
        Vector2 mousePosition,
        ScenePointerButtons buttons,
        float wheelSteps,
        bool allowNewInput)
    {
        ISceneInputTarget? target = _target;
        if (_disposed || target == null)
        {
            return false;
        }

        if (!target.CanReceivePointerInput)
        {
            _logger.LogWarning("scene pointer target {ItemId} became unavailable", target.ItemId);
            Deactivate();
            return false;
        }

        Vector2 targetPosition = default;
        bool hasHit = false;
        bool canRouteInput = allowNewInput && DesktopInputGuard.CanRoutePointerToCurrentProcess();
        if (canRouteInput)
        {
            if (SceneScreenRaycaster.TryBuildScreenRay(
                    viewportPosition,
                    viewportSize,
                    mousePosition,
                    out Vector3 rayOrigin,
                    out Vector3 rayDirection))
            {
                hasHit = target.TryMapPointer(
                    new ScenePointerRay(rayOrigin, rayDirection),
                    out targetPosition);
            }

            if (hasHit)
            {
                _consecutiveMissCount = 0;
                _lastPosition = targetPosition;
                _hasPosition = true;
                if (!Send(target, new ScenePointerEvent(
                        ScenePointerEventKind.Move,
                        targetPosition,
                        ScenePointerButton.None,
                        _forwardedButtons,
                        0f)))
                {
                    Deactivate();
                    return false;
                }
            }
            else
            {
                ++_consecutiveMissCount;
            }
        }
        else
        {
            _consecutiveMissCount = HoverMissTolerance + 1;
        }

        if (hasHit)
        {
            _isHovering = true;
        }

        ScenePointerButtons changedButtons = _physicalButtons ^ buttons;
        ScenePointerButtons pressedButtons = changedButtons & buttons;
        ScenePointerButtons releasedButtons = changedButtons & ~buttons;
        _physicalButtons = buttons;
        bool retainsHover = _isHovering && _consecutiveMissCount <= HoverMissTolerance;

        if (hasHit && !ForwardPressedButtons(target, pressedButtons))
        {
            Deactivate();
            return false;
        }

        if (!ForwardReleasedButtons(target, releasedButtons))
        {
            Deactivate();
            return false;
        }

        if (_isHovering
            && !retainsHover
            && _forwardedButtons == ScenePointerButtons.None)
        {
            if (!Send(target, new ScenePointerEvent(
                    ScenePointerEventKind.Leave,
                    _lastPosition,
                    ScenePointerButton.None,
                    ScenePointerButtons.None,
                    0f)))
            {
                Deactivate();
                return false;
            }

            _isHovering = false;
        }

        Vector2 wheelPosition = hasHit ? targetPosition : _lastPosition;
        bool hasWheelInput = MathF.Abs(wheelSteps) > 0.001f;
        if (retainsHover
            && hasWheelInput
            && !Send(target, new ScenePointerEvent(
                ScenePointerEventKind.Wheel,
                wheelPosition,
                ScenePointerButton.None,
                _forwardedButtons,
                wheelSteps)))
        {
            Deactivate();
            return false;
        }

        return retainsHover || _forwardedButtons != ScenePointerButtons.None;
    }

    public void Dispose()
    {
        lock (_targetLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _uiBuilder.HideUi -= HandleUiHidden;
        Deactivate();
        lock (_targetLock)
        {
            _targets.Clear();
        }
    }

    private bool ForwardPressedButtons(ISceneInputTarget target, ScenePointerButtons pressedButtons)
    {
        foreach (ScenePointerButton button in ScenePointerButtonExtensions.OrderedButtons)
        {
            ScenePointerButtons flag = button.ToButtons();
            if ((pressedButtons & flag) == ScenePointerButtons.None)
            {
                continue;
            }

            ScenePointerButtons nextButtons = _forwardedButtons | flag;
            if (!Send(target, new ScenePointerEvent(
                    ScenePointerEventKind.ButtonDown,
                    _lastPosition,
                    button,
                    nextButtons,
                    0f)))
            {
                return false;
            }

            _forwardedButtons = nextButtons;
        }

        return true;
    }

    private bool ForwardReleasedButtons(ISceneInputTarget target, ScenePointerButtons releasedButtons)
    {
        foreach (ScenePointerButton button in ScenePointerButtonExtensions.OrderedButtons)
        {
            ScenePointerButtons flag = button.ToButtons();
            if ((releasedButtons & flag) == ScenePointerButtons.None
                || (_forwardedButtons & flag) == ScenePointerButtons.None)
            {
                continue;
            }

            ScenePointerButtons nextButtons = _forwardedButtons & ~flag;
            if (!Send(target, new ScenePointerEvent(
                    ScenePointerEventKind.ButtonUp,
                    _lastPosition,
                    button,
                    nextButtons,
                    0f)))
            {
                return false;
            }

            _forwardedButtons = nextButtons;
        }

        return true;
    }

    private void ReleaseForwardedButtons()
    {
        ISceneInputTarget? target = _target;
        if (target == null || !_hasPosition)
        {
            _forwardedButtons = ScenePointerButtons.None;
            return;
        }

        _ = ForwardReleasedButtons(target, _forwardedButtons);
        _forwardedButtons = ScenePointerButtons.None;
    }

    private bool Send(ISceneInputTarget target, in ScenePointerEvent pointerEvent)
    {
        if (!target.TrySendPointer(pointerEvent))
        {
            _logger.LogWarning(
                "scene pointer target {ItemId} rejected {EventKind} delivery",
                target.ItemId,
                pointerEvent.Kind);
            return false;
        }

        return true;
    }

    private void HandleUiHidden()
        => Deactivate();

    private ISceneInputTarget? FindTarget(Guid itemId)
    {
        lock (_targetLock)
        {
            return _targets.GetValueOrDefault(itemId);
        }
    }

    private void UnregisterTarget(ISceneInputTarget target)
    {
        lock (_targetLock)
        {
            if (_targets.TryGetValue(target.ItemId, out ISceneInputTarget? registered)
                && ReferenceEquals(registered, target))
            {
                _targets.Remove(target.ItemId);
            }
        }

        if (ReferenceEquals(_target, target))
        {
            Deactivate();
        }
    }

    private sealed class TargetRegistration : IDisposable
    {
        private SceneInputService? _owner;
        private readonly ISceneInputTarget _target;

        public TargetRegistration(SceneInputService owner, ISceneInputTarget target)
        {
            _owner = owner;
            _target = target;
        }

        public void Dispose()
            => Interlocked.Exchange(ref _owner, null)?.UnregisterTarget(_target);
    }
}

internal static class ScenePointerButtonExtensions
{
    public static ReadOnlySpan<ScenePointerButton> OrderedButtons
        => [ScenePointerButton.Left, ScenePointerButton.Right, ScenePointerButton.Middle];

    public static ScenePointerButtons ToButtons(this ScenePointerButton button)
        => button switch
        {
            ScenePointerButton.Left => ScenePointerButtons.Left,
            ScenePointerButton.Right => ScenePointerButtons.Right,
            ScenePointerButton.Middle => ScenePointerButtons.Middle,
            _ => ScenePointerButtons.None,
        };

}
