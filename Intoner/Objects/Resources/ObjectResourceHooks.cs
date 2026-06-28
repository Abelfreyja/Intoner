using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine.Group;
using FFXIVClientStructs.FFXIV.Client.System.Resource;
using FFXIVClientStructs.FFXIV.Client.System.Resource.Handle;
using FFXIVClientStructs.FFXIV.Client.System.Scheduler.Base;
using FFXIVClientStructs.FFXIV.Client.System.Scheduler.Instance;
using FFXIVClientStructs.FFXIV.Client.System.Scheduler.Resource;
using Intoner.Objects.Utils;
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
        GetResourceSyncHook = ObjectInteropHookUtility.CreateHookFromAddress(logger, gameInteropProvider, ObjectSignatures.ResourceSync, getResourceSyncDetour);
        GetResourceAsyncHook = ObjectInteropHookUtility.CreateHookFromAddress(logger, gameInteropProvider, ObjectSignatures.ResourceAsync, getResourceAsyncDetour);
        ModelResourceLoadHook = ObjectInteropHookUtility.CreateHook(logger, gameInteropProvider, sigScanner, ObjectSignatures.ModelLoad, modelResourceLoadDetour);
        ModelResourceLoadMaterialsHook = ObjectInteropHookUtility.CreateHookFromAddress(logger, gameInteropProvider, ObjectSignatures.ModelResourceLoadMaterials, modelResourceLoadMaterialsDetour);
        MaterialResourceLoadTexFilesHook = ObjectInteropHookUtility.CreateHookFromAddress(logger, gameInteropProvider, ObjectSignatures.MaterialTextureLoad, materialResourceLoadTexFilesDetour);
        MaterialResourceLoadShpkFilesHook = ObjectInteropHookUtility.CreateHookFromAddress(logger, gameInteropProvider, ObjectSignatures.MaterialShaderLoad, materialResourceLoadShpkFilesDetour);
        ApricotResourceLoadHook = ObjectInteropHookUtility.CreateHook(logger, gameInteropProvider, sigScanner, ObjectSignatures.ApricotLoad, apricotResourceLoadDetour);
        BgObjectLoadAnimationDataHook = ObjectInteropHookUtility.CreateHookFromAddress(logger, gameInteropProvider, ObjectSignatures.BgObjectAnimationLoad, bgObjectLoadAnimationDataDetour);
        SharedGroupLayoutResourceLoadHook = ObjectInteropHookUtility.CreateHookFromVtable(logger, gameInteropProvider, ObjectSignatures.SharedGroupLayoutResourceLoad, sharedGroupLayoutResourceLoadDetour);
        LayoutSharedGroupInsertObjectHook = ObjectInteropHookUtility.CreateHookFromAddress(logger, gameInteropProvider, ObjectSignatures.LayoutSharedGroupInsertObject, layoutSharedGroupInsertObjectDetour);
        ResourceHandleIncRefHook = ObjectInteropHookUtility.CreateHookFromAddress(logger, gameInteropProvider, ObjectSignatures.ResourceHandleIncRef, resourceHandleIncRefDetour);
        SchedulerTimelineLoadResourcesHook = ObjectInteropHookUtility.CreateHookFromAddress(logger, gameInteropProvider, ObjectSignatures.SchedulerTimelineLoadResources, schedulerTimelineLoadResourcesDetour);
        GetCachedScheduleResourceHook = ObjectInteropHookUtility.CreateHook(logger, gameInteropProvider, sigScanner, ObjectSignatures.CachedScheduleResource, getCachedScheduleResourceDetour);

        _enableHooks =
        [
            ObjectInteropHookUtility.CreateEnableAction(GetResourceSyncHook),
            ObjectInteropHookUtility.CreateEnableAction(GetResourceAsyncHook),
            ObjectInteropHookUtility.CreateEnableAction(ModelResourceLoadHook),
            ObjectInteropHookUtility.CreateEnableAction(ModelResourceLoadMaterialsHook),
            ObjectInteropHookUtility.CreateEnableAction(MaterialResourceLoadTexFilesHook),
            ObjectInteropHookUtility.CreateEnableAction(MaterialResourceLoadShpkFilesHook),
            ObjectInteropHookUtility.CreateEnableAction(ApricotResourceLoadHook),
            ObjectInteropHookUtility.CreateEnableAction(BgObjectLoadAnimationDataHook),
            ObjectInteropHookUtility.CreateEnableAction(SharedGroupLayoutResourceLoadHook),
            ObjectInteropHookUtility.CreateEnableAction(LayoutSharedGroupInsertObjectHook),
            ObjectInteropHookUtility.CreateEnableAction(ResourceHandleIncRefHook),
            ObjectInteropHookUtility.CreateEnableAction(SchedulerTimelineLoadResourcesHook),
            ObjectInteropHookUtility.CreateEnableAction(GetCachedScheduleResourceHook),
        ];

        _disposeHooks =
        [
            ObjectInteropHookUtility.CreateDisposeAction(GetResourceSyncHook),
            ObjectInteropHookUtility.CreateDisposeAction(GetResourceAsyncHook),
            ObjectInteropHookUtility.CreateDisposeAction(ModelResourceLoadHook),
            ObjectInteropHookUtility.CreateDisposeAction(ModelResourceLoadMaterialsHook),
            ObjectInteropHookUtility.CreateDisposeAction(MaterialResourceLoadTexFilesHook),
            ObjectInteropHookUtility.CreateDisposeAction(MaterialResourceLoadShpkFilesHook),
            ObjectInteropHookUtility.CreateDisposeAction(ApricotResourceLoadHook),
            ObjectInteropHookUtility.CreateDisposeAction(BgObjectLoadAnimationDataHook),
            ObjectInteropHookUtility.CreateDisposeAction(SharedGroupLayoutResourceLoadHook),
            ObjectInteropHookUtility.CreateDisposeAction(LayoutSharedGroupInsertObjectHook),
            ObjectInteropHookUtility.CreateDisposeAction(ResourceHandleIncRefHook),
            ObjectInteropHookUtility.CreateDisposeAction(SchedulerTimelineLoadResourcesHook),
            ObjectInteropHookUtility.CreateDisposeAction(GetCachedScheduleResourceHook),
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

