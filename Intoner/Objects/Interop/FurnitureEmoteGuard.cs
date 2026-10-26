using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine.Group;
using Intoner.Logging;
using Intoner.Services.Interop;
using Microsoft.Extensions.Logging;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Intoner.Objects.Interop
{
    internal sealed unsafe class FurnitureEmoteGuard : FrameworkNativeService
    {
        [StructLayout(LayoutKind.Sequential, Size = 0x40)]
        private struct SnapResult
        {
            public Vector3 PrimaryPosition;
            public float PrimaryRotation;
            private fixed byte _padding0[12];
            public Vector3 FallbackPosition;
            public float FallbackRotation;
            private fixed byte _padding1[12];
            public nint LayoutInstance;
        }

        [return: MarshalAs(UnmanagedType.I1)]
        private delegate bool ResolveSnapDelegate(nint actor, SnapResult* result);

        private readonly ILogger<FurnitureEmoteGuard> _logger;
        private readonly IGameInteropProvider         _gameInteropProvider;

        private readonly nint _snapVariantZeroAddress;
        private readonly nint _snapVariantOneAddress;

        private readonly Lock _trackedInstancesLock = new();
        private readonly Dictionary<nint, HashSet<nint>> _trackedInstanceGroups = [];
        private readonly HashSet<nint> _trackedInstances = [];

        private Hook<EmoteManager.Delegates.ExecuteEmote>? _executeEmoteHook;
        private Hook<ResolveSnapDelegate>?  _snapVariantZeroHook;
        private Hook<ResolveSnapDelegate>?  _snapVariantOneHook;
        private int _available;

        public bool IsAvailable => !IsDisposing && Volatile.Read(ref _available) != 0;

        public FurnitureEmoteGuard(
            ILogger<FurnitureEmoteGuard> logger,
            IGameInteropProvider gameInteropProvider,
            ISigScanner sigScanner,
            IFramework framework)
            : base(framework, logger)
        {
            _logger              = logger;
            _gameInteropProvider = gameInteropProvider;

            _snapVariantZeroAddress = NativeAddressResolver.TryResolveJmpCallTarget(logger, sigScanner, IntonerSignatures.FurnitureSnapZero);
            _snapVariantOneAddress = NativeAddressResolver.TryResolveJmpCallTarget(logger, sigScanner, IntonerSignatures.FurnitureSnapOne);
        }

        protected override void InitializeCore()
        {
            try
            {
                _executeEmoteHook = Hooks.Add(InteropHookUtility.CreateHookFromAddress<EmoteManager.Delegates.ExecuteEmote>(
                    _logger, _gameInteropProvider, IntonerSignatures.FurnitureExecuteEmote, ExecuteEmoteDetour));
                _snapVariantZeroHook = Hooks.Add(InteropHookUtility.CreateHookFromAddress<ResolveSnapDelegate>(
                    _logger, _gameInteropProvider, _snapVariantZeroAddress, ResolveSnapVariantZeroDetour, IntonerSignatures.FurnitureSnapZero));
                _snapVariantOneHook = Hooks.Add(InteropHookUtility.CreateHookFromAddress<ResolveSnapDelegate>(
                    _logger, _gameInteropProvider, _snapVariantOneAddress, ResolveSnapVariantOneDetour, IntonerSignatures.FurnitureSnapOne));
                if (_executeEmoteHook is null || _snapVariantZeroHook is null || _snapVariantOneHook is null || !Hooks.Enable())
                {
                    throw new InvalidOperationException("required furniture emote hooks are unavailable");
                }

                Volatile.Write(ref _available, 1);
            }
            catch (Exception ex)
            {
                Hooks.Dispose();
                _logger.LogWarning(ex, "furniture emote guard failed to initialize emote hooks");
            }
        }

        public void RegisterInstanceTree(nint instance, IReadOnlyList<nint> instances)
        {
            if (instance == 0)
            {
                return;
            }

            HashSet<nint> trackedAddresses = new(instances);

            lock (_trackedInstancesLock)
            {
                if (IsDisposing)
                {
                    return;
                }

                RemoveTrackedGroup(instance);
                _trackedInstanceGroups[instance] = trackedAddresses;
                _trackedInstances.UnionWith(trackedAddresses);
            }
        }

        public void UnregisterInstanceTree(SharedGroupLayoutInstance* instance)
        {
            if (instance == null)
            {
                return;
            }

            lock (_trackedInstancesLock)
            {
                RemoveTrackedGroup((nint)instance);
            }
        }

        private bool ExecuteEmoteDetour(EmoteManager* emoteManager, ushort emoteId, EmoteController.PlayEmoteOption* playEmoteOption)
        {
            using InteropHookCollection.CallbackScope callback = Hooks.EnterCallback();

            if (_executeEmoteHook is null)
            {
                return false;
            }

            if (callback.IsActive && playEmoteOption != null && IsTrackedInstance((nint)playEmoteOption->Layout))
            {
                EmoteController.PlayEmoteOption sanitizedOption = *playEmoteOption;
                sanitizedOption.Layout = null;
                DiagnosticLog.Write(() => _logger.LogInformation(
                    "cleared helper furniture layout from play emote option for emote 0x{EmoteId:X}", emoteId));
                return _executeEmoteHook.OriginalDisposeSafe(emoteManager, emoteId, &sanitizedOption);
            }

            return _executeEmoteHook.OriginalDisposeSafe(emoteManager, emoteId, playEmoteOption);
        }

        private bool ResolveSnapVariantZeroDetour(nint actor, SnapResult* result)
            => ResolveSnapDetour(_snapVariantZeroHook, actor, result);

        private bool ResolveSnapVariantOneDetour(nint actor, SnapResult* result)
            => ResolveSnapDetour(_snapVariantOneHook, actor, result);

        private bool ResolveSnapDetour(Hook<ResolveSnapDelegate>? hook, nint actor, SnapResult* result)
        {
            using InteropHookCollection.CallbackScope callback = Hooks.EnterCallback();

            if (hook is null)
            {
                return false;
            }

            bool resolved = hook.OriginalDisposeSafe(actor, result);

            if (!resolved || result == null || !callback.IsActive || !IsTrackedInstance(result->LayoutInstance))
            {
                return resolved;
            }

            nint layoutInstance = result->LayoutInstance;
            *result = default;
            DiagnosticLog.Write(() => _logger.LogInformation(
                "blocked emote snap onto helper furniture instance 0x{Address:X}", (ulong)layoutInstance));
            return false;
        }

        internal bool IsTrackedInstance(nint layoutInstance)
        {
            lock (_trackedInstancesLock)
            {
                return _trackedInstances.Contains(layoutInstance);
            }
        }

        private void RemoveTrackedGroup(nint rootInstance)
        {
            if (!_trackedInstanceGroups.Remove(rootInstance, out HashSet<nint>? trackedAddresses))
            {
                return;
            }

            _trackedInstances.ExceptWith(trackedAddresses);
        }

        protected override void DisposeCore()
        {
            Volatile.Write(ref _available, 0);
            base.DisposeCore();
            lock (_trackedInstancesLock)
            {
                _trackedInstanceGroups.Clear();
                _trackedInstances.Clear();
            }
        }
    }
}
