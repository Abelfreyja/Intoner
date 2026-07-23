using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.System.File;
using FFXIVClientStructs.FFXIV.Client.System.Resource;
using FFXIVClientStructs.FFXIV.Client.System.Resource.Handle;
using FFXIVClientStructs.FFXIV.Client.System.Scheduler.Base;
using FFXIVClientStructs.FFXIV.Client.System.Scheduler.Instance;
using Intoner.Objects.Interop;
using Microsoft.Extensions.Logging;
using Penumbra.GameData;
using System.Diagnostics;
using SceneBgObject = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.BgObject;
using SceneVfxObject = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.VfxObject;

namespace Intoner.Objects;

/// <summary>signatures and native targets used by intoner</summary>
internal static unsafe class ObjectSignatures
{
    public const string LodConfig = Sigs.LodConfig;
    public const string ReadFile = Sigs.ReadFile;
    public const string CheckFileState = Sigs.CheckFileState;
    public const string LoadMdlFileExtern = Sigs.LoadMdlFileExtern;
    public const string LoadMdlFileLocal = Sigs.LoadMdlFileLocal;
    public const string LoadTexFileLocal = Sigs.LoadTexFileLocal;
    public const string TexHandleOnLoad = Sigs.TexHandleOnLoad;
    public const string TexHandleUpdateCategory = Sigs.TexHandleUpdateCategory;
    public const string SoundOnLoad = Sigs.SoundOnLoad;
    public const string LoadScdFileLocal = Sigs.LoadScdFileLocal;
    public const string RsfServiceAddress = Sigs.RsfServiceAddress;
    public const string MemoryModelResourceRead =
        "48 89 5C 24 ?? 48 89 6C 24 ?? 57 48 83 EC 20 80 3A 0B";

    public const string GetCachedScheduleResource = Sigs.GetCachedScheduleResource;
    public const string GetResourceHandleType =
        "40 53 48 83 EC ?? 44 0F BE 02";

    public const string ModelResourceLoad =
        "48 89 5C 24 ?? 48 89 74 24 ?? 57 48 83 EC ?? 41 0F B6 F0 48 8B DA 48 8B F9 41 80 F8";

    public const string ApricotResourceLoad =
        "48 89 74 24 ?? 57 48 83 EC ?? 41 0F B6 F0 48 8B F9 40 80 FE";

    public const string AvfxResourceBufferLoad =
        "4C 89 4C 24 ?? 48 89 54 24 ?? 48 89 4C 24 ?? 55 53 56 57";

    public const string SharedGroupLayoutResourceLoad =
        "40 53 48 83 EC 20 8B 81 F0 00 00 00 48 8B D9 24 0F 3C 01 0F 85 ?? ?? ?? ?? 48 39 51 08 0F 85";

    public const string StaticVfxRemove =
        "40 53 48 83 EC 20 48 8B D9 48 8B 89 ?? ?? ?? ?? 48 85 C9 74 28 33 D2 E8 ?? ?? ?? ?? 48 8B 8B ?? ?? ?? ?? 48 85 C9";

    public const string ActorVfxCreate =
        "40 53 55 56 57 48 81 EC ?? ?? ?? ?? 0F 29 B4 24 ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 ?? ?? ?? ?? 0F B6 AC 24 ?? ?? ?? ?? 0F 28 F3 49 8B F8";

    public const string ActorVfxRemove =
        "0F 11 48 10 48 8D 05";

    public const string VfxPauseToggleCall =
        "E8 ?? ?? ?? ?? 48 8B CB E8 ?? ?? ?? ?? 0F 2E C7";

    public const string VfxIsPausedCall =
        "E8 ?? ?? ?? ?? 41 3A C5 74 ?? 48 8B CB";

    public const string VfxSetSpeed =
        "F3 0F 11 89 ?? ?? ?? ?? 48 8B 89";

    public const string CallVfxTrigger =
        "E8 ?? ?? ?? ?? 0F B7 43 56";

    public const string FurnitureSnapVariantZero =
        "E8 ?? ?? ?? ?? 84 C0 74 ?? 4C 8D 74 24";

    public const string FurnitureSnapVariantOne =
        "E8 ?? ?? ?? ?? 84 C0 0F 84 ?? ?? ?? ?? 4C 8D 74 24";

    public const string FurnitureCreate =
        "E8 ?? ?? ?? ?? 48 89 04 ?? C6 44 ";

