using Dalamud.Interface;
using System.Numerics;

namespace Intoner.Objects.UI.TitleBar;

internal readonly record struct TitleBarIndicator(
    FontAwesomeIcon Icon,
    string Label,
    string CompactLabel,
    Vector4 Accent);

