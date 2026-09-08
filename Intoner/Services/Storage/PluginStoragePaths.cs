using Microsoft.Extensions.Hosting;

namespace Intoner.Services.Storage;

/// <summary> resolves persistent storage paths owned by plugin subsystems </summary>
internal interface IPluginStoragePaths
{
    /// <summary> gets the plugin content root used as the storage base </summary>
    string RootPath { get; }

    /// <summary> gets the root directory for data </summary>
    string ObjectRootPath { get; }

    /// <summary> gets the config path </summary>
    string ConfigurationPath { get; }

    /// <summary> gets the authored object collections path </summary>
    string ObjectCollectionsPath { get; }

    /// <summary> gets the reusable object Library directory </summary>
    string ObjectLibraryRootPath { get; }

    /// <summary> gets the ordinary object Library state path </summary>
    string ObjectLibraryStatePath { get; }

    /// <summary> gets the directory containing reusable prefab files </summary>
    string ObjectLibraryPrefabsPath { get; }

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

    /// <summary> gets the shared authored scene store path </summary>
    string SceneStorePath { get; }
}

internal sealed class PluginStoragePaths : IPluginStoragePaths
{
    public PluginStoragePaths(IHostEnvironment hostEnvironment)
    {
        RootPath = hostEnvironment.ContentRootPath;
        ObjectRootPath = Path.Combine(RootPath, "objects");
        ConfigurationPath = Path.Combine(ObjectRootPath, "config.json");
        ObjectCollectionsPath = Path.Combine(ObjectRootPath, "collections.json");
        ObjectLibraryRootPath = Path.Combine(ObjectRootPath, "library");
        ObjectLibraryStatePath = Path.Combine(ObjectLibraryRootPath, "library.json");
        ObjectLibraryPrefabsPath = Path.Combine(ObjectLibraryRootPath, "prefabs");
        ObjectLayoutsPath = Path.Combine(ObjectRootPath, "layouts");
        ObjectAutosaveRootPath = Path.Combine(ObjectRootPath, "autosaves");
        ObjectAutosaveCurrentPath = Path.Combine(ObjectAutosaveRootPath, "current.autosave.json");
        AssetCacheRootPath = Path.Combine(ObjectRootPath, "asset-cache");
        AssetCacheManifestPath = Path.Combine(AssetCacheRootPath, "manifest.json");
        AssetCachePayloadPath = Path.Combine(AssetCacheRootPath, "assets.cache");
        SceneStorePath = Path.Combine(ObjectRootPath, "scene.json");
    }

    public string RootPath { get; }

    public string ObjectRootPath { get; }

    public string ConfigurationPath { get; }

    public string ObjectCollectionsPath { get; }

    public string ObjectLibraryRootPath { get; }

    public string ObjectLibraryStatePath { get; }

    public string ObjectLibraryPrefabsPath { get; }

    public string ObjectLayoutsPath { get; }

    public string ObjectAutosaveRootPath { get; }

    public string ObjectAutosaveCurrentPath { get; }

    public string AssetCacheRootPath { get; }

    public string AssetCacheManifestPath { get; }

    public string AssetCachePayloadPath { get; }

    public string SceneStorePath { get; }
}