    public const string FurnitureDestroy =
        "E8 ?? ?? ?? ?? 44 88 73 ?? 48 85 FF";

    public const string FurnitureApplyState =
        "E8 ?? ?? ?? ?? 48 8B 8F ?? ?? ?? ?? 0F B6 47";

    public const string HousingFurnitureCullingUpdate =
        "48 8B C4 53 56 41 56 48 81 EC ?? ?? ?? ?? 48 89 68 ?? 48 8B D9 48 89 78 ?? 4C 89 68 ?? 4C 89 78 ?? 0F 29 70 ?? 0F 29 78 ?? 44 0F 29 40 ?? 48 8B 05 ?? ?? ?? ?? 48 8B 90 ?? ?? ?? ?? 48 85 D2";

    public const string HousingPlacementRaycast =
        "48 89 5C 24 ?? 48 89 74 24 ?? 48 89 7C 24 ?? 4C 89 74 24 ?? 55 48 8D 6C 24 ?? 48 81 EC ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 8D 55 ?? 48 8B F1 C7 44 24 ?? 01 00 00 00";

    public const string HousingPlacementSweepSphere =
        "48 8B C4 48 89 58 08 48 89 70 10 48 89 78 18 55 48 8D 68 D8 48 81 EC ?? ?? ?? ?? F3 0F 10 41 0C";

    public const string HousingPlacementAreaContainment =
        "48 89 5C 24 08 48 89 6C 24 10 48 89 74 24 18 57 48 81 EC C0 00 00 00 48 8B 05 ?? ?? ?? ?? 48 8B D9 48 8B 50 20 48 85 D2 0F 84 ?? ?? ?? ?? 83 BA 90 01 00 00 01 0F 85 ?? ?? ?? ?? E8 ?? ?? ?? ?? 0F B6 F0 81 FE FF 00 00 00 0F 84 ?? ?? ?? ?? F3 0F 10 03";

    // this one is horrible but will deal with it later..
    public const string HousingPlacementBlockForPosition =
        "48 89 5C 24 08 48 89 6C 24 10 48 89 74 24 18 57 41 56 41 57 48 81 EC D0 00 00 00 F3 0F 10 01 4C 8D 44 24 30 F3 0F 10 49 04 48 8D 54 24 40 48 8B 05 ?? ?? ?? ?? 41 B9 08 00 00 00 F3 0F 11 44 24 30 F3 0F 10 41 08 F3 0F 11 44 24 38 F3 0F 11 4C 24 34 48 8B 88 58 2B 00 00 E8 ?? ?? ?? ?? 84 C0 0F 84 ?? ?? ?? ?? 44 8B B4 24 C0 00 00 00 33 DB 45 85 F6 74 ?? 4C 8B 3D ?? ?? ?? ?? 49 8B 6F 20 48 85 ED 74 ?? 48 8B 4C DC 40 81 79 50 00 30 00 00 75 ?? 48 8B 71 58 B2 2B 48 8B FE C6 44 24 20 00 48 C1 EF 20 44 8B C6 44 8B CF 48 8B CD E8 ?? ?? ?? ?? 48 85 C0 75 ?? 49 8B 4F 18 44 8B CF 44 8B C6 88 44 24 20 B2 2B E8 ?? ?? ?? ?? 48 85 C0 74 ?? F6 80 97 00 00 00 20 74 ?? F6 80 96 00 00 00 0F 74";

    public const string ContextSetRenderTargets =
        "48 89 5C 24 08 48 89 6C 24 10 48 89 74 24 18 48 89 7C 24 20 41 56 48 83 EC 20 8B EA 4D 8B F1 BA 40 00 00 00 49 8B D8 48 8B F1 E8";

    public const string ContextAllocateCommand =
        "4C 8B D1 4C 8D 42 0F";

    public const string ContextPushBackCommand =
        "E8 ?? ?? ?? ?? 8B 6E 6C";

    private static readonly SignatureScanTarget[] SignatureScanTargets =
    [
        new(LodConfig, "object lod config"),
    ];

