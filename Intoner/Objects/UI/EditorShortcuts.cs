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

    public static ShortcutDefinition Group { get; } = new(
        "editor.group",
        "Group",
        "Group the selected editor objects",
        new KeyboardGesture(SeVirtualKey.G, KeyboardModifiers.Control));

    public static ShortcutDefinition Ungroup { get; } = new(
        "editor.ungroup",
        "Ungroup",
        "Ungroup the selected editor groups",
        new KeyboardGesture(SeVirtualKey.G, KeyboardModifiers.Control | KeyboardModifiers.Shift));

    public static ShortcutDefinition ToggleTransformSpace { get; } = new(
        "editor.toggleTransformSpace",
        "Toggle Transform Space",
        "Toggle bounds and gizmo transforms between world and local space.",
        new KeyboardGesture(SeVirtualKey.X));

    public static ShortcutDefinition InvertScaleAnchor { get; } = new(
        "editor.invertScaleAnchor",
        "Anchored Scaling Override",
        "Temporarily switch between anchored and centered scaling.",
        new KeyboardGesture(SeVirtualKey.MENU));

    public static ShortcutDefinition InvertUniformScaling { get; } = new(
        "editor.invertUniformScaling",
        "Uniform Scaling",
        "Temporarily switch between uniform and individual axis scaling.",
        new KeyboardGesture(SeVirtualKey.F),
        AllowAdditionalModifiers: true);

    private static readonly IReadOnlyList<ShortcutDefinition> Definitions = Array.AsReadOnly<ShortcutDefinition>(
    [
        Copy,
        Cut,
        Paste,
        Undo,
        Redo,
        Group,
        Ungroup,
        ToggleTransformSpace,
        InvertScaleAnchor,
        InvertUniformScaling,
    ]);

    public IReadOnlyList<ShortcutDefinition> Shortcuts => Definitions;
}
