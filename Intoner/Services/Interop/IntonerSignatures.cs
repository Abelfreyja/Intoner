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

    public const string ActorVfxCreate =
        "40 53 55 56 57 48 81 EC ?? ?? ?? ?? 0F 29 B4 24 ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 ?? ?? ?? ?? 0F B6 AC 24 ?? ?? ?? ?? 0F 28 F3 49 8B F8";

    public const string VfxPauseToggleCall =
        "E8 ?? ?? ?? ?? 48 8B CB E8 ?? ?? ?? ?? 0F 2E C7";

    public const string VfxIsPausedCall =
        "E8 ?? ?? ?? ?? 41 3A C5 74 ?? 48 8B CB";

    public const string VfxSetSpeed =
        "F3 0F 11 89 ?? ?? ?? ?? 48 8B 89";

    public const string FurnitureSnapVariantZero =
        "E8 ?? ?? ?? ?? 84 C0 74 ?? 4C 8D 74 24";

    public const string FurnitureSnapVariantOne =
        "E8 ?? ?? ?? ?? 84 C0 0F 84 ?? ?? ?? ?? 4C 8D 74 24";

    public const string FurnitureCreate =
        "E8 ?? ?? ?? ?? 48 89 04 ?? C6 44 ";

    public const string FurnitureDestroy =
        "E8 ?? ?? ?? ?? 44 88 73 ?? 48 85 FF";

    public const string HousingPlacementRaycast =
        "48 89 5C 24 ?? 48 89 74 24 ?? 48 89 7C 24 ?? 4C 89 74 24 ?? 55 48 8D 6C 24 ?? 48 81 EC ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 8D 55 ?? 48 8B F1 C7 44 24 ?? 01 00 00 00";

    public const string HousingPlacementSweepSphere =
        "48 8B C4 48 89 58 08 48 89 70 10 48 89 78 18 55 48 8D 68 D8 48 81 EC ?? ?? ?? ?? F3 0F 10 41 0C";

    public const string HousingPlacementAreaContainment =
        "48 89 5C 24 08 48 89 6C 24 10 48 89 74 24 18 57 48 81 EC C0 00 00 00 48 8B 05 ?? ?? ?? ?? 48 8B D9 48 8B 50 20 48 85 D2 0F 84 ?? ?? ?? ?? 83 BA 90 01 00 00 01 0F 85 ?? ?? ?? ?? E8 ?? ?? ?? ?? 0F B6 F0 81 FE FF 00 00 00 0F 84 ?? ?? ?? ?? F3 0F 10 03";

    // this one is horrible but will deal with it later..
    public const string HousingPlacementBlockForPosition =
        "48 89 5C 24 08 48 89 6C 24 10 48 89 74 24 18 57 41 56 41 57 48 81 EC D0 00 00 00 F3 0F 10 01 4C 8D 44 24 30 F3 0F 10 49 04 48 8D 54 24 40 48 8B 05 ?? ?? ?? ?? 41 B9 08 00 00 00 F3 0F 11 44 24 30 F3 0F 10 41 08 F3 0F 11 44 24 38 F3 0F 11 4C 24 34 48 8B 88 58 2B 00 00 E8 ?? ?? ?? ?? 84 C0 0F 84 ?? ?? ?? ?? 44 8B B4 24 C0 00 00 00 33 DB 45 85 F6 74 ?? 4C 8B 3D ?? ?? ?? ?? 49 8B 6F 20 48 85 ED 74 ?? 48 8B 4C DC 40 81 79 50 00 30 00 00 75 ?? 48 8B 71 58 B2 2B 48 8B FE C6 44 24 20 00 48 C1 EF 20 44 8B C6 44 8B CF 48 8B CD E8 ?? ?? ?? ?? 48 85 C0 75 ?? 49 8B 4F 18 44 8B CF 44 8B C6 88 44 24 20 B2 2B E8 ?? ?? ?? ?? 48 85 C0 74 ?? F6 80 97 00 00 00 20 74 ?? F6 80 96 00 00 00 0F 74";

    // layout and collision targets
    public static readonly NativeRelativeBranchTarget ILayoutInstanceInit =
        new("E8 ?? ?? ?? ?? EB ?? 41 FF D1 48 8B 4D", "ILayoutInstance.Init");
    public static readonly NativeDirectSignatureTarget SceneManagerCtor =
        new("48 8D 05 ?? ?? ?? ?? C7 41 ?? 00 00 C8 42", "SceneManager.Ctor");
    public static readonly NativeDirectSignatureTarget SceneManagerInit =
        new("48 89 5C 24 ?? 57 48 83 EC 20 44 89 41", "SceneManager.Init");
    public static readonly NativeDirectSignatureTarget SceneManagerUpdateScenes =
        new("48 89 5C 24 ?? 57 48 83 EC 30 48 8B 59 ?? 48 8B F9 48 85 DB", "SceneManager.UpdateScenes");
    public static readonly NativeDirectSignatureTarget BGCollisionModuleShutdown =
        new("48 83 EC 28 C6 41 ?? 01 48 8B 49", "BGCollisionModule.Shutdown");
    public static readonly NativeRelativeBranchTarget BGCollisionModuleDtor =
        new("E8 ?? ?? ?? ?? BA A0 00 00 00 48 8B CF E8 ?? ?? ?? ?? 48 89 B3 ?? ?? ?? ?? 48 8B 0D", "BGCollisionModule.Dtor");
    public static readonly NativeRelativeBranchTarget BGCollisionVector3Normalize =
        new("E8 ?? ?? ?? ?? 0F 57 DB 4C 8D 44 24", "BGCollision.Math.Vector3.Normalize");
    public static readonly NativeRelativeBranchTarget RaycastHitGetNormal =
        new("E8 ?? ?? ?? ?? 84 C0 74 ?? F3 0F 10 65", "RaycastHit.GetNormal");
    public static readonly NativeRelativeBranchTarget CameraSweepSphere =
        new("E8 ?? ?? ?? ?? F3 44 0F 10 65 ?? F3 0F 10 75", "camera sphere sweep");
    public static readonly NativeStaticAddressTarget BgPartsLayoutInstanceVirtualTable =
        new("48 83 C0 10 48 8D 0D ?? ?? ?? ?? 0F 1F 44 00 ?? 4C 89 70", 7, "BgPartsLayoutInstance virtual table");
    public static readonly NativeStaticAddressTarget VfxLayoutInstanceVirtualTable =
        new("48 8D 05 ?? ?? ?? ?? 48 89 01 33 D2 48 89 51 ?? 48 89 51", 3, "VfxLayoutInstance virtual table");
    public static readonly NativeStaticAddressTarget EnvLocationLayoutInstanceVirtualTable =
        new("48 8D 0D ?? ?? ?? ?? 4C 89 70 ?? 4C 89 70 ?? 44 89 70 ?? 4C 89 70 ?? 4C 89 70 ?? 4C 89 70", 3, "EnvLocationLayoutInstance virtual table");
    public static readonly NativeStaticAddressTarget SharedGroupLayoutInstanceVirtualTable =
        new("48 89 77 ?? 48 8D 05 ?? ?? ?? ?? 48 89 07 48 8D 05", 7, "SharedGroupLayoutInstance virtual table");
    public static readonly NativeStaticAddressTarget TriggerBoxLayoutInstanceVirtualTable =
        new("48 83 C0 68 48 8D 0D ?? ?? ?? ?? 48 8D 15 ?? ?? ?? ?? 0F 1F 44 00", 7, "TriggerBoxLayoutInstance virtual table");
    public static readonly NativeStaticAddressTarget CollisionBoxLayoutInstanceVirtualTable =
        new("45 33 FF 4C 8D 25 ?? ?? ?? ?? 0F 1F 00 48 8D 8F", 6, "CollisionBoxLayoutInstance virtual table");
    public static readonly NativeStaticAddressTarget HousingObjectLayoutInstanceVirtualTable =
        new("48 8D 05 ?? ?? ?? ?? 48 89 01 33 D2 48 89 51 ?? 48 8D 05 ?? ?? ?? ?? 48 89 51 ?? 89 51 ?? C7 41 ?? FF 80 07 F5", 3, "HousingObjectLayoutInstance virtual table");
    public static readonly NativeStaticAddressTarget WaterRangeLayoutInstanceVirtualTable =
        new("0F 83 ?? ?? ?? ?? 4C 8D 35 ?? ?? ?? ?? 90 48 8B CB", 9, "WaterRangeLayoutInstance virtual table");

    public static readonly NativeRelativeBranchTarget LayoutWorldShutdownWrapper =
        new("E8 ?? ?? ?? ?? 48 8B 8B ?? ?? ?? ?? 84 C0 40 0F B6 FF", "LayoutWorld shutdown wrapper");
    public static readonly NativeDirectSignatureTarget LayoutWorldDeinitializeWrapper =
        new("48 8B 0D ?? ?? ?? ?? 48 85 C9 74 ?? 48 8B 01 48 FF 60 10", "LayoutWorld deinitialization wrapper");
    public static readonly NativeStaticAddressTarget MapRangeLayoutInstanceVirtualTable =
        new("48 8D 05 ?? ?? ?? ?? C7 83 ?? ?? ?? ?? 00 FF 00 00", 3, "MapRangeLayoutInstance virtual table");

    public static readonly NativeDirectSignatureTarget ColliderFinalizer =
        new("40 53 48 83 EC 20 48 8D 05 ?? ?? ?? ?? 48 8B D9 48 89 01 48 8D 05 ?? ?? ?? ?? 48 89 41 ?? 48 8D 05 ?? ?? ?? ?? 48 89 41 ?? 48 8D 05 ?? ?? ?? ?? 48 89 41 ?? 48 81 C1 88 00 00 00", "Collider.Finalizer");

    public static readonly NativeRelativeBranchTarget GetAnalyticColliderTransform =
        new("E8 ?? ?? ?? ?? 84 C0 0F 84 ?? ?? ?? ?? 48 8B 4B ?? 48 8D 55", "bg analytic collision transform");
    public static readonly NativeRelativeBranchTarget LayoutTransformFromFile =
        new("E8 ?? ?? ?? ?? 41 83 3E 07 48 8B CB", "bg collision file transform");
    public static readonly NativeRelativeBranchTarget LayoutTransformGetComponents =
        new("E8 ?? ?? ?? ?? 8B 13 83 EA 01 0F 84", "bg collision transform components");
    public static readonly NativeDirectSignatureTarget SetCylinderMeshMaterial =
        new("4C 8B 89 ?? ?? ?? ?? 4D 85 C9 0F 84 ?? ?? ?? ?? 8B CA", "bg cylinder collision material");
    public static readonly NativeRelativeBranchTarget SetCylinderMeshBounds =
        new("E8 ?? ?? ?? ?? EB ?? 0F 57 C0 48 8D 4D", "bg cylinder collision bounds");

    // input targets
    public const string UIInputDataUpdate =
    "48 89 5C 24 ?? 55 56 57 41 54 41 55 41 56 41 57 48 83 EC 20 4D 8B F1";

    public static readonly NativeDirectSignatureTarget NativeUIInputDataUpdate =
        new(UIInputDataUpdate, "UI keyboard input update");

    // scene rendering targets
    public static readonly NativeResolvedAddressTarget SceneRenderSetRenderTargets = new(
        (nint)GraphicsContext.MemberFunctionPointers.SetRenderTargets,
        "Context.SetRenderTargets");
    public static readonly NativeResolvedAddressTarget SceneRenderAllocateCommand = new(
        (nint)GraphicsContext.MemberFunctionPointers.AllocateCommand,
        "Context.AllocateCommand");
    public static readonly NativeResolvedAddressTarget SceneRenderPushBackCommand = new(
        (nint)GraphicsContext.MemberFunctionPointers.PushBackCommand,
        "Context.PushBackCommand");
    public static readonly NativeResolvedAddressTarget SceneRenderSetTargetCommand = new(
        (nint)ImmediateContext.MemberFunctionPointers.DoSetTargetCommand,
        "ImmediateContext.DoSetTargetCommand");

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
        new(LodConfig, 3, "object texture lod config");
    public static readonly NativeResolvedAddressTarget ResourceFileDescriptorRead =
        new((nint)FileDescriptor.MemberFunctionPointers.Read, "FileDescriptor.Read");
    public static readonly NativeResolvedAddressTarget ResourceFileJob =
        new((nint)FileThread.MemberFunctionPointers.DoFileJob, "FileThread.DoFileJob");
    public static readonly NativeResolvedAddressTarget ResourceSync =
        new((nint)ResourceManager.MemberFunctionPointers.GetResourceSync, "ResourceManager.GetResourceSync");
    public static readonly NativeResolvedAddressTarget ResourceAsync =
        new((nint)ResourceManager.MemberFunctionPointers.GetResourceAsync, "ResourceManager.GetResourceAsync");
    public static readonly NativeResolvedAddressTarget MaterialTextureLoad =
        new((nint)MaterialResourceHandle.MemberFunctionPointers.LoadTexFiles, "MaterialResourceHandle.LoadTexFiles");
    public static readonly NativeResolvedAddressTarget MaterialShaderLoad =
        new((nint)MaterialResourceHandle.MemberFunctionPointers.LoadShpkFiles, "MaterialResourceHandle.LoadShpkFiles");
    public static readonly NativeResolvedAddressTarget ModelResourceLoadMaterials =
        new((nint)ModelResourceHandle.MemberFunctionPointers.LoadMaterials, "ModelResourceHandle.LoadMaterials");
    public static readonly NativeResolvedAddressTarget ResourceHandleIncRef =
        new((nint)ResourceHandle.MemberFunctionPointers.IncRef, "ResourceHandle.IncRef");
    public static readonly NativeResolvedAddressTarget SchedulerTimelineLoadResources =
        new((nint)SchedulerTimeline.MemberFunctionPointers.LoadTimelineResources, "SchedulerTimeline.LoadTimelineResources");
    public static readonly NativeResolvedAddressTarget BgObjectAnimationLoad =
        new((nint)SceneBgObject.MemberFunctionPointers.LoadAnimationData, "BgObject.LoadAnimationData");
    public static readonly NativeResolvedAddressTarget LayoutSharedGroupInsertObject =
        new((nint)LayoutSharedGroupObject.MemberFunctionPointers.InsertObject, "LayoutSharedGroupObject.InsertObject");
    // asset discovery targets
    public static readonly NativeDirectSignatureTarget AssetActorVfxCreate = new(ActorVfxCreate, "object asset actor vfx create");
    public static readonly NativeResolvedAddressTarget AssetStaticVfxCreate =
        new((nint)SceneVfxObject.MemberFunctionPointers.Create, "VfxObject.Create");
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
    public static readonly NativeResolvedAddressTarget FurnitureExecuteEmote =
        new((nint)EmoteManager.MemberFunctionPointers.ExecuteEmote, "EmoteManager.ExecuteEmote");
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

        // input
        NativeUIInputDataUpdate,
    ];

    private static readonly NativeRelativeBranchTarget[] RelativeBranchTargets =
    [
        // resource
        ResourceCheckFileState,
        ResourceLoadMdlFileExtern,

        // furniture and housing
        FurnitureSnapZero,
        FurnitureSnapOne,

        // vfx playback
        NativeVfxPauseToggle,
        NativeVfxIsPaused,

        // furniture and housing
        NativeFurnitureCreate,
        NativeFurnitureDestroy,
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
            failed |= NativeAddressResolver.TryScanSingleTextMatch(logger, sigScanner, target.Signature, target.Label) == nint.Zero;
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
}
