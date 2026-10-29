using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;
using Intoner.Services.Interop;
using Microsoft.Extensions.Logging;
using System.Numerics;

namespace Intoner.Objects.Interop
{
    internal sealed unsafe class CameraCollisionBindings
    {
        private readonly ISigScanner _scanner;

        public readonly nint LayoutInstanceInit;
        public readonly nint ColliderFinalizer;
        public readonly nint SceneManagerUpdateScenes;
        public readonly nint BGCollisionModuleShutdown;
        public readonly nint BGCollisionModuleDtor;
        public readonly nint CameraSweepSphere;
        public readonly nint LayoutWorldShutdown;
        public readonly nint LayoutWorldDeinitialize;
        public readonly delegate* unmanaged<SceneManager*, SceneManager*> SceneManagerCtor;
        public readonly delegate* unmanaged<SceneManager*, void*, int, nint> SceneManagerInit;
        public readonly delegate* unmanaged<Vector3*, Vector3*> Vector3Normalize;
        public readonly delegate* unmanaged<RaycastHit*, Vector3*, byte> RaycastHitGetNormal;
        public readonly delegate* unmanaged<Transform*, FileLayerGroupTransform*, void> TransformFromFile;
        public readonly delegate* unmanaged<Vector3*, Vector3*, Vector3*, Transform*, void> TransformGetComponents;
        public readonly delegate* unmanaged<ILayoutInstance*, Vector3*, Vector3*, Vector3*, Transform*, AnalyticShapeData*, byte> GetAnalyticColliderTransform;
        public readonly delegate* unmanaged<ColliderMesh*, uint, uint, void> SetCylinderMeshMaterial;
        public readonly delegate* unmanaged<ColliderMesh*, Vector3*, void> SetCylinderMeshBounds;
        public nint[] PrimaryCreators { get; } = [];
        public nint SecondaryCreator { get; }
        public nint[] Deinitializers { get; } = [];
        public nint[] ActivitySetters { get; } = [];
        public bool IsAvailable { get; }
        public bool CanCreateBg { get; }

