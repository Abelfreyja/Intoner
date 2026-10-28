using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Services;
using Intoner.Objects.Models;
using Intoner.Objects.Runtime;
using Intoner.Scene;
using Intoner.Services;
using System.Globalization;

namespace Intoner.Objects.UI.Services
{
    internal sealed class EditorChatCommands(
        IChatGui chat,
        IObjectLayoutManager layouts,
        IObjectWorkspaceHistory workspaceHistory,
        EditorInteraction interaction,
        ISceneHistoryManager history,
        IIntonerMediator mediator,
        ObjectStateLock stateLock)
    {
        private const int LayoutsPerPage = 5;

        public void Execute(IntonerCommand command)
        {
            switch (command.Kind)
            {
                case IntonerCommandKind.WindowToggle:
                    mediator.Publish(IntonerMainWindowRequest.Toggle);
                    return;
                case IntonerCommandKind.WindowOpen:
                    mediator.Publish(new IntonerMainWindowRequest(IntonerMainWindowRequestKind.Open));
                    return;
                case IntonerCommandKind.WindowClose:
                    mediator.Publish(new IntonerMainWindowRequest(IntonerMainWindowRequestKind.Close));
                    return;
                case IntonerCommandKind.Settings:
                    mediator.Publish(IntonerMainWindowRequest.OpenSettings);
                    return;
                case IntonerCommandKind.LayoutList:
                    ListLayouts(command.Argument);
                    return;
                case IntonerCommandKind.LayoutDestination when command.Argument.Length == 0:
                    ShowDestination();
                    return;
                case IntonerCommandKind.Undo or IntonerCommandKind.Redo
                    or IntonerCommandKind.LayoutEnable or IntonerCommandKind.LayoutDisable
                    or IntonerCommandKind.LayoutToggle or IntonerCommandKind.LayoutDestination:
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(command));
            }

            if (interaction.Dialog.IsOpen)
            {
                chat.PrintIntonerError("Close the dialog before making changes to the workspace.");
                return;
            }

            using Lock.Scope scope = stateLock.Value.EnterScope();

            if (command.Kind is IntonerCommandKind.Undo or IntonerCommandKind.Redo)
            {
                ReplayHistory(command.Kind == IntonerCommandKind.Undo);
            }
            else
            {
                ChangeLayout(command);
            }
        }

        private void ReplayHistory(bool undo)
        {
            if (undo ? interaction.TryUndoHistory() : interaction.TryRedoHistory())
            {
                chat.PrintIntoner(new SeStringBuilder().AddUiForeground(undo ? "Undo" : "Redo", IntonerChat.SuccessColor)
                    .AddText(" complete.").Build());
            }
            else
            {
                chat.PrintIntonerError(history.LastResult.Message);
            }
        }

        private void ChangeLayout(IntonerCommand command)
        {
            if (command.Kind == IntonerCommandKind.LayoutDestination
                && string.Equals(command.Argument, "unassigned", StringComparison.OrdinalIgnoreCase))
            {
                Report(workspaceHistory.SetCreationLayout(null), "New objects will be placed in Unassigned.");
                return;
            }

            if (!TryFindLayout(command.Argument, out ObjectLayoutSnapshot layout))
            {
                return;
            }

            if (command.Kind == IntonerCommandKind.LayoutDestination)
            {
                Report(workspaceHistory.SetCreationLayout(layout.Id), $"New objects will be placed in \"{layout.Name}\".");
                return;
            }

            bool enabled = command.Kind switch
            {
                IntonerCommandKind.LayoutEnable => true,
                IntonerCommandKind.LayoutDisable => false,
                IntonerCommandKind.LayoutToggle => !layouts.IsLayoutEnabled(layout.Id),
                _ => throw new ArgumentOutOfRangeException(nameof(command)),
            };
            Report(workspaceHistory.SetLayoutEnabled(layout.Id, enabled),
                $"Layout \"{layout.Name}\" {(enabled ? "enabled" : "disabled")}.");
        }

