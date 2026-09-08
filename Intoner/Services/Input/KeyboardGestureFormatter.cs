using Dalamud.Game.ClientState.Keys;
using FFXIVClientStructs.FFXIV.Client.System.Input;

namespace Intoner.Services.Input
{
    /// <summary> formats keyboard gestures, keys, and modifiers for display </summary>
    internal static class KeyboardGestureFormatter
    {
        public static string Format(KeyboardGesture gesture)
        {
            string modifiers = Format(gesture.Modifiers);
            string key = Format(gesture.Key);
            if (modifiers.Length == 0)
            {
                return key;
            }

            return key.Length == 0 ? modifiers : $"{modifiers} + {key}";
        }

        public static string Format(KeyboardModifiers modifiers)
            => modifiers switch
            {
                KeyboardModifiers.None => string.Empty,
                KeyboardModifiers.Control => "Ctrl",
                KeyboardModifiers.Shift => "Shift",
                KeyboardModifiers.Alt => "Alt",
                KeyboardModifiers.Control | KeyboardModifiers.Shift => "Ctrl + Shift",
                KeyboardModifiers.Control | KeyboardModifiers.Alt => "Ctrl + Alt",
                KeyboardModifiers.Shift | KeyboardModifiers.Alt => "Shift + Alt",
                KeyboardModifiers.Control | KeyboardModifiers.Shift | KeyboardModifiers.Alt => "Ctrl + Shift + Alt",
                _ => throw new ArgumentOutOfRangeException(nameof(modifiers)),
            };

        public static string Format(SeVirtualKey key)
            => Format((VirtualKey)key);

        public static string Format(VirtualKey key)
            => key switch
            {
                VirtualKey.NO_KEY => string.Empty,
                VirtualKey.CONTROL => "Ctrl",
                VirtualKey.LCONTROL => "Left Ctrl",
                VirtualKey.RCONTROL => "Right Ctrl",
                VirtualKey.LMENU => "Left Alt",
                VirtualKey.RMENU => "Right Alt",
                _ => key.GetFancyName(),
            };
    }
}