        // don't ask me lol
        public CameraCollisionBindings(ILogger<CameraCollisionBindings> logger, ISigScanner scanner)
        {
            _scanner = scanner;
            ColliderFinalizer = Resolve(IntonerSignatures.ColliderFinalizer);
            LayoutInstanceInit = ResolveJmpCall(IntonerSignatures.ILayoutInstanceInit);
            SceneManagerUpdateScenes = Resolve(IntonerSignatures.SceneManagerUpdateScenes);
            BGCollisionModuleShutdown = Resolve(IntonerSignatures.BGCollisionModuleShutdown);
            BGCollisionModuleDtor = ResolveJmpCall(IntonerSignatures.BGCollisionModuleDtor);
            CameraSweepSphere = ResolveJmpCall(IntonerSignatures.CameraSweepSphere);
            LayoutWorldShutdown = ResolveJmpCall(IntonerSignatures.LayoutWorldShutdownWrapper);
            LayoutWorldDeinitialize = Resolve(IntonerSignatures.LayoutWorldDeinitializeWrapper);
            SceneManagerCtor = (delegate* unmanaged<SceneManager*, SceneManager*>)Resolve(IntonerSignatures.SceneManagerCtor);
            SceneManagerInit = (delegate* unmanaged<SceneManager*, void*, int, nint>)Resolve(IntonerSignatures.SceneManagerInit);
            Vector3Normalize = (delegate* unmanaged<Vector3*, Vector3*>)ResolveJmpCall(IntonerSignatures.BGCollisionVector3Normalize);
            RaycastHitGetNormal = (delegate* unmanaged<RaycastHit*, Vector3*, byte>)ResolveJmpCall(IntonerSignatures.RaycastHitGetNormal);
            TransformFromFile = (delegate* unmanaged<Transform*, FileLayerGroupTransform*, void>)ResolveJmpCall(IntonerSignatures.LayoutTransformFromFile);
            TransformGetComponents = (delegate* unmanaged<Vector3*, Vector3*, Vector3*, Transform*, void>)ResolveJmpCall(IntonerSignatures.LayoutTransformGetComponents);
            GetAnalyticColliderTransform = (delegate* unmanaged<ILayoutInstance*, Vector3*, Vector3*, Vector3*, Transform*, AnalyticShapeData*, byte>)ResolveJmpCall(IntonerSignatures.GetAnalyticColliderTransform);
            SetCylinderMeshMaterial = (delegate* unmanaged<ColliderMesh*, uint, uint, void>)Resolve(IntonerSignatures.SetCylinderMeshMaterial);
            SetCylinderMeshBounds = (delegate* unmanaged<ColliderMesh*, Vector3*, void>)ResolveJmpCall(IntonerSignatures.SetCylinderMeshBounds);
            CanCreateBg = TransformFromFile != null && TransformGetComponents != null && GetAnalyticColliderTransform != null
                && SetCylinderMeshMaterial != null && SetCylinderMeshBounds != null;
            ILayoutInstance.ILayoutInstanceVirtualTable* bgParts = ResolveVirtualTable(IntonerSignatures.BgPartsLayoutInstanceVirtualTable);
            ILayoutInstance.ILayoutInstanceVirtualTable* vfx = ResolveVirtualTable(IntonerSignatures.VfxLayoutInstanceVirtualTable);
            ILayoutInstance.ILayoutInstanceVirtualTable* envLocation = ResolveVirtualTable(IntonerSignatures.EnvLocationLayoutInstanceVirtualTable);
            ILayoutInstance.ILayoutInstanceVirtualTable* sharedGroup = ResolveVirtualTable(IntonerSignatures.SharedGroupLayoutInstanceVirtualTable);
            ILayoutInstance.ILayoutInstanceVirtualTable* triggerBox = ResolveVirtualTable(IntonerSignatures.TriggerBoxLayoutInstanceVirtualTable);
            ILayoutInstance.ILayoutInstanceVirtualTable* collisionBox = ResolveVirtualTable(IntonerSignatures.CollisionBoxLayoutInstanceVirtualTable);
            ILayoutInstance.ILayoutInstanceVirtualTable* housingObject = ResolveVirtualTable(IntonerSignatures.HousingObjectLayoutInstanceVirtualTable);
            ILayoutInstance.ILayoutInstanceVirtualTable* waterRange = ResolveVirtualTable(IntonerSignatures.WaterRangeLayoutInstanceVirtualTable);
            ILayoutInstance.ILayoutInstanceVirtualTable* mapRange = ResolveVirtualTable(IntonerSignatures.MapRangeLayoutInstanceVirtualTable);
            ReadOnlySpan<nint> required =
            [
                LayoutInstanceInit, ColliderFinalizer, SceneManagerUpdateScenes, BGCollisionModuleShutdown, BGCollisionModuleDtor, CameraSweepSphere, LayoutWorldShutdown, LayoutWorldDeinitialize,
                (nint)SceneManagerCtor, (nint)SceneManagerInit, (nint)Vector3Normalize, (nint)RaycastHitGetNormal,
                (nint)bgParts, (nint)sharedGroup, (nint)triggerBox, (nint)collisionBox,
                (nint)housingObject, (nint)waterRange, (nint)mapRange, (nint)vfx, (nint)envLocation,
            ];
            IsAvailable = !required.Contains(0);
            if (IsAvailable)
            {
                PrimaryCreators = new[] { (nint)triggerBox->CreatePrimary, (nint)collisionBox->CreatePrimary, (nint)housingObject->CreatePrimary }.Distinct().ToArray();
                SecondaryCreator = (nint)bgParts->CreateSecondary;
                Deinitializers = new[]
                {
                    (nint)bgParts->Deinit, (nint)sharedGroup->Deinit, (nint)triggerBox->Deinit,
                    (nint)collisionBox->Deinit, (nint)housingObject->Deinit, (nint)waterRange->Deinit,
                    (nint)mapRange->Deinit, (nint)vfx->Deinit, (nint)envLocation->Deinit,
                }.Distinct().ToArray();
                ActivitySetters = new[] { (nint)bgParts->SetActive, (nint)vfx->SetActive, (nint)envLocation->SetActive }.Distinct().ToArray();
                IsAvailable = SecondaryCreator != 0 && !PrimaryCreators.Contains(0) && !Deinitializers.Contains(0) && !ActivitySetters.Contains(0);
            }

            nint Resolve(NativeDirectSignatureTarget target)
                => NativeAddressResolver.TryScanSingleTextMatch(logger, scanner, target.Signature, target.Label);

            nint ResolveJmpCall(NativeRelativeBranchTarget target)
                => NativeAddressResolver.TryResolveJmpCallTarget(logger, scanner, target);

            ILayoutInstance.ILayoutInstanceVirtualTable* ResolveVirtualTable(NativeStaticAddressTarget target)
                => NativeAddressResolver.ReadVirtualTable(scanner,
                    (ILayoutInstance.ILayoutInstanceVirtualTable*)NativeAddressResolver.TryResolveStaticAddress(logger, scanner, target));
        }

        public SceneWrapper.SceneWrapperVirtualTable* GetSceneVirtualTable(SceneWrapper* scene)
            => NativeAddressResolver.ReadVirtualTable(_scanner, scene->VirtualTable);
    }
}
