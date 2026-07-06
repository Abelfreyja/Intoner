using Intoner.Objects.Resources;
using Intoner.Objects.Utils;
using Lumina.Data.Files;
using Lumina.Data.Parsing.Layer;
using System.Numerics;
using Common = Lumina.Data.Parsing.Common;

namespace Intoner.Objects.Assets;

internal static class SharedGroupAssetResolver
{
    public static SharedGroupAssetInfo AnalyzeSharedGroup(IObjectAssetGameData gameData, string sharedGroupPath)
    {
        var normalizedPath = ObjectPathRules.NormalizeGamePath(sharedGroupPath);
        if (!ObjectPathRules.IsCatalogSharedGroupPath(normalizedPath) || !gameData.FileExists(normalizedPath))
        {
            return new SharedGroupAssetInfo([], [], [], [], []);
        }

        return new SharedGroupAssetCollector(gameData, SharedGroupCollectionPolicy.Catalog).CollectGame(normalizedPath);
    }

    public static SharedGroupDependencyInfo AnalyzeLocalSharedGroupDependencies(
        IObjectAssetGameData gameData,
        string requestedSharedGroupPath,
        string localSharedGroupPath)
    {
        string normalizedRequestedPath = ObjectPathRules.NormalizeGamePath(requestedSharedGroupPath);
        string normalizedLocalPath = ObjectResourcePathUtility.NormalizeLocalFilePath(localSharedGroupPath);
        if (!ObjectPathRules.IsSharedGroupPath(normalizedRequestedPath)
         || normalizedLocalPath.Length == 0)
        {
            return new SharedGroupDependencyInfo([], [], []);
        }

        return new SharedGroupAssetCollector(gameData, SharedGroupCollectionPolicy.CollectionDependencies)
            .CollectLocal(normalizedRequestedPath, normalizedLocalPath);
    }

    private sealed class SharedGroupAssetCollector(IObjectAssetGameData gameData, SharedGroupCollectionPolicy policy)
    {
        private readonly IObjectAssetGameData _gameData = gameData;
        private readonly SharedGroupCollectionPolicy _policy = policy;
        private readonly List<PreviewModelInfo> _previewModels = [];
        private readonly List<string> _bgObjectModelPaths = [];
        private readonly List<string> _nestedSharedGroupPaths = [];
        private readonly List<string> _standaloneVfxPaths = [];
        private readonly List<string> _referencedVfxPaths = [];
        private readonly HashSet<string> _seenBgObjectModels = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _seenNestedSharedGroups = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _seenStandaloneVfxPaths = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _seenReferencedVfxPaths = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _recursionStack = new(StringComparer.OrdinalIgnoreCase);

        public SharedGroupAssetInfo CollectGame(string sharedGroupPath)
        {
            CollectGameSharedGroupAssets(sharedGroupPath, Matrix4x4.Identity);
            return new SharedGroupAssetInfo(
                _previewModels,
                _bgObjectModelPaths,
                _nestedSharedGroupPaths,
                _standaloneVfxPaths,
                _referencedVfxPaths);
        }

        public SharedGroupDependencyInfo CollectLocal(string requestedSharedGroupPath, string localSharedGroupPath)
        {
            CollectLoadedSharedGroupAssets(
                TryLoadLocalSharedGroup(requestedSharedGroupPath, localSharedGroupPath),
                Matrix4x4.Identity);
            return new SharedGroupDependencyInfo(
                _bgObjectModelPaths,
                _nestedSharedGroupPaths,
                _referencedVfxPaths);
        }

