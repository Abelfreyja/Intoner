using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.FFXIV.Client.Sound;
using FFXIVClientStructs.FFXIV.Client.System.Resource.Handle;
using FFXIVClientStructs.SQEX.CDev.Engine.Sd.Driver;
using Intoner.Services.Interop;
using Microsoft.Extensions.Logging;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using SceneObject = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Object;

namespace Intoner.Objects.Interop
{
    internal sealed class ObjectSoundState(bool enabled = true)
    {
        public volatile bool Enabled = enabled;
    }

    [SuppressMessage("Security", "S6640", Justification = "layout and sound APIs require typed unmanaged pointers")]
    internal sealed unsafe class ObjectSoundService : FrameworkNativeService
    {
        private delegate VfxObject* VfxCtorDelegate(VfxObject* instance);
        private delegate void UpdateEmitterSoundDelegate(EmitterInstance* instance, nint context, float delta);
        private delegate void SetControllerVolumeDelegate(SoundController* controller, float volume, uint duration);

        private const int VfxListenerOffset = 0x90;
        private readonly ILogger<ObjectSoundService> _logger;
        private readonly IGameInteropProvider _interop;
        private readonly ObjectNativeOwnership _ownership;
        private readonly Lock _voicesLock = new();
        private readonly Dictionary<nint, VfxSoundState> _voices = [];
        private readonly ThreadLocal<ObjectSoundState?> _playingOwner = new();
        private readonly nint _vfxCtor;
        private readonly nint _vfxDtor;
        private readonly nint _updateEmitterSound;
        private readonly nint _updateSound;
        private readonly nint _getVolume;
        private readonly nint _setSoundResourceHandle;
        private readonly SetControllerVolumeDelegate? _setControllerVolume;
        private Hook<VfxCtorDelegate>? _vfxCtorHook;
        private Hook<SceneObject.Delegates.Dtor>? _vfxDtorHook;
        private Hook<UpdateEmitterSoundDelegate>? _updateEmitterSoundHook;
        private Hook<SoundManager.Delegates.AcquireSoundData>? _acquireSoundHook;
        private Hook<ISoundData.Delegates.SetSoundResourceHandle>? _setSoundResourceHandleHook;
        private Hook<ISoundData.Delegates.Update>? _updateSoundHook;
        private Hook<ISoundData.Delegates.GetVolume>? _getVolumeHook;
        private int _voiceCount;

        public ObjectSoundService(ILogger<ObjectSoundService> logger, IFramework framework,
            IGameInteropProvider interop, ISigScanner scanner, ObjectNativeOwnership ownership)
            : base(framework, logger)
        {
            _logger = logger;
            _interop = interop;
            _ownership = ownership;
            _vfxCtor = NativeAddressResolver.TryResolveJmpCallTarget(logger, scanner, IntonerSignatures.VfxObjectCtor);
            _updateEmitterSound = NativeAddressResolver.TryScanSingleTextMatch(logger, scanner,
                IntonerSignatures.EmitterInstanceUpdateSound.Signature, IntonerSignatures.EmitterInstanceUpdateSound.Label);
            _setControllerVolume = InteropHookUtility.CreateDelegateFromAddress<SetControllerVolumeDelegate>(logger,
                NativeAddressResolver.TryResolveJmpCallTarget(logger, scanner, IntonerSignatures.SoundControllerSetVolume),
                IntonerSignatures.SoundControllerSetVolume.Label);
            SceneObject.ObjectVirtualTable* vfxTable = NativeAddressResolver.ReadVirtualTable(scanner,
                (SceneObject.ObjectVirtualTable*)NativeAddressResolver.TryResolveStaticAddress(logger, scanner, IntonerSignatures.VfxObjectVirtualTable));
            ISoundData.ISoundDataVirtualTable* soundTable = NativeAddressResolver.ReadVirtualTable(scanner,
                (ISoundData.ISoundDataVirtualTable*)NativeAddressResolver.TryResolveStaticAddress(logger, scanner, IntonerSignatures.SoundDataVirtualTable));
            if (vfxTable != null)
            {
                _vfxDtor = (nint)vfxTable->Dtor;
            }

            if (soundTable != null)
            {
                _updateSound = (nint)soundTable->Update;
                _getVolume = (nint)soundTable->GetVolume;
                _setSoundResourceHandle = (nint)soundTable->SetSoundResourceHandle;
            }
        }

