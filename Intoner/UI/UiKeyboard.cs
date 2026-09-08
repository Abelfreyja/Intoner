using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Keys;
using Intoner.Services.Input;

namespace Intoner.UI
{
    /// <summary> reads UI modifiers through ImGui </summary>
    internal static class UiKeyboard
    {
        public static bool AreModifiersDown(KeyboardModifiers modifiers)
        {
            ImGuiIOPtr io = ImGui.GetIO();
            KeyboardModifiers pressed = KeyboardModifiers.None;
            if (io.KeyCtrl)
            {
                pressed |= KeyboardModifiers.Control;
            }

            if (io.KeyShift)
            {
                pressed |= KeyboardModifiers.Shift;
            }

            if (io.KeyAlt)
            {
                pressed |= KeyboardModifiers.Alt;
            }

            return (pressed & modifiers) == modifiers;
        }

        public static string GetKeyLabel(ImGuiKey key)
            => key switch
            {
                ImGuiKey.None => string.Empty,
                ImGuiKey.LeftCtrl => KeyboardGestureFormatter.Format(VirtualKey.LCONTROL),
                ImGuiKey.RightCtrl => KeyboardGestureFormatter.Format(VirtualKey.RCONTROL),
                ImGuiKey.LeftShift => KeyboardGestureFormatter.Format(VirtualKey.LSHIFT),
                ImGuiKey.RightShift => KeyboardGestureFormatter.Format(VirtualKey.RSHIFT),
                ImGuiKey.LeftAlt => KeyboardGestureFormatter.Format(VirtualKey.LMENU),
                ImGuiKey.RightAlt => KeyboardGestureFormatter.Format(VirtualKey.RMENU),
                _ => ImGui.GetKeyName(key),
            };
    }
}