        private void CollectGameSharedGroupAssets(string sharedGroupPath, Matrix4x4 parentTransform)
        {
            if (!_recursionStack.Add(sharedGroupPath))
            {
                return;
            }

            try
            {
                CollectLoadedSharedGroupAssets(
                    TryLoadGameSharedGroup(sharedGroupPath),
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
            if (file?.LayerGroups is null || file.LayerGroups.Length == 0)
            {
                return;
            }

            foreach (var layerGroup in file.LayerGroups)
            {
                foreach (var layer in layerGroup.Layers)
                {
                    foreach (var instanceObject in layer.InstanceObjects)
                    {
                        var worldTransform = BuildPreviewTransform(instanceObject.Transform) * parentTransform;
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
            }
        }

        private void CollectBgObject(LayerCommon.BGInstanceObject bgInstance, Matrix4x4 worldTransform)
        {
            var modelPath = ObjectPathRules.NormalizeGamePath(bgInstance.AssetPath);
            if (_policy.CollectPreviewModels && CanUsePreviewModelPath(modelPath))
            {
                _previewModels.Add(new PreviewModelInfo(modelPath, worldTransform));
            }

            if (!CanUseModelDependencyPath(modelPath)
             || !_seenBgObjectModels.Add(modelPath))
            {
                return;
            }

            _bgObjectModelPaths.Add(modelPath);
        }

        private void CollectNestedSharedGroup(LayerCommon.SharedGroupInstanceObject sharedGroupInstance, Matrix4x4 worldTransform)
        {
            var sharedGroupPath = ObjectPathRules.NormalizeGamePath(sharedGroupInstance.AssetPath);
            if (!CanUseSharedGroupPath(sharedGroupPath)
             || !_seenNestedSharedGroups.Add(sharedGroupPath))
            {
                return;
            }

            _nestedSharedGroupPaths.Add(sharedGroupPath);
            if (_policy.RecurseNestedSharedGroups)
            {
                CollectGameSharedGroupAssets(sharedGroupPath, worldTransform);
            }
        }

        private void CollectVfx(LayerCommon.VFXInstanceObject vfxInstance)
        {
            var vfxPath = ObjectPathRules.NormalizeGamePath(vfxInstance.AssetPath);
            if (!CanUseVfxPath(vfxPath))
            {
                return;
            }

            if (_seenReferencedVfxPaths.Add(vfxPath))
            {
                _referencedVfxPaths.Add(vfxPath);
            }

            if (vfxInstance.IsAutoPlay != 0 && _seenStandaloneVfxPaths.Add(vfxPath))
            {
                _standaloneVfxPaths.Add(vfxPath);
            }
        }

        private bool CanUsePreviewModelPath(string modelPath)
        {
            if (!_policy.RequireExistingGamePaths)
            {
                return ObjectPathRules.IsModelPath(modelPath);
            }

            return ObjectPathRules.IsCatalogModelPath(modelPath) && _gameData.FileExists(modelPath);
        }

        private bool CanUseModelDependencyPath(string modelPath)
        {
            if (!_policy.RequireExistingGamePaths)
            {
                return ObjectPathRules.IsModelPath(modelPath);
            }

            return ObjectPathRules.IsBgObjectModelPath(modelPath) && _gameData.FileExists(modelPath);
        }

        private bool CanUseSharedGroupPath(string sharedGroupPath)
        {
            if (!_policy.RequireExistingGamePaths)
            {
                return ObjectPathRules.IsSharedGroupPath(sharedGroupPath);
            }

            return ObjectPathRules.IsCatalogSharedGroupPath(sharedGroupPath) && _gameData.FileExists(sharedGroupPath);
        }

        private bool CanUseVfxPath(string vfxPath)
        {
            if (!_policy.RequireExistingGamePaths)
            {
                return ObjectPathRules.IsVfxPath(vfxPath);
            }

            return ObjectPathRules.IsVfxPath(vfxPath) && _gameData.FileExists(vfxPath);
        }

        private SgbFile? TryLoadGameSharedGroup(string sharedGroupPath)
        {
            try
            {
                return _gameData.GetFile<SgbFile>(sharedGroupPath);
            }
            catch
            {
                return null;
            }
        }

        private SgbFile? TryLoadLocalSharedGroup(string requestedSharedGroupPath, string localSharedGroupPath)
        {
            try
            {
                return _gameData.GetFileFromDisk<SgbFile>(localSharedGroupPath, requestedSharedGroupPath);
            }
            catch
            {
                return null;
            }
        }

        private static Matrix4x4 BuildPreviewTransform(Common.Transformation transform)
        {
            var translation = ToNumericsVector3(transform.Translation);
            var rotation = ToNumericsVector3(transform.Rotation);
            var scale = ToNumericsVector3(transform.Scale);

            scale = new Vector3(
                NormalizeScaleComponent(scale.X),
                NormalizeScaleComponent(scale.Y),
                NormalizeScaleComponent(scale.Z));

            return Matrix4x4.CreateScale(scale)
                 * Matrix4x4.CreateFromQuaternion(ObjectTransformMath.CreateRotationQuaternion(rotation))
                 * Matrix4x4.CreateTranslation(translation);
        }

        private static Vector3 ToNumericsVector3(Common.Vector3 vector)
            => new(vector.X, vector.Y, vector.Z);

        private static float NormalizeScaleComponent(float value)
            => !float.IsFinite(value) || ObjectMathUtility.IsNearlyZero(value, 0.0001f)
                ? 1f
                : value;
    }

    private readonly record struct SharedGroupCollectionPolicy(
        bool RequireExistingGamePaths,
        bool CollectPreviewModels,
        bool RecurseNestedSharedGroups)
    {
        public static SharedGroupCollectionPolicy Catalog { get; } = new(
            RequireExistingGamePaths: true,
            CollectPreviewModels: true,
            RecurseNestedSharedGroups: true);

        public static SharedGroupCollectionPolicy CollectionDependencies { get; } = new(
            RequireExistingGamePaths: false,
            CollectPreviewModels: false,
            RecurseNestedSharedGroups: true);
    }
}

