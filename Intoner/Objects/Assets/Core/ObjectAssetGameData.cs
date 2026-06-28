using Dalamud.Plugin.Services;
using Lumina.Data;
using Lumina.Excel;

namespace Intoner.Objects.Assets;

/// <summary> provides the game data surface used by asset discovery and vfx analysis </summary>
internal interface IObjectAssetGameData
{
    /// <summary> gets a language specific excel sheet using the current client language </summary>
    /// <typeparam name="T">the excel row type</typeparam>
    /// <returns>the current language sheet when available</returns>
    ExcelSheet<T>? GetCurrentLanguageExcelSheet<T>()
        where T : struct, IExcelRow<T>;

    /// <summary> gets an excel sheet without forcing a client language lookup </summary>
    /// <typeparam name="T">the excel row type</typeparam>
    /// <returns>the sheet when available</returns>
    ExcelSheet<T>? GetExcelSheet<T>()
        where T : struct, IExcelRow<T>;

    /// <summary> gets whether a normalized game path exists </summary>
    /// <param name="path">the game path to test</param>
    /// <returns>true when the file exists</returns>
    bool FileExists(string path);

    /// <summary> gets an untyped file resource by game path </summary>
    /// <param name="path">the game path to load</param>
    /// <returns>the loaded file resource when available</returns>
    FileResource? GetFile(string path);

    /// <summary> gets a typed file resource by game path </summary>
    /// <typeparam name="T">the file resource type</typeparam>
    /// <param name="path">the game path to load</param>
    /// <returns>the loaded typed file resource when available</returns>
    T? GetFile<T>(string path)
        where T : FileResource;

    /// <summary> gets a typed file resource by local disk path while preserving the original game path context </summary>
    /// <typeparam name="T">the file resource type</typeparam>
    /// <param name="localPath">the rooted local file path to load</param>
    /// <param name="originalPath">the original game path represented by the local file</param>
    /// <returns>the loaded typed file resource when available</returns>
    T? GetFileFromDisk<T>(string localPath, string originalPath)
        where T : FileResource;
}

internal sealed class DalamudObjectAssetGameData : IObjectAssetGameData
{
    private readonly IDataManager _gameData;

    public DalamudObjectAssetGameData(IDataManager gameData)
    {
        _gameData = gameData;
    }

    public ExcelSheet<T>? GetCurrentLanguageExcelSheet<T>()
        where T : struct, IExcelRow<T>
        => _gameData.GetExcelSheet<T>(_gameData.Language);

    public ExcelSheet<T>? GetExcelSheet<T>()
        where T : struct, IExcelRow<T>
        => _gameData.GetExcelSheet<T>();

    public bool FileExists(string path)
        => _gameData.FileExists(path);

    public FileResource? GetFile(string path)
        => _gameData.GetFile(path);

    public T? GetFile<T>(string path)
        where T : FileResource
        => _gameData.GetFile<T>(path);

    public T? GetFileFromDisk<T>(string localPath, string originalPath)
        where T : FileResource
        => _gameData.GameData.GetFileFromDisk<T>(localPath, originalPath);
}

