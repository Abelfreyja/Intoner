using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine.Group;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine.Layer;
using Intoner.Objects.Assets;
using Intoner.Services.Interop;
using Microsoft.Extensions.Logging;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace Intoner.Objects.Interop
{
    [SuppressMessage("Security", "S6640", Justification = "layout bindings require typed unmanaged pointers")]
    internal sealed unsafe class ObjectLayoutBindings
    {
        public readonly nint LayoutInstanceInit;
        public readonly nint SoundLoadResources;
        private readonly delegate* unmanaged<SharedGroupLayoutInstance*, uint, ushort, byte> _setMaterial;

        public bool CanSetMaterial => _setMaterial != null;

        [StructLayout(LayoutKind.Explicit, Size = 0xE0)]
        private struct MaterialState
        {
            [FieldOffset(0xC8)] public uint Type;
            [FieldOffset(0xCC)] public ushort Slot0;
        }

        public bool TrySetWallpaper(SharedGroupLayoutInstance* instance, ushort variant)
        {
            if (_setMaterial == null || instance == null || instance->StainInfo == null || variant > 9999)
            {
                return false;
            }

            MaterialState* state = (MaterialState*)instance->StainInfo;
            if ((state->Type & 0xF) != 7)
            {
                return false;
            }

            if (instance->ResourceHandle == null)
            {
                return false;
            }

            byte target = HousingMaterialUtility.GetWallpaperInstanceId(instance->ResourceHandle->GetDataSpan());
            if (target == 0)
            {
                return false;
            }

            for (int index = 0; index < instance->Instances.Instances.Count; ++index)
            {
                var node = instance->Instances.Instances[index];
                ILayoutInstance* child = node.Value == null ? null : node.Value->Instance;
                if (child == null || child->Id.Type != InstanceType.BgPart || child->NestingLevel is < 1 or > 4
                    || (byte)(child->SubId >> (8 * (4 - child->NestingLevel))) != target)
                {
                    continue;
                }

                if (!child->IsPrimaryLoaded() || ((BgPartsLayoutInstance*)child)->GraphicsObject == null
                    || ((BgPartsLayoutInstance*)child)->GraphicsObject->StainBuffer == null)
                {
                    return false;
                }

                state->Slot0 = (ushort)(variant | 0x8000);
                return _setMaterial(instance, 0, variant) != 0;
            }

            return false;
        }

        public nint[] PrimaryCreators { get; } = [];
        public nint SecondaryCreator { get; }
        public nint[] Deinitializers { get; } = [];
        public nint[] ActivitySetters { get; } = [];
        public bool IsAvailable { get; }

        public ObjectLayoutBindings(ILogger<ObjectLayoutBindings> logger, ISigScanner scanner)
        {
            _setMaterial = (delegate* unmanaged<SharedGroupLayoutInstance*, uint, ushort, byte>)NativeAddressResolver.TryResolveJmpCallTarget(
                logger, scanner, IntonerSignatures.SharedGroupLayoutInstanceSetMaterial);
            SoundLoadResources = NativeAddressResolver.TryResolveJmpCallTarget(logger, scanner, IntonerSignatures.SoundLayoutInstanceLoadResources);
            ILayoutInstance.ILayoutInstanceVirtualTable* bgParts = ResolveVirtualTable(IntonerSignatures.BgPartsLayoutInstanceVirtualTable);
            ILayoutInstance.ILayoutInstanceVirtualTable* vfx = ResolveVirtualTable(IntonerSignatures.VfxLayoutInstanceVirtualTable);
            ILayoutInstance.ILayoutInstanceVirtualTable* sound = ResolveVirtualTable(IntonerSignatures.SoundLayoutInstanceVirtualTable);
            ILayoutInstance.ILayoutInstanceVirtualTable* envLocation = ResolveVirtualTable(IntonerSignatures.EnvLocationLayoutInstanceVirtualTable);
            ILayoutInstance.ILayoutInstanceVirtualTable* sharedGroup = ResolveVirtualTable(IntonerSignatures.SharedGroupLayoutInstanceVirtualTable);
            ILayoutInstance.ILayoutInstanceVirtualTable* triggerBox = ResolveVirtualTable(IntonerSignatures.TriggerBoxLayoutInstanceVirtualTable);
            ILayoutInstance.ILayoutInstanceVirtualTable* collisionBox = ResolveVirtualTable(IntonerSignatures.CollisionBoxLayoutInstanceVirtualTable);
            ILayoutInstance.ILayoutInstanceVirtualTable* housingObject = ResolveVirtualTable(IntonerSignatures.HousingObjectLayoutInstanceVirtualTable);
            ILayoutInstance.ILayoutInstanceVirtualTable* waterRange = ResolveVirtualTable(IntonerSignatures.WaterRangeLayoutInstanceVirtualTable);
            ILayoutInstance.ILayoutInstanceVirtualTable* mapRange = ResolveVirtualTable(IntonerSignatures.MapRangeLayoutInstanceVirtualTable);
            ReadOnlySpan<nint> required =
            [
                (nint)bgParts, (nint)sharedGroup, (nint)triggerBox, (nint)collisionBox,
                (nint)housingObject, (nint)waterRange, (nint)mapRange, (nint)vfx, (nint)envLocation, (nint)sound,
            ];
            IsAvailable = !required.Contains(0);

            if (IsAvailable)
            {
                LayoutInstanceInit = (nint)bgParts->Init;
                PrimaryCreators = new[] { (nint)triggerBox->CreatePrimary, (nint)collisionBox->CreatePrimary, (nint)housingObject->CreatePrimary, (nint)vfx->CreatePrimary }.Distinct().ToArray();
                SecondaryCreator = (nint)bgParts->CreateSecondary;
                Deinitializers = new[]
                {
                    (nint)bgParts->Deinit, (nint)sharedGroup->Deinit, (nint)triggerBox->Deinit,
                    (nint)collisionBox->Deinit, (nint)housingObject->Deinit, (nint)waterRange->Deinit,
                    (nint)mapRange->Deinit, (nint)vfx->Deinit, (nint)envLocation->Deinit, (nint)sound->Deinit,
                }.Distinct().ToArray();
                ActivitySetters = new[] { (nint)bgParts->SetActive, (nint)vfx->SetActive, (nint)envLocation->SetActive }.Distinct().ToArray();
                IsAvailable = LayoutInstanceInit != 0 && SecondaryCreator != 0 && !PrimaryCreators.Contains(0) && !Deinitializers.Contains(0) && !ActivitySetters.Contains(0);
            }

            ILayoutInstance.ILayoutInstanceVirtualTable* ResolveVirtualTable(NativeStaticAddressTarget target)
                => NativeAddressResolver.ReadVirtualTable(scanner,
                    (ILayoutInstance.ILayoutInstanceVirtualTable*)NativeAddressResolver.TryResolveStaticAddress(logger, scanner, target));
        }
    }
}
