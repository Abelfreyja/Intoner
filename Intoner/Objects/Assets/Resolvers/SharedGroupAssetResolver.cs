using Intoner.Objects.Resources;
using Intoner.Objects.Utils;
using Lumina.Data.Files;
using Lumina.Data.Parsing.Layer;
using System.Numerics;
using Common = Lumina.Data.Parsing.Common;

namespace Intoner.Objects.Assets;

internal static class SharedGroupAssetResolver
{
    public static SharedGroupAssetInfo AnalyzeSharedGroup(IObjectAssetGameData gameData, string sharedGroupPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string normalizedPath = ObjectResolvedPath.FromGamePath(sharedGroupPath).Path;

        if (!ObjectAssetPathRules.IsCatalogSharedGroupPath(normalizedPath))
        {
            return new SharedGroupAssetInfo([], [], [], [], [], AssetReferenceSources.Empty, isComplete: false, exists: false);
        }

        bool exists = gameData.FileExists(normalizedPath);
        cancellationToken.ThrowIfCancellationRequested();
        return exists
            ? new SharedGroupAssetCollector(gameData, collectCatalogAssets: true, cancellationToken).CollectGame(normalizedPath)
            : SharedGroupAssetInfo.Missing;
    }

    public static AssetDependencyPaths AnalyzeSharedGroupDependencies(
        IObjectAssetGameData gameData,
        string requestedSharedGroupPath,
        ObjectResolvedPath effectivePath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string normalizedRequestedPath = ObjectResolvedPath.FromGamePath(requestedSharedGroupPath).Path;

        if (!GameAssetPathRules.IsFileKind(normalizedRequestedPath, GameAssetFileKind.Sgb)
         || effectivePath.Kind is not (ObjectResolvedPathKind.GamePath or ObjectResolvedPathKind.LocalFile))
        {
            return AssetDependencyPaths.Empty;
        }

        return new SharedGroupAssetCollector(gameData, collectCatalogAssets: false, cancellationToken)
            .CollectDependencies(normalizedRequestedPath, effectivePath);
    }

    private sealed class SharedGroupAssetCollector(
        IObjectAssetGameData gameData,
        bool collectCatalogAssets,
        CancellationToken cancellationToken = default)
    {
        private readonly IObjectAssetGameData _gameData = gameData;
        private readonly bool _collectCatalogAssets = collectCatalogAssets;
        private readonly CancellationToken _cancellationToken = cancellationToken;
        private readonly List<PreviewModelInfo> _previewModels = [];
        private readonly BgColliderDefinitionSet _colliderDefinitions = new();
        private readonly List<string> _bgObjectModelPaths = [];
        private readonly List<string> _nestedSharedGroupPaths = [];
        private readonly List<string> _autoplayVfxPaths = [];
        private readonly List<string> _referencedVfxPaths = [];
        private readonly List<string> _otherDependencyPaths = [];
        private readonly HashSet<string> _seenBgObjectModels = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _seenNestedSharedGroupPaths = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _seenAutoplayVfxPaths = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _seenReferencedVfxPaths = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _seenOtherDependencyPaths = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _recursionStack = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, SgbFile?> _loadedSharedGroups = new(StringComparer.OrdinalIgnoreCase);
        private readonly AssetReferenceSources.Builder _sources = new();
        private bool _isComplete = true;

        public SharedGroupAssetInfo CollectGame(string sharedGroupPath)
        {
            CollectGameSharedGroupAssets(sharedGroupPath, Matrix4x4.Identity);
            _cancellationToken.ThrowIfCancellationRequested();
            return new SharedGroupAssetInfo(
                _previewModels,
                _bgObjectModelPaths,
                _nestedSharedGroupPaths,
                _autoplayVfxPaths,
                _referencedVfxPaths,
                _sources.Build(),
                _isComplete) { ColliderDefinitions = _colliderDefinitions.Build() };
        }

        public AssetDependencyPaths CollectDependencies(string requestedSharedGroupPath, ObjectResolvedPath effectivePath)
        {
            if (effectivePath.Kind == ObjectResolvedPathKind.GamePath && !_gameData.FileExists(effectivePath.Path))
            {
                _cancellationToken.ThrowIfCancellationRequested();
                return AssetDependencyPaths.Empty;
            }

            SgbFile? file = TryLoadSharedGroup(requestedSharedGroupPath, effectivePath);
            CollectLoadedSharedGroupAssets(
                file,
                Matrix4x4.Identity);
            _cancellationToken.ThrowIfCancellationRequested();
            return new(GameAssetPathCollectionUtility.MergeGamePaths(
                _bgObjectModelPaths,
                _nestedSharedGroupPaths,
                _referencedVfxPaths,
                _otherDependencyPaths), _isComplete);
        }

        private void CollectGameSharedGroupAssets(string sharedGroupPath, Matrix4x4 parentTransform)
        {
            if (!_recursionStack.Add(sharedGroupPath))
            {
                return;
            }

            try
            {
                if (!_loadedSharedGroups.TryGetValue(sharedGroupPath, out SgbFile? file))
                {
                    file = TryLoadSharedGroup(sharedGroupPath, ObjectResolvedPath.FromGamePath(sharedGroupPath));

                    _loadedSharedGroups.Add(sharedGroupPath, file);
                }

                CollectLoadedSharedGroupAssets(
                    file,
                    parentTransform);
            }
            finally
            {
                _recursionStack.Remove(sharedGroupPath);
            }
        }

        private void CollectLoadedSharedGroupAssets(
            SgbFile? file,
            Matrix4x4 parentTransform)
        {
            _cancellationToken.ThrowIfCancellationRequested();

            if (file?.LayerGroups is null)
            {
                _isComplete = false;
                return;
            }

            foreach (var layerGroup in file.LayerGroups)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                foreach (var layer in layerGroup.Layers)
                {
                    _cancellationToken.ThrowIfCancellationRequested();
                    foreach (var instanceObject in layer.InstanceObjects)
                    {
                        _cancellationToken.ThrowIfCancellationRequested();
                        Matrix4x4 worldTransform = _collectCatalogAssets
                            ? BuildPreviewTransform(instanceObject.Transform) * parentTransform
                            : Matrix4x4.Identity;
                        CollectInstanceObject(instanceObject.Object, worldTransform);
                    }
                }
            }
        }