    // resource targets
    public static readonly SignatureHookTarget ModelLoad = new(ModelResourceLoad, "object model resource load");
    public static readonly SignatureHookTarget ApricotLoad = new(ApricotResourceLoad, "object apricot resource load");
    public static readonly SignatureHookTarget AvfxResourceBufferLoadHook =
        new(AvfxResourceBufferLoad, "AVFX resource buffer load");
    public static readonly SignatureHookTarget SharedGroupLayoutResourceLoadHook =
        new(SharedGroupLayoutResourceLoad, "object shared group resource load");
    public static readonly SignatureHookTarget CachedScheduleResource =
        new(GetCachedScheduleResource, "object scheduler cached resource");
    public static readonly SignatureHookTarget ResourceTextureOnLoad = new(TexHandleOnLoad, "object resource texture on load");
    public static readonly SignatureHookTarget ResourceSoundOnLoad = new(SoundOnLoad, "object resource sound on load");
    public static readonly SignatureHookTarget ResourceMemoryModelRead =
        new(MemoryModelResourceRead, "object memory model resource read");
    public static readonly JmpCallHookTarget ResourceCheckFileState =
        new(CheckFileState, "object resource check file state");
    public static readonly JmpCallHookTarget ResourceLoadMdlFileExtern =
        new(LoadMdlFileExtern, "object resource model extern load");
    public static readonly SignatureDelegateTarget ResourceReadFile = new(ReadFile, "object resource read file");
    public static readonly SignatureDelegateTarget ResourceLoadMdlFileLocal = new(LoadMdlFileLocal, "object resource model local load");
    public static readonly SignatureDelegateTarget ResourceLoadTexFileLocal = new(LoadTexFileLocal, "object resource texture local load");
    public static readonly SignatureDelegateTarget ResourceLoadScdFileLocal = new(LoadScdFileLocal, "object resource sound local load");
    public static readonly SignatureDelegateTarget ResourceUpdateTextureCategory =
        new(TexHandleUpdateCategory, "object resource texture category update");
    public static readonly SignatureDelegateTarget ResourceHandleTypeFromPath =
        new(GetResourceHandleType, "object resource handle type from path");
    public static readonly StaticAddressTarget ResourceRsfService =
        new(RsfServiceAddress, 0, "object resource RSF service");
    public static readonly AddressHookTarget ResourceFileDescriptorRead =
        new((nint)FileDescriptor.MemberFunctionPointers.Read, "object resource file descriptor read");
    public static readonly AddressHookTarget ResourceFileJob =
        new((nint)FileThread.MemberFunctionPointers.DoFileJob, "object resource file job");
    public static readonly AddressHookTarget ResourceSync =
        new((nint)ResourceManager.MemberFunctionPointers.GetResourceSync, "object resource sync");
    public static readonly AddressHookTarget ResourceAsync =
        new((nint)ResourceManager.MemberFunctionPointers.GetResourceAsync, "object resource async");
    public static readonly AddressHookTarget MaterialTextureLoad =
        new((nint)MaterialResourceHandle.MemberFunctionPointers.LoadTexFiles, "object material texture load");
    public static readonly AddressHookTarget MaterialShaderLoad =
        new((nint)MaterialResourceHandle.MemberFunctionPointers.LoadShpkFiles, "object material shader load");
    public static readonly AddressHookTarget ModelResourceLoadMaterials =
        new((nint)ModelResourceHandle.MemberFunctionPointers.LoadMaterials, "object model material resource load");
    public static readonly AddressHookTarget ResourceHandleIncRef =
        new((nint)ResourceHandle.MemberFunctionPointers.IncRef, "object resource handle add reference");
    public static readonly AddressHookTarget SchedulerTimelineLoadResources =
        new((nint)SchedulerTimeline.MemberFunctionPointers.LoadTimelineResources, "object scheduler timeline load resources");
    public static readonly AddressHookTarget BgObjectAnimationLoad =
        new((nint)SceneBgObject.MemberFunctionPointers.LoadAnimationData, "object bg object load animation data");
    public static readonly AddressHookTarget LayoutSharedGroupInsertObject =
        new((nint)LayoutSharedGroupObject.MemberFunctionPointers.InsertObject, "object layout shared group insert object");
    // asset discovery targets
    public static readonly SignatureHookTarget AssetStaticVfxRemove = new(StaticVfxRemove, "object asset static vfx remove");
    public static readonly SignatureHookTarget AssetActorVfxCreate = new(ActorVfxCreate, "object asset actor vfx create");
    public static readonly JmpCallHookTarget AssetVfxTrigger = new(CallVfxTrigger, "object asset vfx trigger");
    public static readonly RipRelativePointerTarget AssetActorVfxRemove =
        new(ActorVfxRemove, 7, "object asset actor vfx remove");
    public static readonly AddressHookTarget AssetResourceSync =
        new((nint)ResourceManager.MemberFunctionPointers.GetResourceSync, "object asset resource sync");
    public static readonly AddressHookTarget AssetResourceAsync =
        new((nint)ResourceManager.MemberFunctionPointers.GetResourceAsync, "object asset resource async");
    public static readonly AddressHookTarget AssetStaticVfxCreate =
        new((nint)SceneVfxObject.MemberFunctionPointers.Create, "object asset static vfx create");
    public static readonly JmpCallAddressTarget NativeVfxPauseToggle =
        new(VfxPauseToggleCall, "VFX pause toggle");
    public static readonly JmpCallAddressTarget NativeVfxIsPaused =
        new(VfxIsPausedCall, "VFX paused state");
    public static readonly SignatureDelegateTarget NativeVfxSetSpeed =
        new(VfxSetSpeed, "VFX playback speed");
    // furniture and housing targets
    public static readonly SignatureHookTarget HousingFurnitureCulling =
        new(HousingFurnitureCullingUpdate, "object housing furniture culling update");
    public static readonly JmpCallHookTarget FurnitureSnapZero =
        new(FurnitureSnapVariantZero, "object furniture snap variant zero");
    public static readonly JmpCallHookTarget FurnitureSnapOne =
        new(FurnitureSnapVariantOne, "object furniture snap variant one");
    public static readonly JmpCallAddressTarget NativeFurnitureCreate =
        new(FurnitureCreate, "object furniture create");
    public static readonly JmpCallAddressTarget NativeFurnitureDestroy =
        new(FurnitureDestroy, "object furniture destroy");
    public static readonly JmpCallAddressTarget NativeFurnitureApplyState =
        new(FurnitureApplyState, "object furniture apply state");
    public static readonly AddressHookTarget FurnitureExecuteEmote =
        new((nint)EmoteManager.MemberFunctionPointers.ExecuteEmote, "object furniture emote execute");
    public static readonly SignatureDelegateTarget NativeHousingPlacementRaycast =
        new(HousingPlacementRaycast, "object native housing placement raycast");
    public static readonly SignatureDelegateTarget NativeHousingPlacementSweepSphere =
        new(HousingPlacementSweepSphere, "object native housing placement sphere sweep");
    public static readonly SignatureDelegateTarget NativeHousingPlacementAreaContainment =
        new(HousingPlacementAreaContainment, "object native housing placement area containment");
    public static readonly SignatureDelegateTarget NativeHousingPlacementBlockForPosition =
        new(HousingPlacementBlockForPosition, "object native housing placement block for position");

