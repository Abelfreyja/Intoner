using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Intoner.UI;
using System.Numerics;

namespace Intoner.Objects.UI.Components;

internal sealed class EditorDialog
{
    private const string DialogId = "##editorDialog";
    private const float PreferredWidth = 420f;
    private const float ContentMargin = 12f;
    private const float ButtonHeight = 30f;

    private Request? _request;
    private string _input = string.Empty;
    private string _submissionError = string.Empty;
    private bool _focusInput;
    private bool _suppressSubmitShortcut;
    private bool _fieldPopupOpen;

    internal sealed record Request
    {
        private readonly Func<string, bool> _submit;

        private Request(
            string key,
            string title,
            string confirmLabel,
            bool hasTextInput,
            Func<string, bool> submit)
        {
            Key = key;
            Title = title;
            ConfirmLabel = confirmLabel;
            HasTextInput = hasTextInput;
            _submit = submit;
        }

        public string Key { get; }
        public string Title { get; }
        public string ConfirmLabel { get; }
        public bool HasTextInput { get; }
        public FontAwesomeIcon Icon { get; init; } = FontAwesomeIcon.Pen;
        public FontAwesomeIcon ConfirmIcon { get; init; } = FontAwesomeIcon.Check;
        public Vector4 Accent { get; init; } = ThemeColors.AccentPrimary;
        public string InitialValue { get; init; } = string.Empty;
        public string Placeholder { get; init; } = string.Empty;
        public string Detail { get; init; } = string.Empty;
        public string Description { get; init; } = string.Empty;
        public string FailureMessage { get; init; } = "The change could not be applied.";
        public int MaxLength { get; init; } = 256;
        public Func<string, string?>? Validate { get; init; }
        public SecondaryAction? Secondary { get; init; }
        public FormContent? Fields { get; private init; }

        public static Request TextInput(string key, string title, string confirmLabel, Func<string, bool> submit)
        {
            ArgumentNullException.ThrowIfNull(submit);
            return new Request(key, title, confirmLabel, hasTextInput: true, submit);
        }

        public static Request Confirmation(string key, string title, string confirmLabel, Action confirm)
        {
            ArgumentNullException.ThrowIfNull(confirm);
            return new Request(key, title, confirmLabel, hasTextInput: false, _ =>
            {
                confirm();
                return true;
            });
        }

        public static Request TryConfirmation(string key, string title, string confirmLabel, Func<bool> confirm)
        {
            ArgumentNullException.ThrowIfNull(confirm);
            return new Request(key, title, confirmLabel, hasTextInput: false, _ => confirm());
        }

        public static Request Form(string key, string title, string confirmLabel, FormContent fields, Func<bool> submit)
        {
            ArgumentNullException.ThrowIfNull(fields);
            ArgumentNullException.ThrowIfNull(submit);
            return new Request(key, title, confirmLabel, hasTextInput: false, _ => submit()) { Fields = fields };
        }

        public bool TrySubmit(string input)
            => _submit(input);
    }

    /// <summary> custom fields hosted by the shared dialog header, validation, and actions </summary>
    /// <param name="Draw"> draws the fields and returns whether their values changed </param>
    /// <param name="MeasureHeight"> measures the fields at the available content width, excluding trailing item spacing </param>
    /// <param name="CanSubmit"> gets whether the current field values can be submitted </param>
    internal sealed record FormContent(Func<bool> Draw, Func<float, float> MeasureHeight, Func<bool> CanSubmit);

    internal sealed record SecondaryAction(
        string Label,
        FontAwesomeIcon Icon,
        Vector4 Accent,
        Func<bool> Execute);

    public bool IsOpen => _request is not null;

    public void Open(Request request)
    {
        ArgumentNullException.ThrowIfNull(request);

        _request = request;
        _input = request.InitialValue;
        _submissionError = string.Empty;
        _focusInput = request.HasTextInput;
        _suppressSubmitShortcut = true;
        _fieldPopupOpen = false;
    }

    public void DismissIfCurrent(string key)
    {
        if (_request is not null && string.Equals(_request.Key, key, StringComparison.Ordinal))
        {
            Dismiss();
        }
    }

    public void Dismiss()
    {
        _request = null;
        _input = string.Empty;
        _submissionError = string.Empty;
        _focusInput = false;
        _suppressSubmitShortcut = false;
        _fieldPopupOpen = false;
    }

