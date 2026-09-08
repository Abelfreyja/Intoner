using Dalamud.Interface.Textures;
using Dalamud.Plugin.Services;
using Intoner.Objects.Utils;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using System.Diagnostics.CodeAnalysis;

namespace Intoner.Objects.UI.Services;

internal sealed record TerritoryArtwork(
    uint TerritoryId,
    string Name,
    string TexturePath,
    ISharedImmediateTexture? Texture);

internal sealed class TerritoryArtworkService
{
    private readonly IDataManager _gameData;
    private readonly ITextureProvider _textureProvider;
    private readonly ExcelSheet<TerritoryType>? _territories;
    private readonly ExcelSheet<PlaceName>? _placeNames;
    private readonly Dictionary<uint, TerritoryArtwork?> _artwork = [];

    public TerritoryArtworkService(
        IDataManager gameData,
        ITextureProvider textureProvider)
    {
        _gameData = gameData;
        _textureProvider = textureProvider;
        _territories = gameData.GetExcelSheet<TerritoryType>(gameData.Language);
        _placeNames = gameData.GetExcelSheet<PlaceName>(gameData.Language);
    }

    public bool TryGet(uint territoryId, [NotNullWhen(true)] out TerritoryArtwork? artwork)
    {
        if (!_artwork.TryGetValue(territoryId, out artwork))
        {
            artwork = Resolve(territoryId);
            _artwork.Add(territoryId, artwork);
        }

        return artwork is not null;
    }

    private TerritoryArtwork? Resolve(uint territoryId)
    {
        if (territoryId == 0
         || _territories is null
         || !_territories.TryGetRow(territoryId, out TerritoryType territory))
        {
            return null;
        }

        ObjectTerritoryMetadata metadata = ObjectTerritoryMetadataUtility.BuildFromTerritory(territory, _placeNames);
        string fileName = territory.LoadingImage.ValueNullable?.FileName.ToString() ?? string.Empty;
        string texturePath = ResolveTexturePath(fileName);
        return new TerritoryArtwork(
            territoryId,
            metadata.TerritoryName,
            texturePath,
            string.IsNullOrEmpty(texturePath) ? null : _textureProvider.GetFromGame(texturePath));
    }

    private string ResolveTexturePath(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return string.Empty;
        }

        string path = $"ui/loadingimage/{fileName}_hr1.tex";
        if (!_gameData.FileExists(path))
        {
            path = $"ui/loadingimage/{fileName}.tex";
        }

        return _gameData.FileExists(path) ? path : string.Empty;
    }
}
