using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using FFXIVClientStructs.FFXIV.Client.System.Resource;
using FFXIVClientStructs.FFXIV.Client.System.Resource.Handle;
using FFXIVClientStructs.FFXIV.Client.System.Scheduler.Base;
using FFXIVClientStructs.FFXIV.Client.System.Scheduler.Instance;
using FFXIVClientStructs.FFXIV.Client.System.Scheduler.Resource;
using Intoner.Objects.Utils;
using Intoner.Services.Interop;
using Microsoft.Extensions.Logging;
using System.Runtime.InteropServices;
using SceneBgObject = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.BgObject;

namespace Intoner.Objects.Resources;

[StructLayout(LayoutKind.Explicit)]
internal struct ObjectGetResourceParameters
{
    [FieldOffset(16)] public uint SegmentOffset;
    [FieldOffset(20)] public uint SegmentLength;

    public bool IsPartialRead
        => SegmentLength != 0;
}

internal sealed unsafe class ObjectResourceHooks : IDisposable
{
    public delegate ResourceHandle* GetResourceSyncDelegate(
        ResourceManager* resourceManager,
        ResourceHandleType* handleType,
        uint* resourceType,
        uint* resourceHash,
        byte* path,
        ObjectGetResourceParameters* getResourceParameters,
        byte* file,
        uint line);

    public delegate ResourceHandle* GetResourceAsyncDelegate(
        ResourceManager* resourceManager,
        ResourceHandleType* handleType,
        uint* resourceType,
        uint* resourceHash,
        byte* path,
        ObjectGetResourceParameters* getResourceParameters,
        byte hasHandleLock,
        byte* file,
        uint line);

    public delegate byte ModelResourceLoadDelegate(ModelResourceHandle* handle, void* contents, byte flag);
    public delegate bool ModelResourceLoadMaterialsDelegate(ModelResourceHandle* handle);
    public delegate byte MaterialResourceSubfileLoadDelegate(MaterialResourceHandle* handle);
    public delegate byte ApricotResourceLoadDelegate(ResourceHandle* handle, nint unknown0, byte flag);
    public delegate bool BgObjectLoadAnimationDataDelegate(SceneBgObject* bgObject, byte* modelPath);
    public delegate void SharedGroupLayoutResourceLoadDelegate(ResourceEventListener* listener, ResourceHandle* handle);
    public delegate void LayoutSharedGroupInsertObjectDelegate(LayoutSharedGroupObject* instance, ILayoutInstance* layoutInstance);
    public delegate nint ResourceHandleIncRefDelegate(ResourceHandle* handle);
    public delegate ulong SchedulerTimelineLoadResourcesDelegate(SchedulerTimeline* timeline);
    public delegate SchedulerResource* GetCachedScheduleResourceDelegate(
        SchedulerResourceManagement* resourceManagement,
        ScheduleResourceLoadData* loadData,
        byte useMap);

    private readonly Action[] _enableHooks;
    private readonly Action[] _disposeHooks;
    private readonly ObjectLockedOnce _enableOnce = new();

    public readonly Hook<GetResourceSyncDelegate>? GetResourceSyncHook;
    public readonly Hook<GetResourceAsyncDelegate>? GetResourceAsyncHook;
    public readonly Hook<ModelResourceLoadDelegate>? ModelResourceLoadHook;
    public readonly Hook<ModelResourceLoadMaterialsDelegate>? ModelResourceLoadMaterialsHook;
    public readonly Hook<MaterialResourceSubfileLoadDelegate>? MaterialResourceLoadTexFilesHook;
    public readonly Hook<MaterialResourceSubfileLoadDelegate>? MaterialResourceLoadShpkFilesHook;
    public readonly Hook<ApricotResourceLoadDelegate>? ApricotResourceLoadHook;
    public readonly Hook<BgObjectLoadAnimationDataDelegate>? BgObjectLoadAnimationDataHook;
    public readonly Hook<SharedGroupLayoutResourceLoadDelegate>? SharedGroupLayoutResourceLoadHook;
    public readonly Hook<LayoutSharedGroupInsertObjectDelegate>? LayoutSharedGroupInsertObjectHook;
    public readonly Hook<ResourceHandleIncRefDelegate>? ResourceHandleIncRefHook;
    public readonly Hook<SchedulerTimelineLoadResourcesDelegate>? SchedulerTimelineLoadResourcesHook;
    public readonly Hook<GetCachedScheduleResourceDelegate>? GetCachedScheduleResourceHook;

