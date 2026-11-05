using Dalamud.Interface;
using Intoner.Objects.UI.Components;
using Intoner.Scene;
using Intoner.Services.Input;
using System.Numerics;

namespace Intoner.Objects.UI;

internal sealed class EditorWorldPicker : IDisposable
{
    private const int NearbyRefreshIntervalMilliseconds = 1000;

    private readonly WorldAssetPicker _picker;
    private readonly IViewportMouseInputService _mouse;
    private readonly IUiBuilder _uiBuilder;
    private readonly ISceneLocationService _location;
    private bool _focusRequested;
    private long _nextNearbyRefresh;

    public bool IsOpen { get; private set; }
    public bool BlocksSceneInput { get; private set; }
    public bool IsPointerInScene { get; private set; }
    public WorldAssetPickState State => _picker.State;
    public WorldAsset? BrowserHover { get; set; }

    public EditorWorldPicker(WorldAssetPicker picker, IViewportMouseInputService mouse, IUiBuilder uiBuilder, ISceneLocationService location)
    {
        _picker = picker;
        _mouse = mouse;
        _uiBuilder = uiBuilder;
        _location = location;
        _uiBuilder.HideUi += Suspend;
        _location.TransitionStarted += Suspend;
    }

    public void BeginFrame()
    {
        BlocksSceneInput = State.IsPicking;
        BrowserHover = null;
    }

    public bool ConsumeFocusRequest()
    {
        bool requested = _focusRequested;
        _focusRequested = false;
        return requested;
    }

    public void Begin()
    {
        _mouse.Cancel();
        _picker.Begin();
        IsPointerInScene = false;
        IsOpen = true;
        _focusRequested = true;
        BlocksSceneInput = true;
    }

    public void Cancel()
    {
        if (!State.IsPicking)
        {
            return;
        }

        _mouse.Cancel();
        _picker.Cancel();
        IsPointerInScene = false;
        BlocksSceneInput = true;
    }

    public void Close()
    {
        Suspend();
        IsOpen = false;
    }

    private void Suspend()
    {
        Cancel();
        _picker.ClearScene();
        _nextNearbyRefresh = 0;
        BrowserHover = null;
        _focusRequested = false;
    }

    public void RefreshNearby(bool force = false)
    {
        long now = Environment.TickCount64;
        if (!force && now < _nextNearbyRefresh)
        {
            return;
        }

        _nextNearbyRefresh = now + NearbyRefreshIntervalMilliseconds;
        _picker.RefreshNearby();
    }

    public void ToggleNearbyOrigin()
    {
        _picker.ToggleNearbyOrigin();
        _nextNearbyRefresh = Environment.TickCount64 + NearbyRefreshIntervalMilliseconds;
    }

    public void Select(WorldAsset asset)
    {
        _mouse.Cancel();
        _picker.Select(asset);
        IsPointerInScene = false;
        BlocksSceneInput = true;
    }

    public void RefreshSelection()
        => _picker.RefreshSelection();

    public void ProcessPointer(EditorScreenArea viewport, Vector2 pointerPosition, bool canPick)
    {
        if (!State.IsPicking)
        {
            return;
        }

        IsPointerInScene = canPick;

        if (!canPick)
        {
            _mouse.Cancel();
            _picker.RefreshSelection();
            return;
        }

        Vector2 size = viewport.Max - viewport.Min;
        _mouse.Configure(viewport.Min, size, true, ViewportMouseCapture.LeftButton);
        for (int sample = 0; sample < 2 && _mouse.TryRead(out ViewportMouseInput input); ++sample)
        {
            if (!input.Cancelled && input.Pressed && _picker.ConfirmScreen(viewport.Min, size, input.Position))
            {
                _mouse.Cancel();
                IsPointerInScene = false;
                return;
            }
        }

        if (_picker.UpdateScreen(viewport.Min, size, pointerPosition) is { Status: WorldAssetPickStatus.Unavailable, Asset: null })
        {
            Cancel();
        }
    }

    public void Dispose()
    {
        _uiBuilder.HideUi -= Suspend;
        _location.TransitionStarted -= Suspend;
        _mouse.Cancel();
    }
}