using System.Numerics;

namespace Intoner.Objects.UI.Settings.Components;

internal sealed class IntegerSettingEntry : ISettingEntry
{
    private readonly IntegerSettingRange _range;
    private readonly Func<int> _readValue;
    private readonly Action<int> _writeValue;
    private readonly Func<int, string> _formatValue;
    private readonly Func<int, string> _formatRangeValue;
    private readonly Func<bool> _isEnabled;
    private readonly SettingRowLayout _layout;
    private readonly IntegerSettingEditState _editState = new();

    private int _pendingValue;
    private bool _hasPendingValue;

    public IntegerSettingEntry(
        SettingDefinition definition,
        IntegerSettingRange range,
        Func<int> readValue,
        Action<int> writeValue,
        Func<int, string> formatValue,
        Func<int, string>? formatRangeValue = null,
        Func<bool>? isEnabled = null,
        SettingRowLayout layout = default)
    {
        Definition = definition;
        _range = range;
        _readValue = readValue;
        _writeValue = writeValue;
        _formatValue = formatValue;
        _formatRangeValue = formatRangeValue ?? formatValue;
        _isEnabled = isEnabled ?? (static () => true);
        _layout = layout;
    }

    public SettingDefinition Definition { get; }

    public SettingRowLayout Layout
        => _layout;

    public void DrawRow(Vector4 accent, bool prominentControl)
    {
        bool enabled = _isEnabled();
        int savedValue = _range.Clamp(_readValue());
        if (!enabled)
        {
            _hasPendingValue = false;
            _editState.End();
        }

        int value = _hasPendingValue ? _pendingValue : savedValue;
        if (IntegerRow.Draw(
                Definition,
                ref value,
                _range,
                _formatValue(value),
                _formatRangeValue(_range.Minimum),
                _formatRangeValue(_range.Maximum),
                accent,
                prominentControl,
                enabled,
                _editState,
                _layout,
                out IntegerSettingUpdate update))
        {
            ApplyUpdate(savedValue, update);
        }
    }

    private void ApplyUpdate(int savedValue, IntegerSettingUpdate update)
    {
        int nextValue = _range.Clamp(update.Value);
        if (!update.Commit)
        {
            _pendingValue = nextValue;
            _hasPendingValue = true;
            return;
        }

        _hasPendingValue = false;
        if (nextValue != savedValue)
        {
            _writeValue(nextValue);
        }
    }
}