        private void CollectInstanceObject(object instanceObject, Matrix4x4 worldTransform)
        {
            switch (instanceObject)
            {
                case LayerCommon.BGInstanceObject bgInstance:
                    CollectBgObject(bgInstance, worldTransform);
                    break;
                case LayerCommon.VFXInstanceObject vfxInstance:
                    CollectVfx(vfxInstance);
                    break;
                case LayerCommon.SharedGroupInstanceObject sharedGroupInstance:
                    CollectNestedSharedGroup(sharedGroupInstance, worldTransform);
                    break;
                case LayerCommon.SoundInstanceObject soundInstance when !_collectCatalogAssets:
                    CollectOtherDependency(soundInstance.AssetPath, GameAssetFileKind.Scd);
                    break;
                case LayerCommon.LightInstanceObject lightInstance when !_collectCatalogAssets:
                    CollectOtherDependency(lightInstance.TexturePath, GameAssetFileKind.Tex);
                    break;
            }
        }

        private void CollectOtherDependency(string path, GameAssetFileKind kind)
        {
            string normalizedPath = ObjectResolvedPath.FromGamePath(path).Path;

            if (GameAssetPathRules.IsFileKind(normalizedPath, kind) && _seenOtherDependencyPaths.Add(normalizedPath))
            {
                _otherDependencyPaths.Add(normalizedPath);
            }
        }

        private void CollectBgObject(LayerCommon.BGInstanceObject bgInstance, Matrix4x4 worldTransform)
        {
            string modelPath = ObjectResolvedPath.FromGamePath(bgInstance.AssetPath).Path;

            if (!CanUseModelPath(modelPath))
            {
                return;
            }

            if (_collectCatalogAssets)
            {
                _previewModels.Add(new PreviewModelInfo(modelPath, worldTransform));
            }

            if (_collectCatalogAssets && !ObjectAssetPathRules.IsBgObjectModelPath(modelPath))
            {
                return;
            }

            if (_collectCatalogAssets)
            {
                _sources.Add(modelPath, _recursionStack);
            }

            if (_seenBgObjectModels.Add(modelPath))
            {
                _bgObjectModelPaths.Add(modelPath);
            }
        }

        private void CollectNestedSharedGroup(LayerCommon.SharedGroupInstanceObject sharedGroupInstance, Matrix4x4 worldTransform)
        {
            string sharedGroupPath = ObjectResolvedPath.FromGamePath(sharedGroupInstance.AssetPath).Path;

            if (!CanUseSharedGroupPath(sharedGroupPath))
            {
                return;
            }

            if (_seenNestedSharedGroupPaths.Add(sharedGroupPath))
            {
                _nestedSharedGroupPaths.Add(sharedGroupPath);
            }

            if (_collectCatalogAssets)
            {
                CollectGameSharedGroupAssets(sharedGroupPath, worldTransform);
            }
        }

