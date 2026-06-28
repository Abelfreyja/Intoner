using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using System.Numerics;

namespace Intoner.Objects.UI.TitleBar;

internal sealed class TitleBarWindowMetrics
{
    public bool TryCreateContext(Window window, out TitleBarRenderContext context)
    {
        context = default;
        if (string.IsNullOrWhiteSpace(window.WindowName))
        {
            return false;
        }

        ImGuiWindowPtr imguiWindow = ImGuiP.FindWindowByName(window.WindowName);
        if (imguiWindow.IsNull)
        {
            return false;
        }

        context = TitleBarRenderContext.From(window, CreateSnapshot(imguiWindow));
        return true;
    }

    private static TitleBarWindowSnapshot CreateSnapshot(ImGuiWindowPtr window)
    {
        ImRect titleBarRect = window.TitleBarRect();
        return new TitleBarWindowSnapshot(titleBarRect.Min, titleBarRect.Max, window.DrawList);
    }
}

internal readonly record struct TitleBarWindowSnapshot(
    Vector2 TitleBarMin,
    Vector2 TitleBarMax,
    ImDrawListPtr DrawList);

internal readonly record struct TitleBarRenderContext(
    TitleBarWindowSnapshot Window,
    string WindowTitle,
    ImGuiWindowFlags WindowFlags,
    bool ShowCloseButton,
    bool AllowPinning,
    bool AllowClickthrough,
    IReadOnlyList<TitleBarButton> TitleBarButtons)
{
    public ImDrawListPtr DrawList => Window.DrawList;
    public Vector2 Min => Window.TitleBarMin;
    public Vector2 Max => Window.TitleBarMax;
    public float Height => Max.Y - Min.Y;

    public bool HasTitleBar
        => !WindowFlags.HasFlag(ImGuiWindowFlags.NoDecoration)
           && !WindowFlags.HasFlag(ImGuiWindowFlags.NoTitleBar);

    public static TitleBarRenderContext From(Window window, TitleBarWindowSnapshot snapshot)
        => new(
            snapshot,
            window.WindowName ?? string.Empty,
            window.Flags,
            window.ShowCloseButton,
            window.AllowPinning,
            window.AllowClickthrough,
            window.TitleBarButtons);
}

