using Dalamud.Interface;
using System.Numerics;

namespace Intoner.Objects.UI.TitleBar;

internal readonly record struct TitleBarIndicator(
    FontAwesomeIcon Icon,
    string Label,
    string CompactLabel,
    Vector4 Accent,
    TitleBarIndicatorTooltip? Tooltip = null,
    TitleBarIndicatorLayout Layout = TitleBarIndicatorLayout.Label);

internal readonly record struct TitleBarIndicatorTooltip(
    Action DrawContent,
    float Width = 0f);

internal enum TitleBarIndicatorLayout
{
    Label,
    Counter,
}