    public ObjectResourceHooks(
        ILogger logger,
        IGameInteropProvider gameInteropProvider,
        ISigScanner sigScanner,
        GetResourceSyncDelegate getResourceSyncDetour,
        GetResourceAsyncDelegate getResourceAsyncDetour,
        ModelResourceLoadDelegate modelResourceLoadDetour,
        ModelResourceLoadMaterialsDelegate modelResourceLoadMaterialsDetour,
        MaterialResourceSubfileLoadDelegate materialResourceLoadTexFilesDetour,
        MaterialResourceSubfileLoadDelegate materialResourceLoadShpkFilesDetour,
        ApricotResourceLoadDelegate apricotResourceLoadDetour,
        BgObjectLoadAnimationDataDelegate bgObjectLoadAnimationDataDetour,
        SharedGroupLayoutResourceLoadDelegate sharedGroupLayoutResourceLoadDetour,
        LayoutSharedGroupInsertObjectDelegate layoutSharedGroupInsertObjectDetour,
        ResourceHandleIncRefDelegate resourceHandleIncRefDetour,
        SchedulerTimelineLoadResourcesDelegate schedulerTimelineLoadResourcesDetour,
        GetCachedScheduleResourceDelegate getCachedScheduleResourceDetour)
    {
        GetResourceSyncHook = InteropHookUtility.CreateHookFromAddress(logger, gameInteropProvider, IntonerSignatures.ResourceSync, getResourceSyncDetour);
        GetResourceAsyncHook = InteropHookUtility.CreateHookFromAddress(logger, gameInteropProvider, IntonerSignatures.ResourceAsync, getResourceAsyncDetour);
        ModelResourceLoadHook = InteropHookUtility.CreateHook(logger, gameInteropProvider, sigScanner, IntonerSignatures.ModelLoad, modelResourceLoadDetour);
        ModelResourceLoadMaterialsHook = InteropHookUtility.CreateHookFromAddress(logger, gameInteropProvider, IntonerSignatures.ModelResourceLoadMaterials, modelResourceLoadMaterialsDetour);
        MaterialResourceLoadTexFilesHook = InteropHookUtility.CreateHookFromAddress(logger, gameInteropProvider, IntonerSignatures.MaterialTextureLoad, materialResourceLoadTexFilesDetour);
        MaterialResourceLoadShpkFilesHook = InteropHookUtility.CreateHookFromAddress(logger, gameInteropProvider, IntonerSignatures.MaterialShaderLoad, materialResourceLoadShpkFilesDetour);
        ApricotResourceLoadHook = InteropHookUtility.CreateHook(logger, gameInteropProvider, sigScanner, IntonerSignatures.ApricotLoad, apricotResourceLoadDetour);
        BgObjectLoadAnimationDataHook = InteropHookUtility.CreateHookFromAddress(logger, gameInteropProvider, IntonerSignatures.BgObjectAnimationLoad, bgObjectLoadAnimationDataDetour);
        SharedGroupLayoutResourceLoadHook = InteropHookUtility.CreateHook(logger, gameInteropProvider, sigScanner, IntonerSignatures.SharedGroupLayoutResourceLoadHook, sharedGroupLayoutResourceLoadDetour);
        LayoutSharedGroupInsertObjectHook = InteropHookUtility.CreateHookFromAddress(logger, gameInteropProvider, IntonerSignatures.LayoutSharedGroupInsertObject, layoutSharedGroupInsertObjectDetour);
        ResourceHandleIncRefHook = InteropHookUtility.CreateHookFromAddress(logger, gameInteropProvider, IntonerSignatures.ResourceHandleIncRef, resourceHandleIncRefDetour);
        SchedulerTimelineLoadResourcesHook = InteropHookUtility.CreateHookFromAddress(logger, gameInteropProvider, IntonerSignatures.SchedulerTimelineLoadResources, schedulerTimelineLoadResourcesDetour);
        GetCachedScheduleResourceHook = InteropHookUtility.CreateHook(logger, gameInteropProvider, sigScanner, IntonerSignatures.CachedScheduleResource, getCachedScheduleResourceDetour);

        _enableHooks =
        [
            InteropHookUtility.CreateEnableAction(GetResourceSyncHook),
            InteropHookUtility.CreateEnableAction(GetResourceAsyncHook),
            InteropHookUtility.CreateEnableAction(ModelResourceLoadHook),
            InteropHookUtility.CreateEnableAction(ModelResourceLoadMaterialsHook),
            InteropHookUtility.CreateEnableAction(MaterialResourceLoadTexFilesHook),
            InteropHookUtility.CreateEnableAction(MaterialResourceLoadShpkFilesHook),
            InteropHookUtility.CreateEnableAction(ApricotResourceLoadHook),
            InteropHookUtility.CreateEnableAction(BgObjectLoadAnimationDataHook),
            InteropHookUtility.CreateEnableAction(SharedGroupLayoutResourceLoadHook),
            InteropHookUtility.CreateEnableAction(LayoutSharedGroupInsertObjectHook),
            InteropHookUtility.CreateEnableAction(ResourceHandleIncRefHook),
            InteropHookUtility.CreateEnableAction(SchedulerTimelineLoadResourcesHook),
            InteropHookUtility.CreateEnableAction(GetCachedScheduleResourceHook),
        ];

        _disposeHooks =
        [
            InteropHookUtility.CreateDisposeAction(GetResourceSyncHook),
            InteropHookUtility.CreateDisposeAction(GetResourceAsyncHook),
            InteropHookUtility.CreateDisposeAction(ModelResourceLoadHook),
            InteropHookUtility.CreateDisposeAction(ModelResourceLoadMaterialsHook),
            InteropHookUtility.CreateDisposeAction(MaterialResourceLoadTexFilesHook),
            InteropHookUtility.CreateDisposeAction(MaterialResourceLoadShpkFilesHook),
            InteropHookUtility.CreateDisposeAction(ApricotResourceLoadHook),
            InteropHookUtility.CreateDisposeAction(BgObjectLoadAnimationDataHook),
            InteropHookUtility.CreateDisposeAction(SharedGroupLayoutResourceLoadHook),
            InteropHookUtility.CreateDisposeAction(LayoutSharedGroupInsertObjectHook),
            InteropHookUtility.CreateDisposeAction(ResourceHandleIncRefHook),
            InteropHookUtility.CreateDisposeAction(SchedulerTimelineLoadResourcesHook),
            InteropHookUtility.CreateDisposeAction(GetCachedScheduleResourceHook),
        ];
    }