        private void ShowDestination()
        {
            using Lock.Scope scope = stateLock.Value.EnterScope();
            SeStringBuilder message = new SeStringBuilder().AddUiForeground("Placement destination", IntonerChat.AccentColor).AddText(": ");

            if (layouts.GetCreationLayoutId() is { } id)
            {
                if (!layouts.TryGetLayout(id, out ObjectLayoutSnapshot layout))
                {
                    chat.PrintIntonerError("The placement destination was not found.");
                    return;
                }

                message.AddUiForeground(layout.Name, IntonerChat.ValueColor).AddText($"\n  ID: {layout.Id:D}");
            }
            else
            {
                message.AddUiForeground("Unassigned", IntonerChat.ValueColor);
            }

            chat.PrintIntoner(message.Build());
        }

        private void ListLayouts(string argument)
        {
            int page = 1;

            if (argument.Length > 0 && (!int.TryParse(argument, NumberStyles.None, CultureInfo.InvariantCulture, out page) || page < 1))
            {
                chat.PrintIntonerError($"Enter a positive page number. {IntonerCommand.GetUsage(IntonerCommandKind.LayoutList)}");
                return;
            }

            using Lock.Scope scope = stateLock.Value.EnterScope();
            IReadOnlyList<ObjectLayoutSnapshot> saved = layouts.GetLayouts();

            if (saved.Count == 0)
            {
                chat.PrintIntoner("No saved layouts.");
                return;
            }

            int pages = (saved.Count - 1) / LayoutsPerPage + 1;

            if (page > pages)
            {
                chat.PrintIntonerError($"There are {pages} pages. Use {IntonerCommand.GetUsage(IntonerCommandKind.LayoutList)}.");
                return;
            }

            Guid? destination = layouts.GetCreationLayoutId();
            SeStringBuilder message = new SeStringBuilder().AddUiForeground("Saved layouts", IntonerChat.AccentColor)
                .AddText($" · Page {page}/{pages}");

            foreach (ObjectLayoutSnapshot layout in saved.Skip((page - 1) * LayoutsPerPage).Take(LayoutsPerPage))
            {
                message.AddText("\n").AddUiForeground(layout.Name, IntonerChat.ValueColor).AddText(" · ");

                if (layouts.IsLayoutEnabled(layout.Id))
                {
                    message.AddUiForeground("Enabled", IntonerChat.SuccessColor);
                }
                else
                {
                    message.AddText("Disabled");
                }

                if (destination == layout.Id)
                {
                    message.AddText(" · ").AddUiForeground("Placement destination", IntonerChat.AccentColor);
                }

                message.AddText($"\n  ID: {layout.Id:D}");
            }

            chat.PrintIntoner(message.Build());
        }

        private bool TryFindLayout(string target, out ObjectLayoutSnapshot layout)
        {
            if (Guid.TryParse(target, out Guid id))
            {
                if (layouts.TryGetLayout(id, out layout))
                {
                    return true;
                }
            }
            else
            {
                ObjectLayoutSnapshot[] matches = layouts.GetLayouts()
                    .Where(candidate => string.Equals(candidate.Name, target, StringComparison.OrdinalIgnoreCase)).ToArray();

                if (matches.Length == 1)
                {
                    layout = matches[0];
                    return true;
                }

                if (matches.Length > 1)
                {
                    chat.PrintIntonerError($"More than one layout is named \"{target}\". Use its ID from {IntonerCommand.GetUsage(IntonerCommandKind.LayoutList)}.");
                    layout = null!;
                    return false;
                }
            }

            chat.PrintIntonerError($"Layout \"{target}\" was not found. Use {IntonerCommand.GetUsage(IntonerCommandKind.LayoutList)}.");
            layout = null!;
            return false;
        }

        private void Report(PersistentMutationResult result, string success)
        {
            if (result.IsSuccess)
            {
                chat.PrintIntoner(success);
            }
            else
            {
                chat.PrintIntonerError(result.Message);
            }
        }
    }
}