        private void CollectVfx(LayerCommon.VFXInstanceObject vfxInstance)
        {
            string vfxPath = ObjectResolvedPath.FromGamePath(vfxInstance.AssetPath).Path;

            if (!CanUseVfxPath(vfxPath))
            {
                return;
            }

            if (_seenReferencedVfxPaths.Add(vfxPath))
            {
                _referencedVfxPaths.Add(vfxPath);
            }

            if (_collectCatalogAssets)
            {
                _sources.Add(vfxPath, _recursionStack);
            }

            if (vfxInstance.IsAutoPlay != 0 && _seenAutoplayVfxPaths.Add(vfxPath))
            {
                _autoplayVfxPaths.Add(vfxPath);
            }
        }

        private bool CanUseModelPath(string modelPath)
        {
            if (!_collectCatalogAssets)
            {
                return GameAssetPathRules.IsFileKind(modelPath, GameAssetFileKind.Mdl);
            }

            return ObjectAssetPathRules.IsCatalogModelPath(modelPath) && _gameData.FileExists(modelPath);
        }

        private bool CanUseSharedGroupPath(string sharedGroupPath)
        {
            if (!_collectCatalogAssets)
            {
                return GameAssetPathRules.IsFileKind(sharedGroupPath, GameAssetFileKind.Sgb);
            }

            if (!ObjectAssetPathRules.IsCatalogSharedGroupPath(sharedGroupPath))
            {
                return false;
            }

            return _gameData.FileExists(sharedGroupPath);
        }

        private bool CanUseVfxPath(string vfxPath)
        {
            if (!_collectCatalogAssets)
            {
                return GameAssetPathRules.IsFileKind(vfxPath, GameAssetFileKind.Avfx);
            }

            return GameAssetPathRules.IsFileKind(vfxPath, GameAssetFileKind.Avfx) && _gameData.FileExists(vfxPath);
        }

        private SgbFile? TryLoadSharedGroup(string requestedSharedGroupPath, ObjectResolvedPath effectivePath)
        {
            _cancellationToken.ThrowIfCancellationRequested();

            try
            {
                SgbFile? file = effectivePath.IsLocalFile
                    ? _gameData.GetFileFromDisk<SgbFile>(effectivePath.Path, requestedSharedGroupPath)
                    : _gameData.GetFile<SgbFile>(effectivePath.Path);
                _cancellationToken.ThrowIfCancellationRequested();

                if (file is not null)
                {
                    try
                    {
                        _isComplete &= BgColliderDefinitionReader.CollectSharedGroup(file.Data, requestedSharedGroupPath,
                            _collectCatalogAssets ? _colliderDefinitions : null,
                            path => CollectOtherDependency(path, GameAssetFileKind.Pcb), _cancellationToken);
                    }
                    catch (InvalidDataException)
                    {
                        _isComplete = false;
                    }
                }

                return file;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ObjectAssetFileUtility.IsReadFailure(ex)
                                   || ex is ArgumentException or IndexOutOfRangeException or OverflowException)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                return null;
            }
        }

        private static Matrix4x4 BuildPreviewTransform(Common.Transformation transform)
        {
            var translation = ToNumericsVector3(transform.Translation);
            var rotation = ToNumericsVector3(transform.Rotation);
            var scale = ToNumericsVector3(transform.Scale);
            rotation = NumericsUtility.IsFinite(rotation) ? rotation : Vector3.Zero;

            scale = new Vector3(
                NormalizeScaleComponent(scale.X),
                NormalizeScaleComponent(scale.Y),
                NormalizeScaleComponent(scale.Z));

            return Matrix4x4.CreateScale(scale)
                 * Matrix4x4.CreateRotationX(rotation.X)
                 * Matrix4x4.CreateRotationY(rotation.Y)
                 * Matrix4x4.CreateRotationZ(rotation.Z)
                 * Matrix4x4.CreateTranslation(translation);
        }

        private static Vector3 ToNumericsVector3(Common.Vector3 vector)
            => new(vector.X, vector.Y, vector.Z);

        private static float NormalizeScaleComponent(float value)
            => !float.IsFinite(value) || NumericsUtility.IsNearlyZero(value, 0.0001f)
                ? 1f
                : value;
    }
}
