using Intoner.Objects.Resources;
using Penumbra.GameData.Data;
using PenumbraMdlFile = Penumbra.GameData.Files.Models.MdlFile;
using PenumbraMtrlFile = Penumbra.GameData.Files.Materials.MtrlFile;

namespace Intoner.Objects.Assets;

internal static class ObjectMaterialPathUtility
{
    public static AssetDependencyPaths CollectGameModelMaterialPaths(
        IObjectAssetGameData gameData,
        string modelPath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string normalizedModelPath = ObjectResolvedPath.FromGamePath(modelPath).Path;

        if (!GameAssetPathRules.IsFileKind(normalizedModelPath, GameAssetFileKind.Mdl))
        {
            return AssetDependencyPaths.Empty;
        }

        ObjectAssetFileData file = ObjectAssetFileUtility.ReadGameFile(gameData, normalizedModelPath, cancellationToken);
        PenumbraMdlFile? model = TryLoadModel(file.Data, cancellationToken);
        return new(CollectModelMaterialPaths(normalizedModelPath, model, gameData.FileExists, cancellationToken),
            model is not null || file.Data is null && file.IsComplete);
    }

    public static AssetDependencyPaths CollectLocalModelMaterialPaths(
        IObjectAssetGameData gameData,
        string requestedModelPath,
        string localModelPath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string normalizedRequestedModelPath = ObjectResolvedPath.FromGamePath(requestedModelPath).Path;

        if (!GameAssetPathRules.IsFileKind(normalizedRequestedModelPath, GameAssetFileKind.Mdl))
        {
            return AssetDependencyPaths.Empty;
        }

        PenumbraMdlFile? model = TryLoadModel(
            ObjectAssetFileUtility.TryReadLocalFileBytes(localModelPath, cancellationToken),
            cancellationToken);
        return new(CollectModelMaterialPaths(normalizedRequestedModelPath, model, gameData.FileExists, cancellationToken),
            model is not null);
    }

    public static AssetDependencyPaths CollectMaterialDependencyPaths(
        IObjectAssetGameData gameData,
        string materialPath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string normalizedMaterialPath = ObjectResolvedPath.FromGamePath(materialPath).Path;

        if (!GameAssetPathRules.IsFileKind(normalizedMaterialPath, GameAssetFileKind.Mtrl))
        {
            return AssetDependencyPaths.Empty;
        }

        ObjectAssetFileData file = ObjectAssetFileUtility.ReadGameFile(gameData, normalizedMaterialPath, cancellationToken);
        PenumbraMtrlFile? material = TryLoadMaterial(file.Data, cancellationToken);
        List<string> dependencyPaths = [];
        CollectMaterialDependencyPaths(material, dependencyPaths, new HashSet<string>(StringComparer.OrdinalIgnoreCase), cancellationToken);
        return new(dependencyPaths, material is not null || file.Data is null && file.IsComplete);
    }

    public static AssetDependencyPaths CollectLocalMaterialDependencyPaths(
        string localMaterialPath,
        CancellationToken cancellationToken = default)
    {
        List<string> dependencyPaths = [];
        HashSet<string> seenPaths = new(StringComparer.OrdinalIgnoreCase);
        PenumbraMtrlFile? material = TryLoadMaterial(
            ObjectAssetFileUtility.TryReadLocalFileBytes(localMaterialPath, cancellationToken),
            cancellationToken);
        CollectMaterialDependencyPaths(material, dependencyPaths, seenPaths, cancellationToken);
        return new(dependencyPaths, material is not null);
    }

    private static IReadOnlyList<string> CollectModelMaterialPaths(
        string normalizedModelPath,
        PenumbraMdlFile? model,
        Func<string, bool> fileExists,
        CancellationToken cancellationToken)
    {
        if (model is null)
        {
            return [];
        }

        List<string> materialPaths = [];
        HashSet<string> seenPaths = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> seenMaterialNames = new(StringComparer.OrdinalIgnoreCase);
        foreach (string materialName in model.Materials)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string normalizedMaterialName = ObjectResolvedPath.FromGamePath(materialName).Path;

            if (normalizedMaterialName.Length > 0
             && seenMaterialNames.Add(normalizedMaterialName)
             && GameMaterialPathUtility.TryResolveMaterialPath(fileExists, normalizedModelPath, normalizedMaterialName, out string materialPath, allowMissing: true))
            {
                AddCandidatePath(materialPaths, seenPaths, materialPath);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return materialPaths;
    }

    private static void CollectMaterialDependencyPaths(
        PenumbraMtrlFile? material,
        ICollection<string> dependencyPaths,
        ISet<string> seenPaths,
        CancellationToken cancellationToken)
    {
        if (material is null)
        {
            return;
        }

        foreach (PenumbraMtrlFile.Texture texture in material.Textures)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TextureMapKindResolver.TryResolveTexturePath(texture, out string texturePath))
            {
                AddCandidatePath(dependencyPaths, seenPaths, texturePath);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();

        string shaderName = ObjectResolvedPath.FromGamePath(material.ShaderPackage.Name).Path;

        if (shaderName.Length > 0)
        {
            AddCandidatePath(dependencyPaths, seenPaths, GamePaths.Shader(shaderName));
        }
    }

    private static PenumbraMdlFile? TryLoadModel(byte[]? data, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PenumbraMdlFile? model = ModelFileReader.Read(data);
        cancellationToken.ThrowIfCancellationRequested();
        return model;
    }

    private static PenumbraMtrlFile? TryLoadMaterial(byte[]? data, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (data is null)
        {
            return null;
        }

        try
        {
            // material validation requires directories; the native loader also accepts dummy.tex :)
            PenumbraMtrlFile material = new(data);
            cancellationToken.ThrowIfCancellationRequested();
            return material;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or IndexOutOfRangeException or OverflowException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }
    }

    private static void AddCandidatePath(ICollection<string> candidates, ISet<string> seenPaths, string path)
        => _ = GameAssetPathCollectionUtility.AddGamePath(candidates, seenPaths, ObjectResolvedPath.FromGamePath(path).Path);
}