    public bool CanResolveCollectionResources(ObjectRootPathKind kind)
    {
        if (!HasCoreCollectionHooks())
        {
            return false;
        }

        return kind switch
        {
            ObjectRootPathKind.BgModel => HasModelCollectionHooks()
                && BgObjectLoadAnimationDataHook != null,
            ObjectRootPathKind.FurnitureSharedGroup => HasModelCollectionHooks()
                && ApricotResourceLoadHook != null
                && SharedGroupLayoutResourceLoadHook != null
                && LayoutSharedGroupInsertObjectHook != null
                && SchedulerTimelineLoadResourcesHook != null
                && GetCachedScheduleResourceHook != null,
            ObjectRootPathKind.Vfx => ApricotResourceLoadHook != null,
            _ => false,
        };
    }

    public bool CanResolveResourceRequests()
        => GetResourceSyncHook != null
        && GetResourceAsyncHook != null;

    public void Enable()
        => _enableOnce.Execute(
            () =>
            {
                foreach (var enableHook in _enableHooks)
                {
                    enableHook();
                }
            });

    public void Dispose()
    {
        foreach (var disposeHook in _disposeHooks)
        {
            disposeHook();
        }
    }

    private bool HasCoreCollectionHooks()
        => GetResourceSyncHook != null
        && GetResourceAsyncHook != null
        && ResourceHandleIncRefHook != null;

    private bool HasModelCollectionHooks()
        => ModelResourceLoadHook != null
        && ModelResourceLoadMaterialsHook != null
        && MaterialResourceLoadTexFilesHook != null
        && MaterialResourceLoadShpkFilesHook != null;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct ScheduleResourceLoadData
{
    public byte* Path;
    public uint Id;
}

