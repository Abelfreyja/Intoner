using System.Numerics;

namespace Intoner.Objects.UI.Settings.Components;

internal sealed class ToggleSettingEntry : ISettingEntry
{
    private readonly Func<DrawContext, bool> _readValue;
    private readonly Action<DrawContext, bool> _writeValue;
    private readonly Func<DrawContext, SettingStatus> _resolveStatus;
    private readonly Func<DrawContext, bool> _isEnabled;

    public ToggleSettingEntry(
        SettingDefinition definition,
        Func<DrawContext, bool> readValue,
        Action<DrawContext, bool> writeValue,
        Func<DrawContext, SettingStatus> resolveStatus,
        Func<DrawContext, bool>? isEnabled = null)
    {
        Definition = definition;
        _readValue = readValue;
        _writeValue = writeValue;
        _resolveStatus = resolveStatus;
        _isEnabled = isEnabled ?? (static _ => true);
    }

    public SettingDefinition Definition { get; }

    public SettingRowLayout Layout
        => default;

    public void DrawRow(DrawContext context, Vector4 accent, bool prominentControl)
    {
        bool value = _readValue(context);
        if (ToggleRow.Draw(
                Definition,
                ref value,
                _resolveStatus(context),
                accent,
                prominentControl,
                _isEnabled(context)))
        {
            _writeValue(context, value);
        }
    }
}

