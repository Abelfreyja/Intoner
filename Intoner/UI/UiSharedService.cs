using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.GameFonts;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.ManagedFontAtlas;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Intoner.UI.Theme;
using Microsoft.Extensions.Logging;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Intoner.UI;

internal sealed class UiSharedService : IDisposable
{
    public const string TooltipSeparator = "--SEP--";

    private static readonly (bool ItemHq, bool HiRes)[] IconLookupOrders =
    [
        (false, true),
        (true,  true),
        (false, false),
        (true,  false),
    ];

    private readonly ILogger<UiSharedService> _logger;
    private readonly ITextureProvider _textureProvider;

    public UiSharedService(
        ILogger<UiSharedService> logger,
        IDalamudPluginInterface pluginInterface,
        ITextureProvider textureProvider,
        FileDialogManager fileDialogManager)
    {
        _logger = logger;
        _textureProvider = textureProvider;
        FileDialogManager = fileDialogManager;
        UidFont = pluginInterface.UiBuilder.FontAtlas.NewDelegateFontHandle(e =>
        {
            e.OnPreBuild(tk => tk.AddDalamudAssetFont(Dalamud.DalamudAsset.NotoSansCjkMedium, new()
            {
                SizePx = 35,
            }));
        });
        MediumFont = pluginInterface.UiBuilder.FontAtlas.NewDelegateFontHandle(e =>
        {
            e.OnPreBuild(tk => tk.AddDalamudAssetFont(Dalamud.DalamudAsset.NotoSansCjkMedium, new()
            {
                SizePx = 22,
            }));
        });
        GameFont = pluginInterface.UiBuilder.FontAtlas.NewGameFontHandle(new(GameFontFamilyAndSize.Axis12));
        IconFont = pluginInterface.UiBuilder.IconFontFixedWidthHandle;
    }

    public FileDialogManager FileDialogManager { get; }
    public IFontHandle GameFont { get; }
    public IFontHandle IconFont { get; }
    public IFontHandle UidFont { get; }
    public IFontHandle MediumFont { get; }

    public void Dispose()
    {
        GameFont.Dispose();
        UidFont.Dispose();
        MediumFont.Dispose();
    }

    public bool TryGetIcon(uint iconId, out IDalamudTextureWrap? wrap)
    {
        foreach (var (itemHq, hiRes) in IconLookupOrders)
        {
            if (TryGetIconWithLookup(iconId, itemHq, hiRes, out wrap))
            {
                return true;
            }
        }

        foreach (var (itemHq, hiRes) in IconLookupOrders)
        {
            if (!_textureProvider.TryGetIconPath(new GameIconLookup(iconId, itemHq, hiRes), out var path)
             || string.IsNullOrEmpty(path))
            {
                continue;
            }

            if (TryLoadGameTexture(path, iconId, out wrap))
            {
                return true;
            }
        }

        foreach (var hiRes in new[] { true, false })
        {
            if (TryLoadGameTexture(BuildIconPath(iconId, hiRes), iconId, out wrap))
            {
                return true;
            }
        }

        wrap = null;
        return false;
    }

    public IDalamudTextureWrap LoadImage(byte[] imageData)
    {
        return _textureProvider.CreateFromImageAsync(imageData).Result;
    }