    // native draw targets
    public static readonly SignatureHookTarget NativeContextSetRenderTargets =
        new(ContextSetRenderTargets, "object native context set render targets");
    public static readonly SignatureDelegateTarget NativeContextAllocateCommand =
        new(ContextAllocateCommand, "object native context allocate command");
    public static readonly JmpCallAddressTarget NativeContextPushBackCommand =
        new(ContextPushBackCommand, "object native context push command");

    private static readonly SignatureHookTarget[] SignatureHookTargets =
    [
        // resource
        ModelLoad,
        ApricotLoad,
        CachedScheduleResource,
        AvfxResourceBufferLoadHook,
        ResourceTextureOnLoad,
        ResourceSoundOnLoad,
        ResourceMemoryModelRead,

        // asset discovery
        AssetStaticVfxRemove,
        AssetActorVfxCreate,

        // furniture and housing
        HousingFurnitureCulling,

        // native draw
        NativeContextSetRenderTargets,
    ];

    private static readonly JmpCallHookTarget[] JmpCallHookTargets =
    [
        // resource
        ResourceCheckFileState,
        ResourceLoadMdlFileExtern,

        // asset discovery
        AssetVfxTrigger,

        // furniture and housing
        FurnitureSnapZero,
        FurnitureSnapOne,
    ];

    private static readonly SignatureDelegateTarget[] SignatureDelegateTargets =
    [
        // resource
        ResourceReadFile,
        ResourceLoadMdlFileLocal,
        ResourceLoadTexFileLocal,
        ResourceLoadScdFileLocal,
        ResourceUpdateTextureCategory,
        ResourceHandleTypeFromPath,

        // vfx playback
        NativeVfxSetSpeed,

        // furniture and housing
        NativeHousingPlacementRaycast,
        NativeHousingPlacementSweepSphere,
        NativeHousingPlacementAreaContainment,
        NativeHousingPlacementBlockForPosition,

        // native draw
        NativeContextAllocateCommand,
    ];