        protected override void InitializeCore()
        {
            try
            {
                if (_setControllerVolume is null)
                {
                    throw new InvalidOperationException("object sound volume binding is unavailable");
                }

                _vfxCtorHook = Hooks.CreateRequired<VfxCtorDelegate>(
                    _interop, _vfxCtor, VfxCtor, "VfxObject.Ctor");
                _vfxDtorHook = Hooks.CreateRequired<SceneObject.Delegates.Dtor>(
                    _interop, _vfxDtor, VfxDtor, "VfxObject.Dtor");
                _updateEmitterSoundHook = Hooks.CreateRequired<UpdateEmitterSoundDelegate>(
                    _interop, _updateEmitterSound, UpdateEmitterSound, "EmitterInstance.UpdateSound");
                _acquireSoundHook = Hooks.CreateRequired<SoundManager.Delegates.AcquireSoundData>(
                    _interop, (nint)SoundManager.MemberFunctionPointers.AcquireSoundData, AcquireSound, "SoundManager.AcquireSoundData");
                _setSoundResourceHandleHook = Hooks.CreateRequired<ISoundData.Delegates.SetSoundResourceHandle>(
                    _interop, _setSoundResourceHandle, SetSoundResourceHandle, "SoundData.SetSoundResourceHandle");
                _updateSoundHook = Hooks.CreateRequired<ISoundData.Delegates.Update>(
                    _interop, _updateSound, UpdateSound, "SoundData.Update");
                _getVolumeHook = Hooks.CreateRequired<ISoundData.Delegates.GetVolume>(
                    _interop, _getVolume, GetVolume, "SoundData.GetVolume");

                if (!Hooks.Enable())
                {
                    throw new InvalidOperationException("required object sound hooks are unavailable");
                }
            }
            catch (Exception ex)
            {
                Hooks.Dispose();
                _logger.LogWarning(ex, "object sound hooks could not start");
            }
        }

        private VfxObject* VfxCtor(VfxObject* instance)
        {
            using InteropHookCollection.CallbackScope callback = Hooks.EnterCallback();
            VfxObject* result = _vfxCtorHook!.OriginalDisposeSafe(instance);
            if (callback.IsActive)
            {
                _ownership.RegisterVfxListener((nint)instance + VfxListenerOffset,
                    _playingOwner.Value ?? (_ownership.CurrentOwner as FurnitureNativeInstance)?.Sound);
            }

            return result;
        }

        private SceneObject* VfxDtor(SceneObject* instance, byte freeFlags)
        {
            using InteropHookCollection.CallbackScope callback = Hooks.EnterCallback();

            if (callback.IsActive)
            {
                _ownership.RemoveVfxListener((nint)instance + VfxListenerOffset);
            }

            return _vfxDtorHook!.OriginalDisposeSafe(instance, freeFlags);
        }

        private void UpdateEmitterSound(EmitterInstance* instance, nint context, float delta)
        {
            using InteropHookCollection.CallbackScope callback = Hooks.EnterCallback();

            if (!callback.IsActive || !(instance->SoundStartTimeout >= 0))
            {
                _updateEmitterSoundHook!.OriginalDisposeSafe(instance, context, delta);
                return;
            }

            ObjectSoundState? owner = instance->Document != null
                ? _ownership.GetVfxOwner(instance->Document->Listener) : null;
            if (owner is { Enabled: false })
            {
                return;
            }

            if (owner is null && _playingOwner.Value is null)
            {
                _updateEmitterSoundHook!.OriginalDisposeSafe(instance, context, delta);
                return;
            }

            using CreationScope creation = EnterCreation(owner);
            _updateEmitterSoundHook!.OriginalDisposeSafe(instance, context, delta);

            if (instance->SoundData != null && GetVoice(instance->SoundData) is { } voice)
            {
                voice.Ready = true;
            }
        }

        public CreationScope EnterCreation(ObjectSoundState? sound)
            => new(this, sound);

