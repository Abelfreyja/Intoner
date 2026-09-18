using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.System.File;
using FFXIVClientStructs.FFXIV.Client.System.Resource;
using FFXIVClientStructs.FFXIV.Client.System.Resource.Handle;
using FFXIVClientStructs.FFXIV.Client.System.Scheduler.Base;
using FFXIVClientStructs.FFXIV.Client.System.Scheduler.Instance;
using Microsoft.Extensions.Logging;
using Penumbra.GameData;
using System.Diagnostics;
using GraphicsContext = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Context;
using ImmediateContext = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.ImmediateContext;
using SceneBgObject = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.BgObject;
using SceneVfxObject = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.VfxObject;

namespace Intoner.Services.Interop;

/// <summary>signatures and native targets used by intoner</summary>
internal static unsafe class IntonerSignatures
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

    public const string HousingPlacementRaycast =
        "48 89 5C 24 ?? 48 89 74 24 ?? 48 89 7C 24 ?? 4C 89 74 24 ?? 55 48 8D 6C 24 ?? 48 81 EC ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 8D 55 ?? 48 8B F1 C7 44 24 ?? 01 00 00 00";

    public const string HousingPlacementSweepSphere =
        "48 8B C4 48 89 58 08 48 89 70 10 48 89 78 18 55 48 8D 68 D8 48 81 EC ?? ?? ?? ?? F3 0F 10 41 0C";

    public const string HousingPlacementAreaContainment =
        "48 89 5C 24 08 48 89 6C 24 10 48 89 74 24 18 57 48 81 EC C0 00 00 00 48 8B 05 ?? ?? ?? ?? 48 8B D9 48 8B 50 20 48 85 D2 0F 84 ?? ?? ?? ?? 83 BA 90 01 00 00 01 0F 85 ?? ?? ?? ?? E8 ?? ?? ?? ?? 0F B6 F0 81 FE FF 00 00 00 0F 84 ?? ?? ?? ?? F3 0F 10 03";

    // this one is horrible but will deal with it later..
    public const string HousingPlacementBlockForPosition =
        "48 89 5C 24 08 48 89 6C 24 10 48 89 74 24 18 57 41 56 41 57 48 81 EC D0 00 00 00 F3 0F 10 01 4C 8D 44 24 30 F3 0F 10 49 04 48 8D 54 24 40 48 8B 05 ?? ?? ?? ?? 41 B9 08 00 00 00 F3 0F 11 44 24 30 F3 0F 10 41 08 F3 0F 11 44 24 38 F3 0F 11 4C 24 34 48 8B 88 58 2B 00 00 E8 ?? ?? ?? ?? 84 C0 0F 84 ?? ?? ?? ?? 44 8B B4 24 C0 00 00 00 33 DB 45 85 F6 74 ?? 4C 8B 3D ?? ?? ?? ?? 49 8B 6F 20 48 85 ED 74 ?? 48 8B 4C DC 40 81 79 50 00 30 00 00 75 ?? 48 8B 71 58 B2 2B 48 8B FE C6 44 24 20 00 48 C1 EF 20 44 8B C6 44 8B CF 48 8B CD E8 ?? ?? ?? ?? 48 85 C0 75 ?? 49 8B 4F 18 44 8B CF 44 8B C6 88 44 24 20 B2 2B E8 ?? ?? ?? ?? 48 85 C0 74 ?? F6 80 97 00 00 00 20 74 ?? F6 80 96 00 00 00 0F 74";

    // scene rendering targets
    public static readonly NativeResolvedAddressTarget SceneRenderSetRenderTargets = new(
        (nint)GraphicsContext.MemberFunctionPointers.SetRenderTargets,
        "scene render context set render targets");
    public static readonly NativeResolvedAddressTarget SceneRenderAllocateCommand = new(
        (nint)GraphicsContext.MemberFunctionPointers.AllocateCommand,
        "scene render context allocate command");
    public static readonly NativeResolvedAddressTarget SceneRenderPushBackCommand = new(
        (nint)GraphicsContext.MemberFunctionPointers.PushBackCommand,
        "scene render context push command");
    public static readonly NativeResolvedAddressTarget SceneRenderSetTargetCommand = new(
        (nint)ImmediateContext.MemberFunctionPointers.DoSetTargetCommand,
        "scene render immediate context set target command");
    // resource targets
    public static readonly NativeDirectSignatureTarget ModelLoad = new(ModelResourceLoad, "object model resource load");
    public static readonly NativeDirectSignatureTarget ApricotLoad = new(ApricotResourceLoad, "object apricot resource load");
    public static readonly NativeDirectSignatureTarget ResourceHandleDestructor =
        new(Sigs.ResourceHandleDestructor, "object resource handle destruction");
    public static readonly NativeDirectSignatureTarget AvfxResourceBufferLoadHook =
        new(AvfxResourceBufferLoad, "AVFX resource buffer load");
    public static readonly NativeDirectSignatureTarget SharedGroupLayoutResourceLoadHook =
        new(SharedGroupLayoutResourceLoad, "object shared group resource load");
    public static readonly NativeDirectSignatureTarget CachedScheduleResource =
        new(GetCachedScheduleResource, "object scheduler cached resource");
    public static readonly NativeDirectSignatureTarget ResourceTextureOnLoad = new(TexHandleOnLoad, "object resource texture on load");
    public static readonly NativeDirectSignatureTarget ResourceSoundOnLoad = new(SoundOnLoad, "object resource sound on load");
    public static readonly NativeDirectSignatureTarget ResourceMemoryModelRead =
        new(MemoryModelResourceRead, "object memory model resource read");
    public static readonly NativeRelativeBranchTarget ResourceCheckFileState =
        new(CheckFileState, "object resource check file state");
    public static readonly NativeRelativeBranchTarget ResourceLoadMdlFileExtern =
        new(LoadMdlFileExtern, "object resource model extern load");
    public static readonly NativeDirectSignatureTarget ResourceReadFile = new(ReadFile, "object resource read file");
    public static readonly NativeDirectSignatureTarget ResourceLoadMdlFileLocal = new(LoadMdlFileLocal, "object resource model local load");
    public static readonly NativeDirectSignatureTarget ResourceLoadTexFileLocal = new(LoadTexFileLocal, "object resource texture local load");
    public static readonly NativeDirectSignatureTarget ResourceLoadScdFileLocal = new(LoadScdFileLocal, "object resource sound local load");
    public static readonly NativeDirectSignatureTarget ResourceUpdateTextureCategory =
        new(TexHandleUpdateCategory, "object resource texture category update");
    public static readonly NativeDirectSignatureTarget ResourceHandleTypeFromPath =
        new(GetResourceHandleType, "object resource handle type from path");
    public static readonly NativeStaticAddressTarget ResourceLodConfig =
        new(LodConfig, 0, "object texture lod config");
    public static readonly NativeResolvedAddressTarget ResourceFileDescriptorRead =
        new((nint)FileDescriptor.MemberFunctionPointers.Read, "object resource file descriptor read");
    public static readonly NativeResolvedAddressTarget ResourceFileJob =
        new((nint)FileThread.MemberFunctionPointers.DoFileJob, "object resource file job");
    public static readonly NativeResolvedAddressTarget ResourceSync =
        new((nint)ResourceManager.MemberFunctionPointers.GetResourceSync, "object resource sync");
    public static readonly NativeResolvedAddressTarget ResourceAsync =
        new((nint)ResourceManager.MemberFunctionPointers.GetResourceAsync, "object resource async");
    public static readonly NativeResolvedAddressTarget MaterialTextureLoad =
        new((nint)MaterialResourceHandle.MemberFunctionPointers.LoadTexFiles, "object material texture load");
    public static readonly NativeResolvedAddressTarget MaterialShaderLoad =
        new((nint)MaterialResourceHandle.MemberFunctionPointers.LoadShpkFiles, "object material shader load");
    public static readonly NativeResolvedAddressTarget ModelResourceLoadMaterials =
        new((nint)ModelResourceHandle.MemberFunctionPointers.LoadMaterials, "object model material resource load");
    public static readonly NativeResolvedAddressTarget ResourceHandleIncRef =
        new((nint)ResourceHandle.MemberFunctionPointers.IncRef, "object resource handle add reference");
    public static readonly NativeResolvedAddressTarget SchedulerTimelineLoadResources =
        new((nint)SchedulerTimeline.MemberFunctionPointers.LoadTimelineResources, "object scheduler timeline load resources");
    public static readonly NativeResolvedAddressTarget BgObjectAnimationLoad =
        new((nint)SceneBgObject.MemberFunctionPointers.LoadAnimationData, "object bg object load animation data");
    public static readonly NativeResolvedAddressTarget LayoutSharedGroupInsertObject =
        new((nint)LayoutSharedGroupObject.MemberFunctionPointers.InsertObject, "object layout shared group insert object");
    // asset discovery targets
    public static readonly NativeDirectSignatureTarget AssetStaticVfxRemove = new(StaticVfxRemove, "object asset static vfx remove");
    public static readonly NativeDirectSignatureTarget AssetActorVfxCreate = new(ActorVfxCreate, "object asset actor vfx create");
    public static readonly NativeRelativeBranchTarget AssetVfxTrigger = new(CallVfxTrigger, "object asset vfx trigger");
    public static readonly NativeRipRelativePointerTarget AssetActorVfxRemove =
        new(ActorVfxRemove, 7, "object asset actor vfx remove");
    public static readonly NativeResolvedAddressTarget AssetResourceSync =
        new((nint)ResourceManager.MemberFunctionPointers.GetResourceSync, "object asset resource sync");
    public static readonly NativeResolvedAddressTarget AssetResourceAsync =
        new((nint)ResourceManager.MemberFunctionPointers.GetResourceAsync, "object asset resource async");
    public static readonly NativeResolvedAddressTarget AssetStaticVfxCreate =
        new((nint)SceneVfxObject.MemberFunctionPointers.Create, "object asset static vfx create");
    public static readonly NativeRelativeBranchTarget NativeVfxPauseToggle =
        new(VfxPauseToggleCall, "VFX pause toggle");
    public static readonly NativeRelativeBranchTarget NativeVfxIsPaused =
        new(VfxIsPausedCall, "VFX paused state");
    public static readonly NativeDirectSignatureTarget NativeVfxSetSpeed =
        new(VfxSetSpeed, "VFX playback speed");
    // furniture and housing targets
    public static readonly NativeRelativeBranchTarget FurnitureSnapZero =
        new(FurnitureSnapVariantZero, "object furniture snap variant zero");
    public static readonly NativeRelativeBranchTarget FurnitureSnapOne =
        new(FurnitureSnapVariantOne, "object furniture snap variant one");
    public static readonly NativeRelativeBranchTarget NativeFurnitureCreate =
        new(FurnitureCreate, "object furniture create");
    public static readonly NativeRelativeBranchTarget NativeFurnitureDestroy =
        new(FurnitureDestroy, "object furniture destroy");
    public static readonly NativeRelativeBranchTarget NativeFurnitureApplyState =
        new(FurnitureApplyState, "object furniture apply state");
    public static readonly NativeResolvedAddressTarget FurnitureExecuteEmote =
        new((nint)EmoteManager.MemberFunctionPointers.ExecuteEmote, "object furniture emote execute");
    public static readonly NativeDirectSignatureTarget NativeHousingPlacementRaycast =
        new(HousingPlacementRaycast, "object native housing placement raycast");
    public static readonly NativeDirectSignatureTarget NativeHousingPlacementSweepSphere =
        new(HousingPlacementSweepSphere, "object native housing placement sphere sweep");
    public static readonly NativeDirectSignatureTarget NativeHousingPlacementAreaContainment =
        new(HousingPlacementAreaContainment, "object native housing placement area containment");
    public static readonly NativeDirectSignatureTarget NativeHousingPlacementBlockForPosition =
        new(HousingPlacementBlockForPosition, "object native housing placement block for position");

    private static readonly NativeDirectSignatureTarget[] DirectSignatureTargets =
    [
        // resource
        ModelLoad,
        ApricotLoad,
        ResourceHandleDestructor,
        CachedScheduleResource,
        AvfxResourceBufferLoadHook,
        SharedGroupLayoutResourceLoadHook,
        ResourceTextureOnLoad,
        ResourceSoundOnLoad,
        ResourceMemoryModelRead,

        // asset discovery
        AssetStaticVfxRemove,
        AssetActorVfxCreate,

        // resource delegates
        ResourceReadFile,
        ResourceLoadMdlFileLocal,
        ResourceLoadTexFileLocal,
        ResourceLoadScdFileLocal,
        ResourceUpdateTextureCategory,
        ResourceHandleTypeFromPath,

        // vfx playback
        NativeVfxSetSpeed,

        // furniture and housing delegates
        NativeHousingPlacementRaycast,
        NativeHousingPlacementSweepSphere,
        NativeHousingPlacementAreaContainment,
        NativeHousingPlacementBlockForPosition,
    ];

    private static readonly NativeRelativeBranchTarget[] RelativeBranchTargets =
    [
        // resource
        ResourceCheckFileState,
        ResourceLoadMdlFileExtern,

        // asset discovery
        AssetVfxTrigger,

        // furniture and housing
        FurnitureSnapZero,
        FurnitureSnapOne,

        // vfx playback
        NativeVfxPauseToggle,
        NativeVfxIsPaused,

        // furniture and housing
        NativeFurnitureCreate,
        NativeFurnitureDestroy,
        NativeFurnitureApplyState,
    ];

    private static readonly NativeRipRelativePointerTarget[] NativeRipRelativePointerTargets =
    [
        AssetActorVfxRemove,
    ];

    private static readonly NativeResolvedAddressTarget[] ResolvedAddressTargets =
    [
        // scene rendering
        SceneRenderSetRenderTargets,
        SceneRenderAllocateCommand,
        SceneRenderPushBackCommand,
        SceneRenderSetTargetCommand,

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

    private static readonly NativeStaticAddressTarget[] NativeStaticAddressTargets =
    [
        ResourceLodConfig,
    ];

    [Conditional("DEBUG")]
    public static void Verify(ISigScanner sigScanner, ILogger logger)
    {
        var failed = false;

        foreach (var target in DirectSignatureTargets)
        {
            failed |= !TryScanSingle(sigScanner, logger, target.Signature, target.Label, out _);
        }

        foreach (var target in NativeRipRelativePointerTargets)
        {
            if (!TryScanSingle(sigScanner, logger, target.Signature, target.Label, out nint address))
            {
                failed = true;
                continue;
            }

            if (NativeAddressResolver.TryResolveRipRelativePointerTarget(logger, sigScanner, address, target.Offset, target.Label) == nint.Zero)
            {
                logger.LogWarning("native target {Label} resolved a zero RIP pointer", target.Label);
                failed = true;
            }
        }

        foreach (var target in RelativeBranchTargets)
        {
            if (NativeAddressResolver.TryResolveJmpCallTarget(logger, sigScanner, target) == nint.Zero)
            {
                logger.LogWarning("native target {Label} resolved a zero JMP/CALL address", target.Label);
                failed = true;
            }
        }

        foreach (var target in ResolvedAddressTargets)
        {
            if (target.Address == nint.Zero)
            {
                logger.LogWarning("native target {Label} has a zero ClientStructs function pointer", target.Label);
                failed = true;
            }
        }

        foreach (var target in NativeStaticAddressTargets)
        {
            if (NativeAddressResolver.TryResolveStaticAddress(logger, sigScanner, target) == nint.Zero)
            {
                failed = true;
            }
        }

        if (failed)
        {
            logger.LogWarning("native target verification found unresolved targets");
        }
    }

    private static bool TryScanSingle(ISigScanner sigScanner, ILogger logger, string signature, string label, out nint address)
    {
        address = NativeAddressResolver.TryScanSingleTextMatch(logger, sigScanner, signature, label);
        return address != nint.Zero;
    }

}
