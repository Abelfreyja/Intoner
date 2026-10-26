using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.ImGuiFileDialog;
using Intoner.Objects.Api;
using Intoner.Objects.Models;
using Intoner.Objects.Runtime;
using Intoner.Objects.UI.Components;
using Intoner.Objects.UI.Services;
using Intoner.Objects.Utils;
using Intoner.Services.Loading;
using Intoner.UI;
using Microsoft.Extensions.Logging;
using System.Numerics;

namespace Intoner.Objects.UI
{
    /// <summary> owns layout editor commands, dialogs and file operations </summary>
    internal sealed class LayoutActions : IAsyncDisposable
    {
        public const int NameMaxLength = 128;

        private readonly ILogger<LayoutActions>   _logger;
        private readonly IObjectLayoutManager     _layouts;
        private readonly IObjectLayoutFileService _files;
        private readonly IObjectManager           _objects;
        private readonly FileDialogManager        _dialogs;
        private readonly EditorInteraction        _interaction;
        private readonly IObjectWorkspaceHistory  _history;

        private readonly BackgroundOperation<ObjectLayoutTransferResult> _operation;
        private string _directory = string.Empty;

        public Guid? SelectedLayoutId { get; set; }
        public string StatusMessage { get; private set; } = string.Empty;
        public bool StatusIsError { get; private set; }
        public bool IsBusy => _operation.IsBusy;

        public LayoutActions(ILogger<LayoutActions> logger, IObjectLayoutManager layouts, IObjectLayoutFileService files,
            IObjectManager objects, FileDialogManager dialogs, EditorInteraction interaction, IObjectWorkspaceHistory history)
        {
            _logger      = logger;
            _layouts     = layouts;
            _files       = files;
            _objects     = objects;
            _dialogs     = dialogs;
            _interaction = interaction;
            _history     = history;
            _operation   = new BackgroundOperation<ObjectLayoutTransferResult>(logger);
        }

        public ValueTask DisposeAsync()
            => _operation.DisposeAsync();

        public void RefreshContext(IReadOnlyList<ObjectLayoutSnapshot> layouts)
        {
            if (SelectedLayoutId is { } selectedId && layouts.All(layout => layout.Id != selectedId))
            {
                SelectedLayoutId = null;
            }

            CompleteFileOperation();
        }

        public bool SaveCurrent(string name)
        {
            PersistentMutationResult result = _history.Execute("Save Layout", () => _objects.SaveCurrentObjectsAsLayout(name), static result => result.IsAccepted);

            if (!result.IsAccepted || !result.EntityId.HasValue)
            {
                SetStatus(
                    string.IsNullOrWhiteSpace(result.Message)
                        ? "The layout could not be created."
                        : result.Message,
                    isError: true);
                return false;
            }

            SelectedLayoutId = result.EntityId.Value;
            SetStatus(string.Empty, isError: false);
            _interaction.SelectionChanged(_interaction.Selection.TryClear());
            return true;
        }

        public bool SetCreation(Guid? layoutId)
        {
            PersistentMutationResult result = _history.Execute("Set Creation Destination", () => _objects.SetCreationLayout(layoutId), static result => result.IsAccepted);
            SetStatus(result.IsAccepted ? string.Empty : result.Message, !result.IsAccepted);
            return result.IsAccepted;
        }

        public void SetEnabled(Guid layoutId, bool enabled)
        {
            PersistentMutationResult result = _history.Execute(enabled ? "Enable Layout" : "Disable Layout", () => _objects.SetLayoutEnabled(layoutId, enabled), static result => result.IsAccepted);
            SetStatus(result.IsAccepted ? string.Empty : result.Message, !result.IsAccepted);
        }

        public void Rename(ObjectLayoutSnapshot layout)
        {
            _interaction.OpenDialog(EditorDialog.Request.TextInput(
                "layout-rename",
                "Rename Layout",
                "Rename Layout",
                name => _history.Execute("Rename Layout", () => _layouts.TryRenameLayout(layout.Id, name), static result => result)) with
            {
                Icon = FontAwesomeIcon.Edit,
                InitialValue = layout.Name,
                Placeholder = "layout name",
                FailureMessage = "The layout could not be renamed.",
                MaxLength = NameMaxLength,
                Validate = static name => TextUtility.TrimOrEmpty(name).Length == 0
                    ? "Enter a layout name."
                    : null,
            });
        }

        public void Delete(ObjectLayoutSnapshot layout)
        {
            _interaction.CommitPendingHistory();

            if (!_layouts.TryGetLayout(layout.Id, out ObjectLayoutSnapshot current))
            {
                return;
            }

            _interaction.OpenDialog(EditorDialog.Request.TryConfirmation(
                "layout-delete",
                "Delete Layout",
                "Delete Layout",
                () => Delete(current.Id, current.Revision)) with
            {
                Icon = FontAwesomeIcon.Trash,
                ConfirmIcon = FontAwesomeIcon.Trash,
                Accent = ThemeColors.DimRed,
                Detail = current.Name,
                Description = "Deletes the saved layout and removes its active objects. You can undo this during the current session.",
                FailureMessage = "The layout could not be deleted.",
            });
        }

