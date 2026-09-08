using Dalamud.Bindings.ImGui;

namespace Intoner.Services;

/// <summary> reads and writes system clipboard text </summary>
internal interface IClipboardTextService
{
    /// <summary> reads the current clipboard text </summary>
    string ReadText();

    /// <summary> replaces the current clipboard text </summary>
    void WriteText(string text);
}

internal sealed class ClipboardTextService : IClipboardTextService
{
    public string ReadText()
        => ImGui.GetClipboardText() ?? string.Empty;

    public void WriteText(string text)
        => ImGui.SetClipboardText(text ?? string.Empty);
}
