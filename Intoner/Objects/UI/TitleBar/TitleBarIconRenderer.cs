using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility;
using Intoner.UI;
using Microsoft.Extensions.Logging;
using System.Numerics;
using System.Reflection;

namespace Intoner.Objects.UI.TitleBar;

internal sealed class TitleBarIconRenderer : IDisposable
{
    private readonly ILogger _logger;
    private readonly UiSharedService _uiSharedService;
    private readonly TitleBarIconOptions _options;

    private IDalamudTextureWrap? _icon;
    private bool _iconLoadAttempted;

    public TitleBarIconRenderer(ILogger logger, UiSharedService uiSharedService, TitleBarIconOptions options)
    {
        _logger = logger;
        _uiSharedService = uiSharedService;
        _options = options;
    }

    public void Dispose()
    {
        _icon?.Dispose();
        _icon = null;
    }

    public float Draw(TitleBarRenderContext context)
    {
        if (!context.HasTitleBar)
        {
            return 0f;
        }

        ImGuiStylePtr style = ImGui.GetStyle();
        float scale = ImGuiHelpers.GlobalScale;
        float cursorX = context.Min.X + style.FramePadding.X + ResolveLeftButtonOffset(context.WindowFlags, style);
        float iconEdge = MathF.Max(1f, MathF.Min(_options.IconSize * scale, context.Height - (_options.VerticalReserve * scale)));
        IDalamudTextureWrap? icon = GetIcon();

        if (icon is not null)
        {
            Vector2 iconSize = FitIcon(icon, iconEdge);
            Vector2 iconMin = new(cursorX, context.Min.Y + ((context.Height - iconSize.Y) * 0.5f));
            context.DrawList.AddImage(icon.Handle, iconMin, iconMin + iconSize);
            cursorX += iconSize.X + (_options.IconGap * scale);
        }

        Vector2 textSize = ImGui.CalcTextSize(_options.Label);
        Vector2 textPos = new(cursorX, context.Min.Y + ((context.Height - textSize.Y) * 0.5f));
        context.DrawList.AddText(textPos, ImGui.GetColorU32(_options.TextColor), _options.Label);
        return textPos.X + textSize.X + (_options.ReserveGap * scale);
    }

    private IDalamudTextureWrap? GetIcon()
    {
        if (_iconLoadAttempted)
        {
            return _icon;
        }

        _iconLoadAttempted = true;
        return LoadEmbeddedIcon();
    }

    private IDalamudTextureWrap? LoadEmbeddedIcon()
    {
        try
        {
            using Stream? stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(_options.EmbeddedResourceName);
            if (stream is null)
            {
                return null;
            }

            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            _icon = _uiSharedService.LoadImage(memory.ToArray());
            return _icon;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to load embedded title bar icon {ResourceName}", _options.EmbeddedResourceName);
            return null;
        }
    }

    private static float ResolveLeftButtonOffset(ImGuiWindowFlags windowFlags, ImGuiStylePtr style)
        => !windowFlags.HasFlag(ImGuiWindowFlags.NoCollapse) && style.WindowMenuButtonPosition == ImGuiDir.Left
            ? ImGui.GetFrameHeight() + style.ItemInnerSpacing.X
            : 0f;

    private static Vector2 FitIcon(IDalamudTextureWrap icon, float edge)
    {
        if (icon.Width <= 0 || icon.Height <= 0)
        {
            return new Vector2(edge);
        }

        if (icon.Width >= icon.Height)
        {
            return new Vector2(edge, edge * icon.Height / icon.Width);
        }

        return new Vector2(edge * icon.Width / icon.Height, edge);
    }
}

internal sealed record TitleBarIconOptions(string Label, string EmbeddedResourceName)
{
    public float IconSize { get; init; } = 16f;
    public float IconGap { get; init; } = 5f;
    public float ReserveGap { get; init; } = 8f;
    public float VerticalReserve { get; init; } = 2f;
    public Vector4 TextColor { get; init; } = EditorColors.Color(1f, 1f, 1f, 0.96f);

    public static TitleBarIconOptions Embedded(string label, string resourceName)
        => new(label, resourceName);
}

