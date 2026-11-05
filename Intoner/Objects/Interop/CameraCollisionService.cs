using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine.Group;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;
using Intoner.Logging;
using Intoner.Objects.Models;
using Intoner.Objects.Resources;
using Intoner.Scene;
using Intoner.Services.Interop;
using Intoner.Utils;
using Microsoft.Extensions.Logging;
using System.Numerics;
using Camera = FFXIVClientStructs.FFXIV.Client.Game.Camera;

namespace Intoner.Objects.Interop
{
    /// <summary> isolates placed collision and adds it to the normal camera's sweeps </summary>
    internal sealed unsafe class CameraCollisionService : FrameworkNativeService
    {
        private delegate Collider* PrimitiveFactoryDelegate(SceneWrapper* scene, ulong layer, Vector3* position, Vector3* rotation, Vector3* scale);
        private delegate void ColliderFinalizerDelegate(Collider* collider);
        private delegate void SceneManagerUpdateScenesDelegate(SceneManager* manager);
        private delegate byte BGCollisionModuleShutdownDelegate(BGCollisionModule* module);
        private delegate void BGCollisionModuleDtorDelegate(BGCollisionModule* module);
        private delegate byte LayoutWorldShutdownDelegate();
        private delegate void LayoutWorldDeinitializeDelegate();
        private delegate byte CameraSweepSphereDelegate(Camera* camera, Vector4* output, Vector3* start, Vector3* end,
            float radius, float maximumDistance, ulong material);

        private readonly ILogger<CameraCollisionService> _logger;
        private readonly IFramework                      _framework;
        private readonly IGameInteropProvider            _interop;
        private readonly CameraCollisionBindings         _bindings;
        private readonly ObjectNativeBindings            _nativeBindings;
        private readonly ObjectLayoutService             _layouts;
        private readonly ObjectResourceLoadScope         _resourceLoadScope;
        private readonly ObjectNativeOwnership           _ownership;

        private ObjectCollisionScene? _scene;
        private BGCollisionModule* _module;

        private Hook<ColliderFinalizerDelegate>? _colliderFinalizerHook;
        private Hook<SceneWrapper.Delegates.AddColliderMesh>? _meshHook;
        private Hook<SceneManagerUpdateScenesDelegate>? _updateHook;
        private Hook<BGCollisionModuleShutdownDelegate>? _moduleShutdownHook;
        private Hook<BGCollisionModuleDtorDelegate>? _moduleDestroyHook;
        private Hook<LayoutWorldShutdownDelegate>? _layoutShutdownHook;
        private Hook<LayoutWorldDeinitializeDelegate>? _layoutDeinitializeHook;
        private Hook<CameraSweepSphereDelegate>? _cameraHook;

        private int _acceptingRoots;
        private int _queryEnabled;
        private bool _engineStopping;

        protected override bool RequiresFrameworkDisposal => Volatile.Read(ref _scene) is not null;

        public bool IsAvailable => Volatile.Read(ref _acceptingRoots) != 0;
        public bool IsFurnitureAvailable => IsAvailable && _layouts.IsAvailable && _nativeBindings.Furniture.IsAvailable;
        public bool IsBgAvailable => IsAvailable && _scene is { CanCreateBg: true };

        public CameraCollisionService(
            ILogger<CameraCollisionService> logger,
            IFramework framework,
            IGameInteropProvider interop,
            CameraCollisionBindings bindings,
            ObjectNativeBindings nativeBindings,
            ObjectLayoutService layouts,
            ObjectResourceLoadScope resourceLoadScope,
            ObjectNativeOwnership ownership)
            : base(framework, logger)
        {
            _logger            = logger;
            _framework         = framework;
            _interop           = interop;
            _bindings          = bindings;
            _nativeBindings    = nativeBindings;
            _layouts           = layouts;
            _resourceLoadScope = resourceLoadScope;
            _ownership         = ownership;
        }

