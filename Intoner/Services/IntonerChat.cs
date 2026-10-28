using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Services;

namespace Intoner.Services
{
    internal static class IntonerChat
    {
        public const ushort AccentColor = 541;
        public const ushort CommandColor = 517;
        public const ushort ValueColor = 31;
        public const ushort SuccessColor = 504;

        public static void PrintIntoner(this IChatGui chat, SeString message)
            => chat.Print(message, "Intoner", AccentColor);

        public static void PrintIntonerError(this IChatGui chat, SeString message)
            => chat.PrintError(message, "Intoner", AccentColor);

        public static SeString Help(string topic)
        {
            SeStringBuilder message = new SeStringBuilder()
                .AddUiForeground(topic.Length == 0 ? "Commands" : $"{Title(topic)} commands", AccentColor)
                .AddText(" · ").AddUiForeground(IntonerCommand.Name, CommandColor).AddUiForeground(" [command]", ValueColor);

            string? previousGroup = null;
            foreach (IntonerCommand.Definition definition in IntonerCommand.GetHelp(topic))
            {
                int separator = definition.Path.LastIndexOf(' ');
                string group = separator < 0 ? string.Empty : definition.Path[..separator];

                if (previousGroup is not null && !string.Equals(previousGroup, group, StringComparison.Ordinal))
                {
                    message.AddText("\n");
                }

                message.AddText("\n》 ").AddUiForeground(definition.Path, CommandColor);
                previousGroup = group;

                if (definition.Arguments.Length > 0)
                {
                    message.AddUiForeground(definition.Arguments, ValueColor);
                }

                message.AddText($" — {definition.Description}");
            }

            return message.Build();
        }

        private static string Title(string text)
            => char.ToUpperInvariant(text[0]) + text[1..];
    }
}
