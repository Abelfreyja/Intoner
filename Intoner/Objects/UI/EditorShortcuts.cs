using FFXIVClientStructs.FFXIV.Client.System.Input;
using Intoner.Services.Input;

namespace Intoner.Objects.UI;

/// <summary> defines keyboard shortcuts owned by the editor </summary>
internal sealed class EditorShortcuts : IShortcutProvider
{
    public static ShortcutDefinition Copy { get; } = new(
        "editor.copy",
        "Copy",
        "Copy the selected editor objects",
        new KeyboardGesture(SeVirtualKey.C, KeyboardModifiers.Control));

    public static ShortcutDefinition Cut { get; } = new(
        "editor.cut",
        "Cut",
        "Cut the selected editor objects",
        new KeyboardGesture(SeVirtualKey.X, KeyboardModifiers.Control));

    public static ShortcutDefinition Paste { get; } = new(
        "editor.paste",
        "Paste",
        "Paste editor objects from the clipboard",
        new KeyboardGesture(SeVirtualKey.V, KeyboardModifiers.Control));

    public static ShortcutDefinition Undo { get; } = new(
        "editor.undo",
        "Undo",
        "Undo the most recent editor change",
        new KeyboardGesture(SeVirtualKey.Z, KeyboardModifiers.Control));

    public static ShortcutDefinition Redo { get; } = new(
        "editor.redo",
        "Redo",
        "Redo the most recently undone editor change",
        new KeyboardGesture(SeVirtualKey.Z, KeyboardModifiers.Control | KeyboardModifiers.Shift));

    private static readonly IReadOnlyList<ShortcutDefinition> Definitions = Array.AsReadOnly<ShortcutDefinition>(
    [
        Copy,
        Cut,
        Paste,
        Undo,
        Redo,
    ]);

    public IReadOnlyList<ShortcutDefinition> Shortcuts => Definitions;
}