        protected override void InitializeCore()
        {
            try
            {
                if (!_bindings.IsAvailable || !_layouts.IsAvailable || !ObjectCollisionSceneQuery.TryResolveScene(out BGCollisionModule* module, out SceneWrapper* world))
                {
                    _logger.LogWarning("camera collision is unavailable");
                    return;
                }

                SceneWrapper.SceneWrapperVirtualTable* factories = _bindings.GetSceneVirtualTable(world);

                if (factories == null)
                {
                    throw new InvalidOperationException("collision scene virtual table is outside the game module");
                }

                _module = module;
                _scene = new ObjectCollisionScene(_bindings, module);
                _colliderFinalizerHook = Hooks.CreateRequired(_interop, _bindings.ColliderFinalizer, (ColliderFinalizerDelegate)FinalizeCollider, "camera collider destruction");
                _meshHook = Hooks.CreateRequired(_interop, (nint)factories->AddColliderMesh, (SceneWrapper.Delegates.AddColliderMesh)CreateMesh, "mesh collider creation");
                ReadOnlySpan<nint> primitiveFactories =
                [
                    (nint)factories->AddColliderMeshCylinder,
                    (nint)factories->AddColliderBox,
                    (nint)factories->AddColliderCylinder,
                    (nint)factories->AddColliderSphere,
                    (nint)factories->AddColliderPlane,
                    (nint)factories->AddColliderPlaneTwoSided,
                ];
                foreach (nint factory in primitiveFactories)
                {
                    AddPrimitiveHook(factory);
                }

                _updateHook = Hooks.CreateRequired(_interop, _bindings.SceneManagerUpdateScenes, (SceneManagerUpdateScenesDelegate)UpdateScenes, "collision scene update");
                _layoutShutdownHook = Hooks.CreateRequired(_interop, _bindings.LayoutWorldShutdown, (LayoutWorldShutdownDelegate)ShutdownLayout, "layout shutdown");
                _layoutDeinitializeHook = Hooks.CreateRequired(_interop, _bindings.LayoutWorldDeinitialize, (LayoutWorldDeinitializeDelegate)DeinitializeLayoutWorld, "layout deinitialization");
                _moduleShutdownHook = Hooks.CreateRequired(_interop, _bindings.BGCollisionModuleShutdown, (BGCollisionModuleShutdownDelegate)ShutdownModule, "collision module shutdown");
                _moduleDestroyHook = Hooks.CreateRequired(_interop, _bindings.BGCollisionModuleDtor, (BGCollisionModuleDtorDelegate)DestroyModule, "collision module destruction");
                _cameraHook = Hooks.CreateRequired(_interop, _bindings.CameraSweepSphere, (CameraSweepSphereDelegate)CameraSweepSphere, "camera collision sweep");
                if (!Hooks.Enable())
                {
                    throw new InvalidOperationException("camera collision hooks could not be enabled");
                }

                Volatile.Write(ref _queryEnabled, 1);
                Volatile.Write(ref _acceptingRoots, 1);
            }
            catch (Exception ex)
            {
                Volatile.Write(ref _acceptingRoots, 0);
                Interlocked.Exchange(ref _scene, null)?.Dispose();
                Hooks.Dispose();
                _logger.LogWarning(ex, "camera collision could not start");
            }
        }

        public FurnitureNativeInstance CreateRoot(bool visible, bool cameraCollision = true)
        {
            ObjectDisposedException.ThrowIf(IsDisposing || _engineStopping, this);

            if (!IsFurnitureAvailable)
            {
                throw new InvalidOperationException("furniture collision isolation is unavailable");
            }

            FurnitureNativeInstance root = new(this, visible, cameraCollision);
            _ownership.RegisterOwner(root);

            return root;
        }

        public ObjectNativeOwnership.Scope EnterCreation(FurnitureNativeInstance root)
            => _ownership.EnterCreation(root);

        internal void AttachRoot(FurnitureNativeInstance root, SharedGroupLayoutInstance* instance)
        {
            if (!_ownership.Contains((ILayoutInstance*)instance, root))
            {
                throw new InvalidOperationException("furniture ownership was not initialized");
            }

            root.Instance = instance;
        }

        public BgCollisionInstance CreateBg(BgColliderDefinition? definition, SceneTransform transform, bool visible, bool cameraCollision)
        {
            ObjectDisposedException.ThrowIf(IsDisposing || _engineStopping, this);
            bool isolated = IsBgAvailable;
            BgCollisionInstance instance = new(this, isolated, visible, cameraCollision);

            if (!isolated)
            {
                if (definition is { Kind: not BgCollisionKind.None })
                {
                    throw new InvalidOperationException("BG camera collision is unavailable");
                }

                return instance;
            }

            _ownership.RegisterOwner(instance);

            try
            {
                ApplyBg(instance, definition, transform, visible, cameraCollision);

                return instance;
            }
            catch
            {
                instance.Dispose();
                throw;
            }
        }

        internal void SetParticipation(FurnitureNativeInstance owner, bool visible, bool cameraCollision)
        {
            if (owner.Visible == visible && owner.CameraCollision == cameraCollision)
            {
                return;
            }

            if (_scene == null || _engineStopping || IsDisposing)
            {
                return;
            }

            using CollisionLock writeLock = CollisionLock.Write(_module);

            if (_ownership.ContainsOwner(owner))
            {
                _ownership.SetParticipation(owner, visible, cameraCollision);
            }
        }

