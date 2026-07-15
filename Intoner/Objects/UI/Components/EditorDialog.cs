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
        public Vector4 Accent { get; init; } = EditorColors.AccentPurple;
        public string InitialValue { get; init; } = string.Empty;
        public string Placeholder { get; init; } = string.Empty;
        public string Detail { get; init; } = string.Empty;
        public string Description { get; init; } = string.Empty;
        public string FailureMessage { get; init; } = "The change could not be applied.";
        public int MaxLength { get; init; } = 256;
        public Func<string, string?>? Validate { get; init; }
        public SecondaryAction? Secondary { get; init; }

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

        public bool TrySubmit(string input)
            => _submit(input);
    }

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
                ImGui.GetColorU32(EditorColors.Color(0f, 0f, 0f, 0.58f)),
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
            using (ImRaii.PushColor(ImGuiCol.ChildBg, EditorColors.WindowBg with { W = 0.98f }))
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
        else
        {
            submitted = allowSubmitShortcut && ImGui.IsKeyPressed(ImGuiKey.Enter);
        }

        string? visibleError = ResolveVisibleError(validationError);
        if (!string.IsNullOrEmpty(visibleError))
        {
            using var wrap = ImRaiiScope.TextWrapPos();
            ImGui.TextColored(EditorColors.DimRed, visibleError);
        }

        if (submitted && validationError is null && TrySubmit(request))
        {
            return;
        }

        if (ImGui.IsKeyPressed(ImGuiKey.Escape))
        {
            Dismiss();
            return;
        }

        DrawActions(request, !request.HasTextInput || validationError is null);
    }

    private void DrawActions(Request request, bool canSubmit)
    {
        ImGui.Separator();

        float scale = ImGuiHelpers.GlobalScale;
        float height = ButtonHeight * scale;
        float spacing = ImGui.GetStyle().ItemSpacing.X;
        float cancelWidth = ResolveButtonWidth("Cancel", 80f * scale);
        float confirmWidth = ResolveButtonWidth(request.ConfirmLabel, 110f * scale);
        float rightWidth = cancelWidth + spacing + confirmWidth;

        if (request.Secondary is { } secondary)
        {
            float secondaryWidth = ResolveButtonWidth(secondary.Label, 90f * scale);
            if (DrawActionButton("secondary", secondary.Icon, secondary.Label, secondary.Accent, new Vector2(secondaryWidth, height)))
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
        if (DrawActionButton("cancel", FontAwesomeIcon.Times, "Cancel", EditorColors.AccentGrey, new Vector2(cancelWidth, height)))
        {
            Dismiss();
            return;
        }

        ImGui.SameLine();
        if (DrawActionButton(
                "confirm",
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
        if (!string.IsNullOrEmpty(visibleError))
        {
            height += ImGui.CalcTextSize(visibleError, false, innerWidth).Y + spacing;
        }

        height += scale + spacing + (ButtonHeight * scale) + (12f * scale);
        float maximumHeight = MathF.Max(1f, availableSize.Y - (margin * 2f));
        return new Vector2(width, MathF.Min(height, maximumHeight));
    }

    private static float ResolveButtonWidth(string label, float minimumWidth)
        => MathF.Max(minimumWidth, ImGui.CalcTextSize(label).X + (34f * ImGuiHelpers.GlobalScale));

    private static bool DrawActionButton(
        string id,
        FontAwesomeIcon icon,
        string label,
        Vector4 accent,
        Vector2 size,
        bool enabled = true)
    {
        Vector4 fill = EditorColors.ButtonDefault with { W = 0.88f };
        using var disabled = ImRaii.Disabled(!enabled);
        using var button = ImRaii.PushColor(ImGuiCol.Button, fill);
        using var hovered = ImRaii.PushColor(ImGuiCol.ButtonHovered, accent with { W = 0.22f });
        using var active = ImRaii.PushColor(ImGuiCol.ButtonActive, accent with { W = 0.32f });
        using var border = ImRaii.PushColor(ImGuiCol.Border, accent with { W = 0.72f });
        using var borderSize = ImRaii.PushStyle(ImGuiStyleVar.FrameBorderSize, ImGuiHelpers.GlobalScale);
        using var rounding = ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, 6f * ImGuiHelpers.GlobalScale);

        bool clicked = ImGui.Button($"##editorDialog:{id}", size);
        Vector2 min = ImGui.GetItemRectMin();
        Vector2 max = ImGui.GetItemRectMax();
        string iconText = icon.ToIconString();
        Vector2 iconSize;
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            iconSize = ImGui.CalcTextSize(iconText);
        }

        Vector2 labelSize = ImGui.CalcTextSize(label);
        float spacing = 6f * ImGuiHelpers.GlobalScale;
        float contentWidth = iconSize.X + spacing + labelSize.X;
        float startX = min.X + MathF.Max(0f, ((max.X - min.X) - contentWidth) * 0.5f);
        var drawList = ImGui.GetWindowDrawList();
        uint textColor = ImGui.GetColorU32(enabled ? ImGuiCol.Text : ImGuiCol.TextDisabled);
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            drawList.AddText(
                new Vector2(startX, min.Y + ((max.Y - min.Y - iconSize.Y) * 0.5f)),
                textColor,
                iconText);
        }

        drawList.AddText(
            new Vector2(startX + iconSize.X + spacing, min.Y + ((max.Y - min.Y - labelSize.Y) * 0.5f)),
            textColor,
            label);
        return clicked;
    }
}
