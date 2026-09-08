using Dalamud.Interface.GameFonts;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.ManagedFontAtlas;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Microsoft.Extensions.Logging;

namespace Intoner.UI;

internal sealed class UiSharedService : IDisposable
{
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

}