        internal void ApplyBg(BgCollisionInstance instance, BgColliderDefinition? definition, SceneTransform transform,
            bool visible, bool cameraCollision, bool replace = false)
        {
            if (!instance.IsIsolated || _scene == null || _engineStopping || IsDisposing)
            {
                if (definition is { Kind: not BgCollisionKind.None })
                {
                    throw new InvalidOperationException("BG camera collision is unavailable");
                }

                instance.Definition = definition;
                instance.Transform = transform;

                if (!instance.IsIsolated && !_engineStopping && !IsDisposing
                 && (instance.Visible != visible || instance.CameraCollision != cameraCollision))
                {
                    _ownership.SetParticipation(instance, visible, cameraCollision);
                }

                return;
            }

            if (!replace && instance.Definition == definition && instance.Transform == transform
             && instance.Visible == visible && instance.CameraCollision == cameraCollision)
            {
                return;
            }

            using CollisionLock writeLock = CollisionLock.Write(_module);

            if (!_ownership.ContainsOwner(instance))
            {
                throw new InvalidOperationException("BG camera collision ownership is unavailable");
            }

            bool needsCollider = cameraCollision && definition is { Kind: not BgCollisionKind.None };
            bool create = needsCollider && (replace || instance.Instance == null || instance.Definition != definition);
            Collider* replacement = null;

            if (create)
            {
                using ObjectNativeOwnership.Scope ownership = _ownership.EnterCreation(instance);
                replacement = _scene.CreateBgCollider(definition!, transform);

                if (replacement == null)
                {
                    throw new InvalidOperationException("BG collider creation failed");
                }
            }

            if (!needsCollider || create)
            {
                RemoveBgCollider(instance);
                instance.Instance = replacement;
            }
            else if (instance.Transform != transform)
            {
                _scene.UpdateBgCollider(instance.Instance, definition!, transform);
            }

            instance.Definition = definition;
            instance.Transform = transform;

            if (instance.Visible != visible || instance.CameraCollision != cameraCollision)
            {
                _ownership.SetParticipation(instance, visible, cameraCollision);
            }
        }

        internal void DestroyBg(BgCollisionInstance instance)
        {
            if (!instance.IsIsolated || _scene == null)
            {
                return;
            }

            using CollisionLock writeLock = CollisionLock.Write(_module);

            if (!_ownership.RemoveOwner(instance))
            {
                return;
            }

            RemoveBgCollider(instance);
        }

        private void RemoveBgCollider(BgCollisionInstance instance)
        {
            Collider* collider = instance.TakeInstance();

            if (collider == null)
            {
                return;
            }

            collider->LayerMask = 0;
            _ownership.RemoveCollider(collider);
            _scene!.RemoveCollider(collider);
        }

        internal void DestroyRoot(FurnitureNativeInstance root)
        {
            SharedGroupLayoutInstance* instance = root.TakeInstance();
            _ownership.RemoveOwner(root);

            if (instance == null)
            {
                return;
            }

            using ObjectNativeOwnership.Scope scope = _ownership.EnterInstance((ILayoutInstance*)instance);
            _nativeBindings.Furniture.Destroy(instance);
        }

        protected override void DisposeCore()
        {
            RetireModule(_module);
            base.DisposeCore();
        }

        private void StopRoots()
        {
            _engineStopping = true;
            Volatile.Write(ref _queryEnabled, 0);
            Volatile.Write(ref _acceptingRoots, 0);

            foreach (ObjectCollisionOwner owner in _ownership.GetOwners())
            {
                owner.Dispose();
            }
        }

        private SceneWrapper* FactoryScene(SceneWrapper* scene)
            => _ownership.CurrentOwner is FurnitureNativeInstance owner && _ownership.ContainsOwner(owner) && _scene != null
                ? _scene.RouteFactory(scene) : scene;

        private ColliderMesh* CreateMesh(SceneWrapper* scene, ulong layer, byte* path, bool flag,
            Vector3* position, Vector3* rotation, Vector3* scale)
        {
            using InteropHookCollection.CallbackScope callback = Hooks.EnterCallback();
            SceneWrapper* target = callback.IsActive ? FactoryScene(scene) : scene;
            string requestPath = string.Empty;

            if (callback.IsActive && _ownership.CurrentOwner is { } owner && _ownership.ContainsOwner(owner)
             && _scene != null && target->Manager == _scene.Manager)
            {
                if (!ObjectResourcePathEncoding.TryReadPcbPath(path, out requestPath))
                {
                    return null;
                }

                requestPath = requestPath.Replace('\\', '/');
            }

            using ObjectResourceLoadScope.CollisionRequest? request = _resourceLoadScope.EnterCollisionRequest(requestPath);
            ColliderMesh* collider = _meshHook!.OriginalDisposeSafe(target, layer, path, flag, position, rotation, scale);

            if (callback.IsActive)
            {
                TrackCollider((Collider*)collider, layer);
            }

            return collider;
        }

