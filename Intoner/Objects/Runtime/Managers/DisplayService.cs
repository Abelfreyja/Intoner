using Intoner.Scene;

namespace Intoner.Displays;

/// <summary> provides display creation commands to the editor </summary>
internal interface IDisplayService
{
    /// <summary> creates and persists one display </summary>
    bool TryCreate(DisplaySettings settings, SceneTransform transform, bool visible, out DisplaySnapshot snapshot);
}

internal sealed class DisplayService : IDisplayService
{
    private readonly DisplayState _state;
    private readonly DisplayRuntimeManager _runtimes;

    public DisplayService(DisplayState state, DisplayRuntimeManager runtimes)
    {
        _state = state;
        _runtimes = runtimes;
    }

    public bool TryCreate(
        DisplaySettings settings,
        SceneTransform transform,
        bool visible,
        out DisplaySnapshot snapshot)
    {
        bool created = _state.TryCreate(settings, transform, visible, out snapshot);
        if (created)
        {
            _ = _runtimes.ReconcileAfterMutation();
        }

        return created;
    }
}
