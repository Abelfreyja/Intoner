using System.Numerics;

namespace Intoner.Objects.UI.Settings.Components;

internal sealed class ToggleSettingEntry : ISettingEntry
{
    private readonly Func<bool> _readValue;
    private readonly Action<bool> _writeValue;
    private readonly Func<SettingStatus> _resolveStatus;
    private readonly Func<bool> _isEnabled;

    public ToggleSettingEntry(
        SettingDefinition definition,
        Func<bool> readValue,
        Action<bool> writeValue,
        Func<SettingStatus> resolveStatus,
        Func<bool>? isEnabled = null)
    {
        Definition = definition;
        _readValue = readValue;
        _writeValue = writeValue;
        _resolveStatus = resolveStatus;
        _isEnabled = isEnabled ?? (static () => true);
    }

    public SettingDefinition Definition { get; }

    public SettingRowLayout Layout
        => default;

    public void DrawRow(Vector4 accent, bool prominentControl)
    {
        bool value = _readValue();
        if (ToggleRow.Draw(
                Definition,
                ref value,
                _resolveStatus(),
                accent,
                prominentControl,
                _isEnabled()))
        {
            _writeValue(value);
        }
    }
}

