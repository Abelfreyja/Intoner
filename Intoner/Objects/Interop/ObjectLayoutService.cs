using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine.Group;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine.Layer;
using FFXIVClientStructs.Interop;
using Intoner.Objects.Resources;
using Intoner.Services.Interop;
using Microsoft.Extensions.Logging;
using System.Diagnostics.CodeAnalysis;
using LayoutColliderType = FFXIVClientStructs.FFXIV.Client.LayoutEngine.Layer.ColliderType;

namespace Intoner.Objects.Interop
{
    [SuppressMessage("Security", "S6640", Justification = "layout bindings require typed unmanaged pointers")]
    internal sealed unsafe class ObjectLayoutService : FrameworkNativeService
    {
        private delegate byte LoadSoundResourcesDelegate(SoundLayoutInstance* instance, byte* path);

        private readonly ILogger<ObjectLayoutService>            _logger;
        private readonly IGameInteropProvider                    _interop;
        private readonly ObjectLayoutBindings                    _bindings;
        private readonly IObjectResourceLoader                   _resourceLoader;
        private readonly ObjectNativeOwnership                   _ownership;
        private Hook<ILayoutInstance.Delegates.Init>?            _initHook;
        private Hook<ILayoutInstance.Delegates.CreateSecondary>? _secondaryHook;
        private Hook<LoadSoundResourcesDelegate>?                _soundLoadHook;
        private int _available;

        public bool CanSetMaterial => !IsDisposing && _bindings.CanSetMaterial;

        public bool IsAvailable => !IsDisposing && Volatile.Read(ref _available) != 0;

        public ObjectLayoutService(ILogger<ObjectLayoutService> logger, IFramework framework,
            IGameInteropProvider interop, ObjectLayoutBindings bindings,
            IObjectResourceLoader resourceLoader, ObjectNativeOwnership ownership)
            : base(framework, logger)
        {
            _logger         = logger;
            _interop        = interop;
            _bindings       = bindings;
            _resourceLoader = resourceLoader;
            _ownership      = ownership;
        }

        protected override void InitializeCore()
        {
            try
            {
                if (!_bindings.IsAvailable || !_resourceLoader.EnableSharedGroupLoads())
                {
                    _logger.LogWarning("placed layout hooks are unavailable");
                    return;
                }

                _soundLoadHook = Hooks.Add(InteropHookUtility.CreateHookFromAddress<LoadSoundResourcesDelegate>(
                    _logger, _interop, _bindings.SoundLoadResources, LoadSoundResources, "furniture sound resources"));
                _ownership.SetCreators(_bindings.PrimaryCreators, _bindings.SecondaryCreator);
                _initHook = Hooks.CreateRequired(_interop, _bindings.LayoutInstanceInit, (ILayoutInstance.Delegates.Init)InitializeInstance, "layout initialization");
                foreach (nint deinit in _bindings.Deinitializers)
                {
                    AddDeinitHook(deinit);
                }

                foreach (nint setter in _bindings.ActivitySetters)
                {
                    AddActivityHook(setter);
                }

                foreach (nint primary in _bindings.PrimaryCreators)
                {
                    AddPrimaryHook(primary);
                }

                _secondaryHook = Hooks.CreateRequired(_interop, _bindings.SecondaryCreator, (ILayoutInstance.Delegates.CreateSecondary)CreateSecondary, "furniture secondary creation");

                if (!Hooks.Enable())
                {
                    throw new InvalidOperationException("placed layout hooks could not be enabled");
                }

                Volatile.Write(ref _available, 1);
            }
            catch (Exception ex)
            {
                Hooks.Dispose();
                _logger.LogWarning(ex, "placed layout hooks could not start");
            }
        }

        private byte LoadSoundResources(SoundLayoutInstance* instance, byte* path)
        {
            using InteropHookCollection.CallbackScope callback = Hooks.EnterCallback();
            FurnitureNativeInstance? owner = callback.IsActive ? _ownership.GetFurnitureOwner((ILayoutInstance*)instance) : null;

            if (owner is not null && !owner.Sound.Enabled && instance->PathCrc != 0 && path != null && *path != 0)
            {
                return 0;
            }

            bool canLoad = true;
            using ObjectResourceLoadScopeToken resources = owner is not null
                ? EnterResourceScope(owner, out canLoad) : default;
            return canLoad ? _soundLoadHook!.OriginalDisposeSafe(instance, path) : (byte)0;
        }

        private void InitializeInstance(ILayoutInstance* instance, void* creator, byte* path)
        {
            using InteropHookCollection.CallbackScope callback = Hooks.EnterCallback();
            _initHook!.OriginalDisposeSafe(instance, creator, path);

            if (callback.IsActive)
            {
                _ownership.Register(instance);
            }
        }