    public void Draw(EditorOverlayLayer overlayLayer, EditorOverlayArea area)
    {
        Request? request = _request;
        if (request is null)
        {
            return;
        }

        float scale = ImGuiHelpers.GlobalScale;
        Vector2 overlaySize = area.Size;
        if (overlaySize.X <= 1f || overlaySize.Y <= 1f)
        {
            return;
        }

        string? validationError = request.HasTextInput ? request.Validate?.Invoke(_input) : null;
        string? visibleError = ResolveVisibleError(validationError);
        Vector2 dialogSize = ResolveDialogSize(request, overlaySize, visibleError);
        Vector2 dialogPosition = area.Min + ((overlaySize - dialogSize) * 0.5f);

        overlayLayer.DrawClipped(area, drawList =>
            drawList.AddRectFilled(
                area.Min,
                area.Max,
                ImGui.GetColorU32(ThemeColors.Color(0f, 0f, 0f, 0.58f)),
                area.Rounding,
                area.RoundingFlags));
        DrawDialog(request, dialogPosition, dialogSize, scale, validationError, !_suppressSubmitShortcut);
        _suppressSubmitShortcut = false;
    }

    private void DrawDialog(
        Request request,
        Vector2 position,
        Vector2 size,
        float scale,
        string? validationError,
        bool allowSubmitShortcut)
    {
        Vector2 previousCursorPosition = ImGui.GetCursorScreenPos();
        ImGui.SetCursorScreenPos(position);
        try
        {
            using (ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, new Vector2(14f, 12f) * scale))
            using (ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, 7f * scale))
            using (ImRaii.PushStyle(ImGuiStyleVar.ChildBorderSize, scale))
            using (ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, new Vector2(8f, 8f) * scale))
            using (ImRaii.PushColor(ImGuiCol.Border, request.Accent with { W = 0.55f }))
            using (ImRaii.PushColor(ImGuiCol.ChildBg, ThemeColors.WindowBg with { W = 0.98f }))
            using (ImRaii.PushId(request.Key))
            using (var dialog = ImRaii.Child(DialogId, size, true, ImGuiWindowFlags.AlwaysUseWindowPadding))
            {
                if (dialog)
                {
                    DrawHeader(request);
                    DrawBody(request, validationError, allowSubmitShortcut);
                }
            }
        }
        finally
        {
            ImGui.SetCursorScreenPos(previousCursorPosition);
        }
    }

    private static void DrawHeader(Request request)
    {
        using (ImRaii.PushFont(UiBuilder.IconFont))
        using (ImRaii.PushColor(ImGuiCol.Text, request.Accent))
        {
            ImGui.TextUnformatted(request.Icon.ToIconString());
        }

        ImGui.SameLine(0f, 8f * ImGuiHelpers.GlobalScale);
        ImGui.TextUnformatted(request.Title);
        ImGui.Separator();
    }

    private void DrawBody(Request request, string? validationError, bool allowSubmitShortcut)
    {
        if (!string.IsNullOrEmpty(request.Detail))
        {
            EditorTextUtility.ClippedText detail = EditorTextUtility.ClipTextToWidthResult(
                request.Detail,
                ImGui.GetContentRegionAvail().X);
            ImGui.TextDisabled(detail.Text);
            EditorTextUtility.AttachTooltipIfClipped(
                ImGui.GetItemRectMin(),
                ImGui.GetItemRectSize(),
                request.Detail,
                detail.IsClipped);
        }

        if (!string.IsNullOrEmpty(request.Description))
        {
            using var wrap = ImRaiiScope.TextWrapPos();
            ImGui.TextUnformatted(request.Description);
        }

        bool submitted = false;
        // imgui can close a combo before this draw receives its enter or escape key
        bool fieldPopupWasOpen = _fieldPopupOpen
                              || (request.Fields is not null && ImGui.IsPopupOpen(string.Empty, ImGuiPopupFlags.AnyPopupId));
        if (request.HasTextInput)
        {
            if (_focusInput)
            {
                ImGui.SetKeyboardFocusHere();
                _focusInput = false;
            }

            string previousInput = _input;
            ImGui.SetNextItemWidth(-1f);
            bool inputSubmitted = ImGui.InputTextWithHint(
                "##editorDialogInput",
                request.Placeholder,
                ref _input,
                request.MaxLength,
                ImGuiInputTextFlags.EnterReturnsTrue);
            submitted = allowSubmitShortcut && inputSubmitted;
            if (!string.Equals(previousInput, _input, StringComparison.Ordinal))
            {
                _submissionError = string.Empty;
                validationError = request.Validate?.Invoke(_input);
            }
        }
        else if (request.Fields is { } fields)
        {
            if (fields.Draw())
            {
                _submissionError = string.Empty;
            }

            _fieldPopupOpen = ImGui.IsPopupOpen(string.Empty, ImGuiPopupFlags.AnyPopupId);
            submitted = allowSubmitShortcut
                     && !fieldPopupWasOpen
                     && !ImGui.IsAnyItemActive()
                     && !_fieldPopupOpen
                     && ImGui.IsKeyPressed(ImGuiKey.Enter);
        }
        else
        {
            submitted = allowSubmitShortcut && ImGui.IsKeyPressed(ImGuiKey.Enter);
        }

        string? visibleError = ResolveVisibleError(validationError);
        if (!string.IsNullOrEmpty(visibleError))
        {
            using var wrap = ImRaiiScope.TextWrapPos();
            ImGui.TextColored(ThemeColors.DimRed, visibleError);
        }

        bool canSubmit = validationError is null && (request.Fields?.CanSubmit() ?? true);
        if (submitted && canSubmit && TrySubmit(request))
        {
            return;
        }

        if (!fieldPopupWasOpen && ImGui.IsKeyPressed(ImGuiKey.Escape))
        {
            Dismiss();
            return;
        }

        DrawActions(request, canSubmit);
    }

    private void DrawActions(Request request, bool canSubmit)
    {
        ImGui.Separator();

        float scale = ImGuiHelpers.GlobalScale;
        float height = ButtonHeight * scale;
        float spacing = ImGui.GetStyle().ItemSpacing.X;
        float cancelWidth = EditorButton.MeasureWidth(FontAwesomeIcon.Times, "Cancel", 80f);
        float confirmWidth = EditorButton.MeasureWidth(request.ConfirmIcon, request.ConfirmLabel, 110f);
        float rightWidth = cancelWidth + spacing + confirmWidth;

        if (request.Secondary is { } secondary)
        {
            float secondaryWidth = EditorButton.MeasureWidth(secondary.Icon, secondary.Label, 90f);
            if (EditorButton.Draw("dialogSecondary", secondary.Icon, secondary.Label, secondary.Accent, new Vector2(secondaryWidth, height)))
            {
                if (secondary.Execute())
                {
                    Dismiss();
                    return;
                }

                _submissionError = request.FailureMessage;
            }

            ImGui.SameLine();
        }

        float rightStart = ImGui.GetCursorPosX() + MathF.Max(0f, ImGui.GetContentRegionAvail().X - rightWidth);
        ImGui.SetCursorPosX(rightStart);
        if (EditorButton.Draw("dialogCancel", FontAwesomeIcon.Times, "Cancel", ThemeColors.AccentGrey, new Vector2(cancelWidth, height)))
        {
            Dismiss();
            return;
        }

        ImGui.SameLine();
        if (EditorButton.Draw(
                "dialogConfirm",
                request.ConfirmIcon,
                request.ConfirmLabel,
                request.Accent,
                new Vector2(confirmWidth, height),
                canSubmit))
        {
            _ = TrySubmit(request);
        }
    }

    private bool TrySubmit(Request request)
    {
        if (!request.TrySubmit(_input))
        {
            _submissionError = request.FailureMessage;
            return false;
        }

        Dismiss();
        return true;
    }

    private string? ResolveVisibleError(string? validationError)
    {
        if (!string.IsNullOrEmpty(_submissionError))
        {
            return _submissionError;
        }

        return string.IsNullOrEmpty(_input) ? null : validationError;
    }

    private static Vector2 ResolveDialogSize(Request request, Vector2 availableSize, string? visibleError)
    {
        float scale = ImGuiHelpers.GlobalScale;
        float margin = ContentMargin * scale;
        float maximumWidth = MathF.Max(1f, availableSize.X - (margin * 2f));
        float width = MathF.Min(PreferredWidth * scale, maximumWidth);
        float innerWidth = MathF.Max(1f, width - (28f * scale));
        float spacing = 8f * scale;
        float height = (24f * scale)
            + ImGui.GetTextLineHeight()
            + spacing
            + scale
            + spacing;

        if (!string.IsNullOrEmpty(request.Detail))
        {
            height += ImGui.GetTextLineHeight() + spacing;
        }

        if (!string.IsNullOrEmpty(request.Description))
        {
            height += ImGui.CalcTextSize(request.Description, false, innerWidth).Y + spacing;
        }

        if (request.HasTextInput)
        {
            height += ImGui.GetFrameHeight() + spacing;
        }
        else if (request.Fields is { } fields)
        {
            height += fields.MeasureHeight(innerWidth) + spacing;
        }

        if (!string.IsNullOrEmpty(visibleError))
        {
            height += ImGui.CalcTextSize(visibleError, false, innerWidth).Y + spacing;
        }

        height += scale + spacing + (ButtonHeight * scale) + (12f * scale);
        float maximumHeight = MathF.Max(1f, availableSize.Y - (margin * 2f));
        return new Vector2(width, MathF.Min(height, maximumHeight));
    }

}
