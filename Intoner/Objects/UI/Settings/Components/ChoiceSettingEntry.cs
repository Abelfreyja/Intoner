using System.Numerics;

namespace Intoner.Objects.UI.Settings.Components;

internal sealed class ChoiceSettingEntry<TValue> : ISettingEntry
{
    private readonly IReadOnlyList<ChoiceOption<TValue>> _options;
    private readonly Func<TValue> _readValue;
    private readonly Action<TValue> _writeValue;
    private readonly Func<TValue, bool> _isOptionEnabled;
    private readonly Func<bool> _isEnabled;
    private readonly ChoiceRowStyle _style;
    private readonly SettingRowLayout _layout;

    public ChoiceSettingEntry(
        SettingDefinition definition,
        IReadOnlyList<ChoiceOption<TValue>> options,
        Func<TValue> readValue,
        Action<TValue> writeValue,
        Func<TValue, bool>? isOptionEnabled = null,
        Func<bool>? isEnabled = null,
        ChoiceRowStyle style = ChoiceRowStyle.Combo,
        SettingRowLayout layout = default)
    {
        Definition = definition;
        _options = options;
        _readValue = readValue;
        _writeValue = writeValue;
        _isOptionEnabled = isOptionEnabled ?? (static _ => true);
        _isEnabled = isEnabled ?? (static () => true);
        _style = style;
        _layout = layout;
    }

    public SettingDefinition Definition { get; }

    public SettingRowLayout Layout
        => _layout;

    public void DrawRow(Vector4 accent, bool prominentControl)
    {
        TValue value = _readValue();
        bool enabled = _isEnabled();
        bool changed = _style switch
        {
            ChoiceRowStyle.Segmented => SegmentedChoiceRow.Draw(
                Definition,
                _options,
                ref value,
                accent,
                prominentControl,
                enabled,
                _isOptionEnabled,
                _layout),
            _ => ChoiceRow.Draw(
                Definition,
                _options,
                ref value,
                accent,
                enabled,
                _isOptionEnabled,
                _layout),
        };

        if (changed)
        {
            _writeValue(value);
        }
    }
}

internal enum ChoiceRowStyle
{
    Combo,
    Segmented,
}