        private void TrackCollider(Collider* collider, ulong layerMask)
        {
            if (collider != null && _scene != null && collider->Scene->Manager == _scene.Manager)
            {
                _ownership.RegisterCollider(collider, layerMask);
            }
        }

        private void FinalizeCollider(Collider* collider)
        {
            using InteropHookCollection.CallbackScope callback = Hooks.EnterCallback();

            if (callback.IsActive)
            {
                _ownership.RemoveCollider(collider);
            }

            _colliderFinalizerHook!.OriginalDisposeSafe(collider);
        }

        private void AddPrimitiveHook(nint address)
        {
            Hook<PrimitiveFactoryDelegate>? hook = null;
            hook = Hooks.CreateRequired(_interop, address, (PrimitiveFactoryDelegate)((scene, layer, position, rotation, scale) =>
            {
                using InteropHookCollection.CallbackScope callback = Hooks.EnterCallback();
                SceneWrapper* target = callback.IsActive ? FactoryScene(scene) : scene;
                Collider* collider = hook!.OriginalDisposeSafe(target, layer, position, rotation, scale);

                if (callback.IsActive)
                {
                    TrackCollider(collider, layer);
                }

                return collider;
            }), "primitive collider creation");
        }

        private void UpdateScenes(SceneManager* manager)
        {
            using InteropHookCollection.CallbackScope callback = Hooks.EnterCallback();
            _updateHook!.OriginalDisposeSafe(manager);

            if (!callback.IsActive)
            {
                return;
            }

            ObjectCollisionScene? scene = Volatile.Read(ref _scene);
            if (scene != null && scene.Manager != null && manager == _module->SceneManager)
            {
                scene.Manager->StreamingSphere = manager->StreamingSphere;
                _updateHook.OriginalDisposeSafe(scene.Manager);
            }
        }

        private byte ShutdownLayout()
        {
            using InteropHookCollection.CallbackScope callback = Hooks.EnterCallback();

            if (callback.IsActive)
            {
                StopRoots();
            }

            return _layoutShutdownHook!.OriginalDisposeSafe();
        }

        private void DeinitializeLayoutWorld()
        {
            using InteropHookCollection.CallbackScope callback = Hooks.EnterCallback();

            if (callback.IsActive)
            {
                StopRoots();
            }

            _layoutDeinitializeHook!.OriginalDisposeSafe();
        }

        private byte ShutdownModule(BGCollisionModule* module)
        {
            using InteropHookCollection.CallbackScope callback = Hooks.EnterCallback();

            if (callback.IsActive)
            {
                RetireModule(module);
            }

            return _moduleShutdownHook!.OriginalDisposeSafe(module);
        }

        private void DestroyModule(BGCollisionModule* module)
        {
            using InteropHookCollection.CallbackScope callback = Hooks.EnterCallback();

            if (callback.IsActive)
            {
                RetireModule(module);
            }

            _moduleDestroyHook!.OriginalDisposeSafe(module);
        }

        private void RetireModule(BGCollisionModule* module)
        {
            if (module == _module)
            {
                StopRoots();
                Interlocked.Exchange(ref _scene, null)?.Dispose();
            }
        }

        private byte CameraSweepSphere(Camera* camera, Vector4* output, Vector3* start, Vector3* end,
            float radius, float maximumDistance, ulong material)
        {
            using InteropHookCollection.CallbackScope callback = Hooks.EnterCallback();
            ObjectCollisionScene? scene = Volatile.Read(ref _scene);
            if (!callback.IsActive || Volatile.Read(ref _queryEnabled) == 0 || scene == null || !_framework.IsInFrameworkUpdateThread
                || FrameworkUnloadUtility.IsGameFrameworkDestroying())
            {
                return _cameraHook!.OriginalDisposeSafe(camera, output, start, end, radius, maximumDistance, material);
            }

            CameraManager* cameras = CameraManager.Instance();
            if (cameras == null || camera != cameras->Camera || camera != cameras->GetActiveCamera())
            {
                return _cameraHook!.OriginalDisposeSafe(camera, output, start, end, radius, maximumDistance, material);
            }

            try
            {
                // both hit pointers and their normals remain protected by this lock
                using CollisionLock readLock = CollisionLock.Read(_module);

                if (_ownership.HasCameraColliders
                 && scene.TrySweep(output, start, end, radius, maximumDistance, material, out byte hit))
                {
                    return hit;
                }
            }
            catch (Exception ex)
            {
                Volatile.Write(ref _queryEnabled, 0);
                DiagnosticLog.Write(() => _logger.LogWarning(ex, "camera collision query failed"));
            }

            return _cameraHook!.OriginalDisposeSafe(camera, output, start, end, radius, maximumDistance, material);
        }
    }
}
