using System.Numerics;

namespace Intoner.Objects.UI.Settings.Components;

internal sealed class ChoiceSettingEntry<TValue> : ISettingEntry
{
    private readonly IReadOnlyList<ChoiceOption<TValue>> _options;
    private readonly Func<DrawContext, TValue> _readValue;
    private readonly Action<DrawContext, TValue> _writeValue;
    private readonly Func<DrawContext, TValue, bool> _isOptionEnabled;
    private readonly Func<DrawContext, bool> _isEnabled;
    private readonly ChoiceRowStyle _style;
    private readonly SettingRowLayout _layout;

    public ChoiceSettingEntry(
        SettingDefinition definition,
        IReadOnlyList<ChoiceOption<TValue>> options,
        Func<DrawContext, TValue> readValue,
        Action<DrawContext, TValue> writeValue,
        Func<DrawContext, TValue, bool>? isOptionEnabled = null,
        Func<DrawContext, bool>? isEnabled = null,
        ChoiceRowStyle style = ChoiceRowStyle.Combo,
        SettingRowLayout layout = default)
    {
        Definition = definition;
        _options = options;
        _readValue = readValue;
        _writeValue = writeValue;
        _isOptionEnabled = isOptionEnabled ?? (static (_, _) => true);
        _isEnabled = isEnabled ?? (static _ => true);
        _style = style;
        _layout = layout;
    }

    public SettingDefinition Definition { get; }

    public SettingRowLayout Layout
        => _layout;

    public void DrawRow(DrawContext context, Vector4 accent, bool prominentControl)
    {
        TValue value = _readValue(context);
        bool enabled = _isEnabled(context);
        bool changed = _style switch
        {
            ChoiceRowStyle.Segmented => SegmentedChoiceRow.Draw(
                Definition,
                _options,
                ref value,
                accent,
                prominentControl,
                enabled,
                option => _isOptionEnabled(context, option),
                _layout),
            _ => ChoiceRow.Draw(
                Definition,
                _options,
                ref value,
                accent,
                enabled,
                option => _isOptionEnabled(context, option),
                _layout),
        };

        if (changed)
        {
            _writeValue(context, value);
        }
    }
}

internal enum ChoiceRowStyle
{
    Combo,
    Segmented,
}