    private bool TryGetIconWithLookup(uint iconId, bool itemHq, bool hiRes, out IDalamudTextureWrap? wrap)
    {
        try
        {
            var icon = _textureProvider.GetFromGameIcon(new GameIconLookup(iconId, itemHq, hiRes));
            if (icon.TryGetWrap(out var texture, out _))
            {
                wrap = texture;
                return true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogTrace(ex, "failed to load icon {IconId} hq={ItemHq} hiRes={HiRes}", iconId, itemHq, hiRes);
        }

        wrap = null;
        return false;
    }

    private bool TryLoadGameTexture(string path, uint iconId, out IDalamudTextureWrap? wrap)
    {
        try
        {
            var reference = _textureProvider.GetFromGame(path);
            if (reference.TryGetWrap(out var texture, out _))
            {
                wrap = texture;
                return true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogTrace(ex, "failed to load icon {IconId} from {Path}", iconId, path);
        }

        wrap = null;
        return false;
    }

    private static string BuildIconPath(uint iconId, bool hiRes)
    {
        var folder = iconId - iconId % 1000;
        var basePath = $"ui/icon/{folder:000000}/{iconId:000000}";
        return hiRes ? $"{basePath}_hr1.tex" : $"{basePath}.tex";
    }

    public static void DrawAccentTooltip(Action body)
        => DrawAccentTooltipCore(body);

    public static void DrawAccentTooltip(
        Action body,
        Vector4 accent,
        Vector2? windowPaddingOverride = null,
        Vector2? itemSpacingOverride = null,
        float unscaledFixedWidth = 0f)
        => DrawAccentTooltipCore(body, accent, windowPaddingOverride, itemSpacingOverride, unscaledFixedWidth);

    public static void DrawAccentTooltipText(
        string text,
        Vector4? accentOverride = null,
        float wrapEms = 0f,
        bool useColoredSeparator = false,
        float unscaledFixedWidth = 0f)
        => DrawAccentTooltipCore(
            () => RenderTooltipText(text, wrapEms, useColoredSeparator, accentOverride),
            accentOverride,
            unscaledFixedWidth: unscaledFixedWidth);

    public static void AttachToolTip(string text)
    {
        if (!ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            return;
        }

        DrawAccentTooltipCore(() =>
        {
            RenderTooltipText(
                text,
                wrapEms: 35f,
                useColoredSeparator: true,
                separatorColor: UIColors.Get("AccentPurple"));
        });
    }

    public static ComboScope BeginCombo(string label, string previewValue, ImGuiComboFlags flags = ImGuiComboFlags.None)
    {
        IDisposable style = BeginStyledCombo();
        bool isOpen = ImGui.BeginCombo(label, previewValue, flags);
        return new ComboScope(style, isOpen);
    }

    public static bool Combo(string label, ref int currentItem, string[] items, int itemsCount, int popupMaxHeightInItems = -1)
    {
        if (itemsCount <= 0 || items.Length == 0)
        {
            return false;
        }

        int clampedCount = Math.Min(itemsCount, items.Length);
        currentItem = Math.Clamp(currentItem, 0, clampedCount - 1);

        if (popupMaxHeightInItems > 0)
        {
            float rowHeight = ImGui.GetTextLineHeightWithSpacing();
            float popupHeight = (rowHeight * popupMaxHeightInItems) + (ImGui.GetStyle().WindowPadding.Y * 2f);
            ImGui.SetNextWindowSizeConstraints(new Vector2(0f, 0f), new Vector2(float.MaxValue, popupHeight));
        }

        var changed = false;
        using var combo = BeginCombo(label, items[currentItem]);
        if (!combo)
        {
            return false;
        }

        for (var i = 0; i < clampedCount; i++)
        {
            bool isSelected = i == currentItem;
            if (ImGui.Selectable(items[i], isSelected))
            {
                currentItem = i;
                changed = true;
            }

            if (isSelected)
            {
                ImGui.SetItemDefaultFocus();
            }
        }

        return changed;
    }

    public static void ColoredSeparator(Vector4? color = null, float thickness = 1f, float indent = 0f)
    {
        var drawList = ImGui.GetWindowDrawList();
        var min = ImGui.GetCursorScreenPos();
        var max = new Vector2(min.X + ImGui.GetContentRegionAvail().X, min.Y);

        min.X += indent;
        max.X -= indent;

        drawList.AddLine(
            min,
            new Vector2(max.X, min.Y),
            ImGui.GetColorU32(color ?? ImGuiColors.DalamudGrey),
            thickness * ImGuiHelpers.GlobalScale);

        ImGui.Dummy(new Vector2(0f, thickness * ImGuiHelpers.GlobalScale));
    }

    private static void RenderTooltipText(
        string text,
        float wrapEms,
        bool useColoredSeparator,
        Vector4? separatorColor = null)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        if (wrapEms > 0f)
        {
            ImGui.PushTextWrapPos(ImGui.GetFontSize() * wrapEms);
        }

        var sections = SplitTooltipSections(text);
        for (var i = 0; i < sections.Length; i++)
        {
            var section = sections[i];
            if (string.IsNullOrEmpty(section))
            {
                continue;
            }

            ImGui.TextUnformatted(section);

            if (i >= sections.Length - 1)
            {
                continue;
            }

            if (useColoredSeparator)
            {
                ColoredSeparator(separatorColor ?? UIColors.Get("AccentPurple"), 2f);
            }
            else
            {
                ImGui.Separator();
            }
        }

        if (wrapEms > 0f)
        {
            ImGui.PopTextWrapPos();
        }
    }

    private static string[] SplitTooltipSections(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        return text.Contains(TooltipSeparator, StringComparison.Ordinal)
            ? text.Split([TooltipSeparator], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [text.Trim()];
    }

    private static void DrawAccentTooltipCore(
        Action body,
        Vector4? accentOverride = null,
        Vector2? windowPaddingOverride = null,
        Vector2? itemSpacingOverride = null,
        float unscaledFixedWidth = 0f)
    {
        float scale = ImGuiHelpers.GlobalScale;
        Vector4 accent = accentOverride ?? UIColors.Get("AccentPurple");
        Vector2 windowPadding = windowPaddingOverride ?? new Vector2(12f, 10f);
        Vector2 itemSpacing = itemSpacingOverride ?? new Vector2(8f, 6f);
        if (unscaledFixedWidth > 0f)
        {
            float scaledWidth = unscaledFixedWidth * scale;
            ImGui.SetNextWindowSizeConstraints(new Vector2(scaledWidth, 0f), new Vector2(scaledWidth, float.MaxValue));
        }

        using var _rounding = ImRaii.PushStyle(ImGuiStyleVar.WindowRounding, 10f * scale);
        using var _padding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, windowPadding * scale);
        using var _spacing = ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, itemSpacing * scale);
        using var _border = ImRaii.PushStyle(ImGuiStyleVar.WindowBorderSize, 1f * scale);
        using var _borderCol = ImRaii.PushColor(ImGuiCol.Border, new Vector4(accent.X, accent.Y, accent.Z, 0.55f));
        using var tooltip = ImRaii.Tooltip();

        var drawList = ImGui.GetWindowDrawList();
        var pos = ImGui.GetWindowPos();
        var size = ImGui.GetWindowSize();
        drawList.AddRectFilled(pos, new Vector2(pos.X + size.X, pos.Y + (2f * scale)), ImGui.GetColorU32(accent));

        body();
    }

    private static IDisposable BeginStyledCombo()
        => NoopScope.Instance;

    public readonly struct ComboScope : IDisposable
    {
        private readonly IDisposable _style;

        public bool IsOpen { get; }

        public ComboScope(IDisposable style, bool isOpen)
        {
            _style = style;
            IsOpen = isOpen;
        }

        public static implicit operator bool(ComboScope scope)
            => scope.IsOpen;

        public void Dispose()
        {
            if (IsOpen)
            {
                ImGui.EndCombo();
            }

            _style.Dispose();
        }
    }

    private sealed class NoopScope : IDisposable
    {
        public static readonly NoopScope Instance = new();

        public void Dispose()
        { }
    }

    [DllImport("user32.dll")]
    internal static extern short GetKeyState(int nVirtKey);
}
