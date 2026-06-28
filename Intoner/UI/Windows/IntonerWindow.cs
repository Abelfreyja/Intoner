using Dalamud.Interface.Windowing;
using Intoner.UI.Performance;

namespace Intoner.UI.Windows;

internal abstract class IntonerWindow : Window
{
    private readonly IntonerUiPerformanceService _uiPerformance;

    protected IntonerWindow(
        string name,
        IntonerUiPerformanceService uiPerformance)
        : base(name)
        => _uiPerformance = uiPerformance;

    public sealed override void Draw()
    {
        using IntonerUiPerformanceService.Scope timing = _uiPerformance.Measure(this);
        DrawContent();
    }

    protected abstract void DrawContent();
}