        private bool Delete(Guid layoutId, long expectedLayoutRevision)
        {
            if (!_history.Execute("Delete Layout", () => _objects.DeleteLayout(layoutId, expectedLayoutRevision), static result => result.IsAccepted).IsAccepted)
            {
                return false;
            }

            if (SelectedLayoutId == layoutId)
            {
                SelectedLayoutId = null;
            }

            _interaction.SelectionChanged(_interaction.Selection.TryClear());
            return true;
        }

        public void Import(ObjectLayoutFileKind fileKind)
            => OpenImport(fileKind, recover: false);

        public void Recover()
            => OpenImport(ObjectLayoutFileKind.ObjectLayout, recover: true);

        private void OpenImport(ObjectLayoutFileKind fileKind, bool recover)
        {
            string dialogTitle = fileKind == ObjectLayoutFileKind.MakePlaceLayout
                ? "Import MakePlace layout from json" : "Import object layout from json";
            if (recover)
            {
                dialogTitle = "Recover object layout from json";
            }

            _dialogs.OpenFileDialog(
                dialogTitle,
                ".json",
                (success, paths) =>
                {
                    if (!success || paths.FirstOrDefault() is not string path || string.IsNullOrWhiteSpace(path))
                    {
                        return;
                    }

                    RememberDirectory(path);

                    if (_operation.TryStart(token => recover
                            ? _files.RecoverLayoutAsync(path, token)
                            : _files.ImportLayoutAsync(path, fileKind, token)))
                    {
                        SetStatus(recover ? "Recovering layout..." : "Importing layout...", isError: false);
                    }
                },
                1,
                ResolveDirectory());
        }

        public void Export(ObjectLayoutSnapshot layout, ObjectLayoutFileKind fileKind)
        {
            string defaultFileName = ObjectLayoutFileUtility.BuildExportFileName(layout.Name);
            string dialogTitle = fileKind == ObjectLayoutFileKind.MakePlaceLayout
                ? "Export MakePlace layout to json"
                : "Export layout to json";
            _dialogs.SaveFileDialog(
                dialogTitle,
                ".json",
                defaultFileName,
                ".json",
                (success, path) =>
                {
                    if (!success || string.IsNullOrWhiteSpace(path))
                    {
                        return;
                    }

                    RememberDirectory(path);

                    if (_operation.TryStart(token => _files.ExportLayoutAsync(layout, path, fileKind, token)))
                    {
                        SetStatus("Exporting layout...", isError: false);
                    }
                },
                ResolveDirectory());
        }

        private void CompleteFileOperation()
        {
            if (!_operation.TryTakeCompleted(out Task<ObjectLayoutTransferResult>? task))
            {
                return;
            }

            try
            {
                ObjectLayoutTransferResult result = task.GetAwaiter().GetResult();

                if (result.Success && result.Layout is { } layout)
                {
                    SelectedLayoutId = layout.Id;
                    _interaction.SelectionChanged(_interaction.Selection.TryClear());
                    if (result.RecoveryNotes.Count > 0)
                    {
                        OpenRecoveryReport(layout.Name, result.RecoveryNotes);
                    }
                }

                SetStatus(result.Message, isError: !result.Success);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Layout file operation failed");
                SetStatus("The layout file operation failed.", isError: true);
            }
        }

        private void OpenRecoveryReport(string name, IReadOnlyList<string> notes)
        {
            EditorDialog.FormContent fields = new(() =>
            {
                using var list = EditorScrollList.Begin("recoveryNotes", new Vector2(0f, EditorLayout.Scaled(240f)));
                if (list)
                {
                    using var wrap = ImRaiiScope.TextWrapPos();
                    foreach (string note in notes)
                    {
                        ImGui.TextUnformatted(note);
                        ImGui.Spacing();
                    }
                }

                return false;
            }, _ => EditorLayout.Scaled(240f), static () => true);
            _interaction.OpenDialog(EditorDialog.Request.Form("layout-recovery-report", "Recovery Details", "Close", fields, static () => true) with
            {
                Icon = FontAwesomeIcon.FileImport,
                Detail = name,
                Description = "The recovered copy is saved.",
            });
        }

        private void SetStatus(string? message, bool isError)
        {
            StatusMessage = TextUtility.TrimOrEmpty(message);
            StatusIsError = isError;
        }

        private string? ResolveDirectory()
        {
            if (Directory.Exists(_directory))
            {
                return _directory;
            }

            string documentsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            return Directory.Exists(documentsDirectory)
                ? documentsDirectory
                : null;
        }

        private void RememberDirectory(string path)
        {
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
            {
                _directory = directory;
            }
        }
    }
}
