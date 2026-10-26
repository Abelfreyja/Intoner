using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using FFXIVClientStructs.FFXIV.Client.System.Resource;
using FFXIVClientStructs.FFXIV.Client.System.Resource.Handle;
using FFXIVClientStructs.FFXIV.Client.System.Scheduler.Base;
using FFXIVClientStructs.FFXIV.Client.System.Scheduler.Instance;
using FFXIVClientStructs.FFXIV.Client.System.Scheduler.Resource;
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
        => SegmentOffset != 0 || SegmentLength != 0;
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

    public delegate nint ResourceHandleDestructorDelegate(ResourceHandle* handle);
    public delegate SchedulerResource* GetCachedScheduleResourceDelegate(
        SchedulerResourceManagement* resourceManagement,
        ScheduleResourceLoadData* loadData,
        byte useMap);

    private readonly InteropHookCollection _hooks;
    private readonly InteropHookCollection _sharedGroupHooks;

    public readonly Hook<GetResourceSyncDelegate>? GetResourceSyncHook;
    public readonly Hook<GetResourceAsyncDelegate>? GetResourceAsyncHook;
    public readonly Hook<ResourceHandle.Delegates.Load>? ModelResourceLoadHook;
    public readonly Hook<ModelResourceHandle.Delegates.LoadMaterials>? ModelResourceLoadMaterialsHook;
    public readonly Hook<MaterialResourceHandle.Delegates.LoadTexFiles>? MaterialResourceLoadTexFilesHook;
    public readonly Hook<MaterialResourceHandle.Delegates.LoadShpkFiles>? MaterialResourceLoadShpkFilesHook;
    public readonly Hook<ResourceHandle.Delegates.Load>? ApricotResourceLoadHook;
    public readonly Hook<SceneBgObject.Delegates.LoadAnimationData>? BgObjectLoadAnimationDataHook;
    public readonly Hook<ResourceEventListener.Delegates.Load>? SharedGroupLayoutResourceLoadHook;
    public readonly Hook<LayoutSharedGroupObject.Delegates.InsertObject>? LayoutSharedGroupInsertObjectHook;
    public readonly Hook<ResourceHandle.Delegates.IncRef>? ResourceHandleIncRefHook;
    public readonly Hook<ResourceHandleDestructorDelegate>? ResourceHandleDestructorHook;
    public readonly Hook<SchedulerTimeline.Delegates.LoadTimelineResources>? SchedulerTimelineLoadResourcesHook;
    public readonly Hook<GetCachedScheduleResourceDelegate>? GetCachedScheduleResourceHook;

    public ObjectResourceHooks(
        ILogger logger,
        IGameInteropProvider gameInteropProvider,
        ISigScanner sigScanner,
        GetResourceSyncDelegate getResourceSyncDetour,
        GetResourceAsyncDelegate getResourceAsyncDetour,
        ResourceHandle.Delegates.Load modelResourceLoadDetour,
        ModelResourceHandle.Delegates.LoadMaterials modelResourceLoadMaterialsDetour,
        MaterialResourceHandle.Delegates.LoadTexFiles materialResourceLoadTexFilesDetour,
        MaterialResourceHandle.Delegates.LoadShpkFiles materialResourceLoadShpkFilesDetour,
        ResourceHandle.Delegates.Load apricotResourceLoadDetour,
        SceneBgObject.Delegates.LoadAnimationData bgObjectLoadAnimationDataDetour,
        ResourceEventListener.Delegates.Load sharedGroupLayoutResourceLoadDetour,
        LayoutSharedGroupObject.Delegates.InsertObject layoutSharedGroupInsertObjectDetour,
        ResourceHandle.Delegates.IncRef resourceHandleIncRefDetour,
        ResourceHandleDestructorDelegate resourceHandleDestructorDetour,
        SchedulerTimeline.Delegates.LoadTimelineResources schedulerTimelineLoadResourcesDetour,
        GetCachedScheduleResourceDelegate getCachedScheduleResourceDetour)
    {
        _hooks = new InteropHookCollection(logger);
        _sharedGroupHooks = new InteropHookCollection(logger);
        try
        {
            GetResourceSyncHook = _hooks.Add(InteropHookUtility.CreateHookFromAddress(logger, gameInteropProvider, IntonerSignatures.ResourceSync, getResourceSyncDetour));
            GetResourceAsyncHook = _hooks.Add(InteropHookUtility.CreateHookFromAddress(logger, gameInteropProvider, IntonerSignatures.ResourceAsync, getResourceAsyncDetour));
            ModelResourceLoadHook = _hooks.Add(InteropHookUtility.CreateHook(logger, gameInteropProvider, sigScanner, IntonerSignatures.ModelLoad, modelResourceLoadDetour));
            ModelResourceLoadMaterialsHook = _hooks.Add(InteropHookUtility.CreateHookFromAddress(logger, gameInteropProvider, IntonerSignatures.ModelResourceLoadMaterials, modelResourceLoadMaterialsDetour));
            MaterialResourceLoadTexFilesHook = _hooks.Add(InteropHookUtility.CreateHookFromAddress(logger, gameInteropProvider, IntonerSignatures.MaterialTextureLoad, materialResourceLoadTexFilesDetour));
            MaterialResourceLoadShpkFilesHook = _hooks.Add(InteropHookUtility.CreateHookFromAddress(logger, gameInteropProvider, IntonerSignatures.MaterialShaderLoad, materialResourceLoadShpkFilesDetour));
            ApricotResourceLoadHook = _hooks.Add(InteropHookUtility.CreateHook(logger, gameInteropProvider, sigScanner, IntonerSignatures.ApricotLoad, apricotResourceLoadDetour));
            BgObjectLoadAnimationDataHook = _hooks.Add(InteropHookUtility.CreateHookFromAddress(logger, gameInteropProvider, IntonerSignatures.BgObjectAnimationLoad, bgObjectLoadAnimationDataDetour));
            SharedGroupLayoutResourceLoadHook = _sharedGroupHooks.Add(InteropHookUtility.CreateHook(logger, gameInteropProvider, sigScanner, IntonerSignatures.SharedGroupLayoutResourceLoadHook, sharedGroupLayoutResourceLoadDetour));
            LayoutSharedGroupInsertObjectHook = _hooks.Add(InteropHookUtility.CreateHookFromAddress(logger, gameInteropProvider, IntonerSignatures.LayoutSharedGroupInsertObject, layoutSharedGroupInsertObjectDetour));
            ResourceHandleIncRefHook = _hooks.Add(InteropHookUtility.CreateHookFromAddress(logger, gameInteropProvider, IntonerSignatures.ResourceHandleIncRef, resourceHandleIncRefDetour));
            ResourceHandleDestructorHook = _hooks.Add(InteropHookUtility.CreateHook(logger, gameInteropProvider, sigScanner, IntonerSignatures.ResourceHandleDestructor, resourceHandleDestructorDetour));
            SchedulerTimelineLoadResourcesHook = _hooks.Add(InteropHookUtility.CreateHookFromAddress(logger, gameInteropProvider, IntonerSignatures.SchedulerTimelineLoadResources, schedulerTimelineLoadResourcesDetour));
            GetCachedScheduleResourceHook = _hooks.Add(InteropHookUtility.CreateHook(logger, gameInteropProvider, sigScanner, IntonerSignatures.CachedScheduleResource, getCachedScheduleResourceDetour));
        }
        catch
        {
            Dispose();
            throw;
        }
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
            ObjectRootPathKind.LightProjection => true,
            _ => false,
        };
    }

    public bool CanResolveResourceRequests()
        => GetResourceSyncHook != null
        && GetResourceAsyncHook != null
        && ResourceHandleDestructorHook != null;

    public bool IsDisposed
        => _sharedGroupHooks.IsDisposed && _hooks.IsDisposed;

    public bool EnableSharedGroupLoads()
        => SharedGroupLayoutResourceLoadHook != null && _sharedGroupHooks.Enable();

    public bool Enable()
        => (SharedGroupLayoutResourceLoadHook == null || EnableSharedGroupLoads()) && _hooks.Enable();

    public InteropHookCollection.CallbackScope EnterCallback()
        => _hooks.EnterCallback();

    public InteropHookCollection.CallbackScope EnterSharedGroupCallback()
        => _sharedGroupHooks.EnterCallback();

    public void Dispose()
    {
        try
        {
            _sharedGroupHooks.Dispose();
        }
        finally
        {
            _hooks.Dispose();
        }
    }

    private bool HasCoreCollectionHooks()
        => GetResourceSyncHook != null
        && GetResourceAsyncHook != null
        && ResourceHandleIncRefHook != null
        && ResourceHandleDestructorHook != null;

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
