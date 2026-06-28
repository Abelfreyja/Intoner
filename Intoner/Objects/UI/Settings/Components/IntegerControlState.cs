namespace Intoner.Objects.UI.Settings.Components;

internal readonly record struct IntegerRangeControlUpdate(int Value, bool Commit);

internal readonly record struct IntegerInputUpdate(int Value, bool Commit, bool Canceled);

internal readonly record struct IntegerSliderUpdate(bool Changed, bool Commit, bool ManualInputRequested);

internal readonly record struct IntegerSettingUpdate(int Value, bool Commit);

internal sealed class IntegerSettingEditState
{
    private bool _focusRequested;

    public bool IsActive { get; private set; }
    public int Value { get; set; }

    public void Begin(int value)
    {
        Value = value;
        IsActive = true;
        _focusRequested = true;
    }

    public void End()
    {
        IsActive = false;
        _focusRequested = false;
    }

    public bool ConsumeFocusRequest()
    {
        if (!_focusRequested)
        {
            return false;
        }

        _focusRequested = false;
        return true;
    }
}

