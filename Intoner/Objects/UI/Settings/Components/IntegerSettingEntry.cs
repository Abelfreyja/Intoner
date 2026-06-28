using System.Numerics;

namespace Intoner.Objects.UI.Settings.Components;

internal sealed class IntegerSettingEntry : ISettingEntry
{
    private readonly IntegerSettingRange _range;
    private readonly Func<DrawContext, int> _readValue;
    private readonly Action<DrawContext, int> _writeValue;
    private readonly Func<DrawContext, int, string> _formatValue;
    private readonly Func<DrawContext, int, string> _formatRangeValue;
    private readonly Func<DrawContext, bool> _isEnabled;
    private readonly SettingRowLayout _layout;
    private readonly IntegerSettingEditState _editState = new();

    private int _pendingValue;
    private bool _hasPendingValue;

    public IntegerSettingEntry(
        SettingDefinition definition,
        IntegerSettingRange range,
        Func<DrawContext, int> readValue,
        Action<DrawContext, int> writeValue,
        Func<DrawContext, int, string> formatValue,
        Func<DrawContext, int, string>? formatRangeValue = null,
        Func<DrawContext, bool>? isEnabled = null,
        SettingRowLayout layout = default)
    {
        Definition = definition;
        _range = range;
        _readValue = readValue;
        _writeValue = writeValue;
        _formatValue = formatValue;
        _formatRangeValue = formatRangeValue ?? formatValue;
        _isEnabled = isEnabled ?? (static _ => true);
        _layout = layout;
    }

    public SettingDefinition Definition { get; }

    public SettingRowLayout Layout
        => _layout;

    public void DrawRow(DrawContext context, Vector4 accent, bool prominentControl)
    {
        bool enabled = _isEnabled(context);
        int savedValue = _range.Clamp(_readValue(context));
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
                _formatValue(context, value),
                _formatRangeValue(context, _range.Minimum),
                _formatRangeValue(context, _range.Maximum),
                accent,
                prominentControl,
                enabled,
                _editState,
                _layout,
                out IntegerSettingUpdate update))
        {
            ApplyUpdate(context, savedValue, update);
        }
    }

    private void ApplyUpdate(DrawContext context, int savedValue, IntegerSettingUpdate update)
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
            _writeValue(context, nextValue);
        }
    }
}

