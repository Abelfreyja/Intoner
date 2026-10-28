namespace Intoner.Services
{
    internal enum IntonerCommandKind
    {
        Help,
        WindowToggle,
        WindowOpen,
        WindowClose,
        Settings,
        LayoutList,
        LayoutEnable,
        LayoutDisable,
        LayoutToggle,
        LayoutDestination,
        Undo,
        Redo,
    }

    internal readonly record struct IntonerCommand(IntonerCommandKind Kind, string Argument = "")
    {
        public const string Name = "/intoner";

        internal enum ArgumentKind { None, Layout, OptionalLayout, Page, HelpTopic }

        internal sealed record Definition(IntonerCommandKind Kind, string Path, ArgumentKind Argument, string Description)
        {
            public string[] Aliases { get; init; } = [];

            public string Arguments => Argument switch
            {
                ArgumentKind.Layout => " <name or ID>",
                ArgumentKind.OptionalLayout => " [name or ID|unassigned]",
                ArgumentKind.Page => " [page]",
                ArgumentKind.HelpTopic => " [command group]",
                _ => string.Empty,
            };

            public string Usage => $"{Name} {Path}{Arguments}";
        }

        private static readonly Definition[] Definitions =
        [
            new(IntonerCommandKind.Settings, "settings", ArgumentKind.None, "Open the settings."),
            new(IntonerCommandKind.Undo, "undo", ArgumentKind.None, "Undo one action."),
            new(IntonerCommandKind.Redo, "redo", ArgumentKind.None, "Redo one action."),
            new(IntonerCommandKind.Help, "help", ArgumentKind.HelpTopic, "Show command help.") { Aliases = ["?"] },
            new(IntonerCommandKind.WindowOpen, "window on", ArgumentKind.None, "Open the window."),
            new(IntonerCommandKind.WindowClose, "window off", ArgumentKind.None, "Close the window."),
            new(IntonerCommandKind.WindowToggle, "window toggle", ArgumentKind.None, $"Toggle window. Same as {Name}.") { Aliases = ["", "window"] },
            new(IntonerCommandKind.LayoutList, "layout list", ArgumentKind.Page, "List layouts and their IDs."),
            new(IntonerCommandKind.LayoutEnable, "layout enable", ArgumentKind.Layout, "Enable a layout."),
            new(IntonerCommandKind.LayoutDisable, "layout disable", ArgumentKind.Layout, "Disable a layout."),
            new(IntonerCommandKind.LayoutToggle, "layout toggle", ArgumentKind.Layout, "Toggle a layout."),
            new(IntonerCommandKind.LayoutDestination, "layout destination", ArgumentKind.OptionalLayout, "Show or set a destination. 'unassigned' clears it."),
        ];

        private static readonly Dictionary<string, Definition> Routes = Definitions.SelectMany(
            static definition => definition.Aliases.Prepend(definition.Path),
            static (definition, path) => KeyValuePair.Create(path, definition))
            .ToDictionary(static route => route.Key, static route => route.Value, StringComparer.OrdinalIgnoreCase);

        public static IEnumerable<Definition> GetHelp(string topic = "")
            => Definitions.Where(definition => topic.Length == 0
                || definition.Path.StartsWith(topic + " ", StringComparison.OrdinalIgnoreCase));

        public static string GetUsage(IntonerCommandKind kind)
            => GetDefinition(kind).Usage;

        public static bool TryParse(string arguments, out IntonerCommand command, out string error)
        {
            string path = TakeWord(arguments, out string rest);
            string topic = string.Empty;

            while (IsGroup(path) && (rest.Length > 0 || FindDefinition(path) is null))
            {
                topic = path;
                string action = TakeWord(rest, out rest);

                if (action.Length == 0 || FindDefinition(action)?.Kind == IntonerCommandKind.Help)
                {
                    path = GetDefinition(IntonerCommandKind.Help).Path;
                    rest = rest.Length == 0 ? topic : $"{topic} {rest}";
                }
                else
                {
                    path = $"{topic} {action}";
                }
            }

            Definition? definition = FindDefinition(path);

            if (definition is not null && ReadArgument(definition.Argument, rest) is { } argument)
            {
                command = new(definition.Kind, argument);
                error = string.Empty;
                return true;
            }

            command = default;
            error = $"Invalid command. Use {Name} {GetDefinition(IntonerCommandKind.Help).Path}{(topic.Length == 0 ? string.Empty : $" {topic}")}.";
            return false;
        }

        private static string? ReadArgument(ArgumentKind kind, string text)
        {
            if (kind is ArgumentKind.Layout or ArgumentKind.OptionalLayout && text.StartsWith('"'))
            {
                if (text.Length < 2 || !text.EndsWith('"'))
                {
                    return null;
                }

                text = text[1..^1].Trim();

                if (text.Length == 0)
                {
                    return null;
                }
            }

            if (kind == ArgumentKind.HelpTopic)
            {
                text = text.ToLowerInvariant();
            }

            return kind switch
            {
                ArgumentKind.None => text.Length == 0 ? text : null,
                ArgumentKind.Layout => text.Length > 0 ? text : null,
                ArgumentKind.HelpTopic => text.Length == 0 || IsGroup(text) ? text : null,
                ArgumentKind.OptionalLayout or ArgumentKind.Page => text,
                _ => throw new ArgumentOutOfRangeException(nameof(kind)),
            };
        }

        private static bool IsGroup(string path)
            => path.Length > 0 && Routes.Keys.Any(route => route.StartsWith(path + " ", StringComparison.OrdinalIgnoreCase));

        private static Definition GetDefinition(IntonerCommandKind kind)
            => Definitions.Single(definition => definition.Kind == kind);

        private static Definition? FindDefinition(string path)
            => Routes.GetValueOrDefault(path);

        private static string TakeWord(string text, out string rest)
        {
            string[] parts = text.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            rest = parts.Length == 2 ? parts[1] : string.Empty;
            return parts.Length == 0 ? string.Empty : parts[0].ToLowerInvariant();
        }
    }
}