        private void AddDeinitHook(nint address)
        {
            Hook<ILayoutInstance.Delegates.Deinit>? hook = null;
            hook = Hooks.CreateRequired(_interop, address, (ILayoutInstance.Delegates.Deinit)(instance =>
            {
                using InteropHookCollection.CallbackScope callback = Hooks.EnterCallback();
                using ObjectNativeOwnership.Scope scope = callback.IsActive ? _ownership.EnterInstance(instance) : default;
                try
                {
                    hook!.OriginalDisposeSafe(instance);
                }
                finally
                {
                    if (callback.IsActive)
                    {
                        _ownership.Remove(instance);
                    }
                }
            }), "layout cleanup");
        }

        private void AddActivityHook(nint address)
        {
            Hook<ILayoutInstance.Delegates.SetActive>? hook = null;
            hook = Hooks.CreateRequired(_interop, address, (ILayoutInstance.Delegates.SetActive)((instance, active) =>
            {
                using InteropHookCollection.CallbackScope callback = Hooks.EnterCallback();
                hook!.OriginalDisposeSafe(instance, active);

                if (callback.IsActive && active && _ownership.IsHidden(instance))
                {
                    ObjectLayoutInterop.ApplyInstanceGraphicsVisibility(instance, false);
                }
            }), "layout activity");
        }

        private void AddPrimaryHook(nint address)
        {
            Hook<ILayoutInstance.Delegates.CreatePrimary>? hook = null;
            hook = Hooks.CreateRequired(_interop, address, (ILayoutInstance.Delegates.CreatePrimary)((instance, transform, pathOrType) =>
            {
                using InteropHookCollection.CallbackScope callback = Hooks.EnterCallback();
                using ObjectNativeOwnership.Scope scope = callback.IsActive ? _ownership.EnterInstance(instance) : default;

                FurnitureNativeInstance? owner = callback.IsActive ? _ownership.CurrentOwner as FurnitureNativeInstance : null;
                bool ownedMesh = owner is not null && _ownership.ContainsOwner(owner) && instance->Id.Type == InstanceType.CollisionBox
                    && (pathOrType == null || *(LayoutColliderType*)pathOrType == LayoutColliderType.Mesh);
                bool canLoad = true;
                using ObjectResourceLoadScopeToken resources = ownedMesh ? EnterResourceScope(owner!, out canLoad) : default;

                if (ownedMesh && (pathOrType == null || !canLoad || !HasValidCollisionBoxPath((CollisionBoxLayoutInstance*)instance)))
                {
                    return;
                }

                hook!.OriginalDisposeSafe(instance, transform, pathOrType);
            }), "furniture primary creation");
        }

        private static bool HasValidCollisionBoxPath(CollisionBoxLayoutInstance* instance)
        {
            LayoutManager* layout = instance->Layout;

            if (layout == null || !layout->CrcToPath.TryGetValue(instance->PcbPathCrc,
                    out Pointer<RefCountedString> path, copyCtor: false) || path.Value == null)
            {
                return false;
            }

            fixed (byte* bytes = path.Value->Data)
            {
                return ObjectResourcePathEncoding.TryReadPcbPath(bytes, out _);
            }
        }

        private void CreateSecondary(ILayoutInstance* instance)
        {
            using InteropHookCollection.CallbackScope callback = Hooks.EnterCallback();
            using ObjectNativeOwnership.Scope scope = callback.IsActive ? _ownership.EnterInstance(instance) : default;
            FurnitureNativeInstance? owner = callback.IsActive ? _ownership.CurrentOwner as FurnitureNativeInstance : null;
            bool canLoad = true;
            using ObjectResourceLoadScopeToken resources = owner is not null && _ownership.ContainsOwner(owner)
                ? EnterResourceScope(owner, out canLoad) : default;

            if (canLoad)
            {
                _secondaryHook!.OriginalDisposeSafe(instance);
            }
        }

        public bool TrySetWallpaper(FurnitureNativeInstance owner, ushort variant)
        {
            if (IsDisposing)
            {
                return false;
            }

            using ObjectResourceLoadScopeToken resources = EnterResourceScope(owner, out bool canLoad);
            return canLoad && _bindings.TrySetWallpaper(owner.Instance, variant);
        }

        public ObjectResourceLoadScopeToken EnterResourceScope(FurnitureNativeInstance owner, out bool canLoad)
        {
            canLoad = true;
            SharedGroupLayoutInstance* instance = owner.Instance;
            return instance == null ? default : _resourceLoader.EnterSharedGroupLoadScope(instance, out canLoad);
        }
    }
}
