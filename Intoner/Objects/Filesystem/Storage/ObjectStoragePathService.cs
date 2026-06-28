using Microsoft.Extensions.Hosting;

namespace Intoner.Objects.Filesystem.Storage;

/// <summary> resolves storage paths owned by the objects subsystem </summary>
internal interface IObjectStoragePathService
{
    /// <summary> gets the plugin content root used as the base for object storage </summary>
    string PluginRootPath { get; }

    /// <summary> gets the root directory for data </summary>
    string ObjectRootPath { get; }

    /// <summary> gets the config path </summary>
    string ObjectConfigurationPath { get; }

    /// <summary> gets the authored object collections path </summary>
    string ObjectCollectionsPath { get; }

    /// <summary> gets the directory used for saved object layout json files </summary>
    string ObjectLayoutsPath { get; }

    /// <summary> gets the directory used for object autosave layouts </summary>
    string ObjectAutosaveRootPath { get; }

    /// <summary> gets the latest object autosave layout path </summary>
    string ObjectAutosaveCurrentPath { get; }

    /// <summary> gets the directory used by the object asset cache </summary>
    string AssetCacheRootPath { get; }

    /// <summary> gets the object asset cache manifest path </summary>
    string AssetCacheManifestPath { get; }

    /// <summary> gets the object asset cache payload path </summary>
    string AssetCachePayloadPath { get; }

}

internal sealed class ObjectStoragePathService : IObjectStoragePathService
{
    public ObjectStoragePathService(IHostEnvironment hostEnvironment)
    {
        PluginRootPath = hostEnvironment.ContentRootPath;
        ObjectRootPath = Path.Combine(PluginRootPath, "objects");
        ObjectConfigurationPath = Path.Combine(ObjectRootPath, "config.json");
        ObjectCollectionsPath = Path.Combine(ObjectRootPath, "collections.json");
        ObjectLayoutsPath = Path.Combine(ObjectRootPath, "layouts");
        ObjectAutosaveRootPath = Path.Combine(ObjectRootPath, "autosaves");
        ObjectAutosaveCurrentPath = Path.Combine(ObjectAutosaveRootPath, "current.autosave.json");
        AssetCacheRootPath = Path.Combine(ObjectRootPath, "asset-cache");
        AssetCacheManifestPath = Path.Combine(AssetCacheRootPath, "manifest.json");
        AssetCachePayloadPath = Path.Combine(AssetCacheRootPath, "assets.cache");
    }

    public string PluginRootPath { get; }

    public string ObjectRootPath { get; }

    public string ObjectConfigurationPath { get; }

    public string ObjectCollectionsPath { get; }

    public string ObjectLayoutsPath { get; }

    public string ObjectAutosaveRootPath { get; }

    public string ObjectAutosaveCurrentPath { get; }

    public string AssetCacheRootPath { get; }

    public string AssetCacheManifestPath { get; }

    public string AssetCachePayloadPath { get; }
}