    private static readonly RipRelativePointerTarget[] RipRelativePointerTargets =
    [
        AssetActorVfxRemove,
    ];

    private static readonly JmpCallAddressTarget[] JmpCallAddressTargets =
    [
        // vfx playback
        NativeVfxPauseToggle,
        NativeVfxIsPaused,

        // furniture and housing
        NativeFurnitureCreate,
        NativeFurnitureDestroy,
        NativeFurnitureApplyState,

        // native draw
        NativeContextPushBackCommand,
    ];

    private static readonly AddressHookTarget[] AddressHookTargets =
    [
        // resource
        ResourceFileDescriptorRead,
        ResourceFileJob,
        ResourceSync,
        ResourceAsync,
        MaterialTextureLoad,
        MaterialShaderLoad,
        ModelResourceLoadMaterials,
        ResourceHandleIncRef,
        SchedulerTimelineLoadResources,
        BgObjectAnimationLoad,
        LayoutSharedGroupInsertObject,

        // asset discovery
        AssetResourceSync,
        AssetResourceAsync,
        AssetStaticVfxCreate,

        // furniture and housing
        FurnitureExecuteEmote,
    ];

    private static readonly StaticAddressTarget[] StaticAddressTargets =
    [
        ResourceRsfService,
    ];

    [Conditional("DEBUG")]
    public static void TestSignatures(ISigScanner sigScanner, ILogger logger)
    {
        var failed = false;

        foreach (var target in SignatureScanTargets)
        {
            failed |= !TryScanSingle(sigScanner, logger, target.Signature, target.Label, out _);
        }

        foreach (var target in SignatureHookTargets)
        {
            failed |= !TryScanSingle(sigScanner, logger, target.Signature, target.Label, out _);
        }

        foreach (var target in SignatureDelegateTargets)
        {
            failed |= !TryScanSingle(sigScanner, logger, target.Signature, target.Label, out _);
        }

        foreach (var target in RipRelativePointerTargets)
        {
            if (!TryScanSingle(sigScanner, logger, target.Signature, target.Label, out nint address))
            {
                failed = true;
                continue;
            }

            if (ObjectNativeAddressResolver.TryResolveRipRelativePointerTarget(logger, sigScanner, address, target.Offset, target.Label) == nint.Zero)
            {
                logger.LogWarning("object signature {Label} resolved a zero RIP pointer target", target.Label);
                failed = true;
            }
        }

        foreach (var target in JmpCallHookTargets)
        {
            if (ObjectNativeAddressResolver.TryResolveJmpCallTarget(logger, sigScanner, target) == nint.Zero)
            {
                logger.LogWarning("object signature {Label} resolved a zero JMP/CALL target", target.Label);
                failed = true;
            }
        }

        foreach (var target in JmpCallAddressTargets)
        {
            if (ObjectNativeAddressResolver.TryResolveJmpCallTarget(logger, sigScanner, target) == nint.Zero)
            {
                logger.LogWarning("object signature {Label} resolved a zero JMP/CALL target", target.Label);
                failed = true;
            }
        }

        foreach (var target in AddressHookTargets)
        {
            if (target.Address == nint.Zero)
            {
                logger.LogWarning("object signature {Label} has a zero ClientStructs function pointer target", target.Label);
                failed = true;
            }
        }

        foreach (var target in StaticAddressTargets)
        {
            if (ObjectNativeAddressResolver.TryResolveStaticAddress(logger, sigScanner, target) == nint.Zero)
            {
                failed = true;
            }
        }

        if (failed)
        {
            logger.LogWarning("object signature verification found unresolved targets");
        }
    }

    private static bool TryScanSingle(ISigScanner sigScanner, ILogger logger, string signature, string label, out nint address)
    {
        address = ObjectNativeAddressResolver.TryScanSingleTextMatch(logger, sigScanner, signature, label);
        return address != nint.Zero;
    }

    public readonly record struct SignatureHookTarget(string Signature, string Label);
    public readonly record struct SignatureDelegateTarget(string Signature, string Label);
    public readonly record struct RipRelativePointerTarget(string Signature, int Offset, string Label);
    public readonly record struct JmpCallHookTarget(string Signature, string Label);
    public readonly record struct JmpCallAddressTarget(string Signature, string Label);
    public readonly record struct AddressHookTarget(nint Address, string Label);
    public readonly record struct StaticAddressTarget(string Signature, int Offset, string Label);
    private readonly record struct SignatureScanTarget(string Signature, string Label);
}
