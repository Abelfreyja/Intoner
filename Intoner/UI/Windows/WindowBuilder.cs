using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;
using System.Numerics;

namespace Intoner.UI.Windows;

internal sealed class WindowBuilder
{
    private readonly Window _window;
    private readonly List<TitleBarButton> _titleButtons = new();

    private WindowBuilder(Window window)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
    }

    public static WindowBuilder For(Window window)
        => new(window);

    public WindowBuilder AllowPinning(bool allow = true)
    {
        _window.AllowPinning = allow;
        return this;
    }

    public WindowBuilder AllowClickthrough(bool allow = true)
    {
        _window.AllowClickthrough = allow;
        return this;
    }

    public WindowBuilder SetFixedSize(Vector2 size)
        => SetSizeConstraints(size, size);

    public WindowBuilder SetSizeConstraints(Vector2 min, Vector2 max)
    {
        _window.SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = min,
            MaximumSize = max,
        };
        return this;
    }

    public WindowBuilder AddFlags(ImGuiWindowFlags flags)
    {
        _window.Flags |= flags;
        return this;
    }

    public WindowBuilder AddTitleBarButton(FontAwesomeIcon icon, string tooltip, Action onClick, Vector2? iconOffset = null)
    {
        _titleButtons.Add(new TitleBarButton
        {
            Icon = icon,
            IconOffset = iconOffset ?? new Vector2(2, 1),
            Click = _ => onClick(),
            ShowTooltip = () => UiSharedService.AttachToolTip(tooltip),
        });
        return this;
    }

    public Window Apply()
    {
        if (_titleButtons.Count > 0)
        {
            _window.TitleBarButtons = _titleButtons;
        }

        return _window;
    }
}