        public readonly struct CreationScope : IDisposable
        {
            private readonly ObjectSoundService? _service;
            private readonly ObjectSoundState? _previous;

            internal CreationScope(ObjectSoundService service, ObjectSoundState? sound)
            {
                _service = service;
                _previous = service._playingOwner.Value;
                service._playingOwner.Value = sound;
            }

            public void Dispose()
            {
                if (_service is not null)
                {
                    _service._playingOwner.Value = _previous;
                }
            }
        }

        private SoundData* AcquireSound(SoundManager* manager)
        {
            using InteropHookCollection.CallbackScope callback = Hooks.EnterCallback();
            SoundData* sound = _acquireSoundHook!.OriginalDisposeSafe(manager);
            if (callback.IsActive && sound != null && _playingOwner.Value is { } owner)
            {
                lock (_voicesLock)
                {
                    _voices[(nint)sound] = new VfxSoundState(owner);
                    Volatile.Write(ref _voiceCount, _voices.Count);
                }
            }

            return sound;
        }

        private void SetSoundResourceHandle(ISoundData* sound, SoundResourceHandle* resource)
        {
            using InteropHookCollection.CallbackScope callback = Hooks.EnterCallback();
            _setSoundResourceHandleHook!.OriginalDisposeSafe(sound, resource);

            // ReleaseSoundData clears the resource after marking the pooled sound inactive
            if (callback.IsActive && resource == null && Volatile.Read(ref _voiceCount) != 0)
            {
                lock (_voicesLock)
                {
                    if (!sound->IsActive && _voices.Remove((nint)sound))
                    {
                        Volatile.Write(ref _voiceCount, _voices.Count);
                    }
                }
            }
        }

        private VfxSoundState? GetVoice(SoundData* sound)
        {
            if (Volatile.Read(ref _voiceCount) == 0)
            {
                return null;
            }

            lock (_voicesLock)
            {
                return _voices.GetValueOrDefault((nint)sound);
            }
        }

        private float GetVolume(ISoundData* sound)
        {
            using InteropHookCollection.CallbackScope callback = Hooks.EnterCallback();
            float volume = _getVolumeHook!.OriginalDisposeSafe(sound);
            SoundData* data = (SoundData*)sound;
            return callback.IsActive && data->IsLoadingSoundResource && GetVoice(data) is { } voice
                ? voice.GetLoadVolume(volume) : volume;
        }

        private void UpdateSound(ISoundData* sound, float delta)
        {
            using InteropHookCollection.CallbackScope callback = Hooks.EnterCallback();
            SoundData* data = (SoundData*)sound;
            if (callback.IsActive && GetVoice(data) is { } voice && voice.TryGetVolumeChange(data, out float volume))
            {
                _setControllerVolume!((SoundController*)&data->SoundController, volume, 0);
            }

            _updateSoundHook!.OriginalDisposeSafe(sound, delta);
        }

        protected override void DisposeCore()
        {
            base.DisposeCore();
            _playingOwner.Dispose();

            lock (_voicesLock)
            {
                _voices.Clear();
                Volatile.Write(ref _voiceCount, 0);
            }
        }

        internal sealed class VfxSoundState(ObjectSoundState owner)
        {
            internal volatile bool Ready;
            private volatile bool _enabled = true;

            internal float GetLoadVolume(float volume)
            {
                bool enabled = owner.Enabled;
                _enabled = enabled;
                return enabled ? volume : 0;
            }

            internal bool TryGetVolumeChange(SoundData* sound, out float result)
            {
                result = 0;

                if (!Ready || sound->IsLoadingSoundResource)
                {
                    return false;
                }

                bool enabled = owner.Enabled;

                if (_enabled == enabled)
                {
                    return false;
                }

                _enabled = enabled;
                result = enabled && !sound->IsFadeOutEnabled ? sound->Volume : 0;
                return true;
            }
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct EmitterInstance
        {
            [FieldOffset(0x1F8)] public DocumentInstance* Document;
            [FieldOffset(0x254)] public float SoundStartTimeout;
            [FieldOffset(0x300)] public SoundData* SoundData;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct DocumentInstance
        {
            [FieldOffset(0x270)] public nint Listener;
        }
    }
}
