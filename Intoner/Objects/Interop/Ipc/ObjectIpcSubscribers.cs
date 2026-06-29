using Dalamud.Plugin;
using Intoner.Ipc;
using Intoner.Objects.Api;
using Intoner.Objects.Api.Ipc;
using Microsoft.Extensions.Logging;

namespace Intoner.Objects.Interop.Ipc;

/// <summary> shared dependencies for object ipc provider and subscriber wrappers </summary>
internal readonly record struct ObjectIpcContext(IDalamudPluginInterface PluginInterface, ILogger Logger);

/// <summary> object ipc labels and provider wrappers </summary>
internal static class ObjectIpcSubscribers
{
    /// <summary> ipc initialized event </summary>
    internal static class Initialized
    {
        public const string Label = ObjectIpcEndpoints.Events.Initialized;

        public static EventSubscriber Subscriber(ObjectIpcContext context, params Action[] actions)
            => new(context.PluginInterface, context.Logger, Label, actions);

        public static EventProvider Provider(ObjectIpcContext context)
            => new(context.PluginInterface, context.Logger, Label);
    }

    /// <summary> ipc disposed event </summary>
    internal static class Disposed
    {
        public const string Label = ObjectIpcEndpoints.Events.Disposed;

        public static EventSubscriber Subscriber(ObjectIpcContext context, params Action[] actions)
            => new(context.PluginInterface, context.Logger, Label, actions);

        public static EventProvider Provider(ObjectIpcContext context)
            => new(context.PluginInterface, context.Logger, Label);
    }

    /// <summary> persistent scene changed event </summary>
    internal static class PersistentSceneChanged
    {
        public const string Label = ObjectIpcEndpoints.Events.PersistentSceneChanged;

        public static EventSubscriber Subscriber(ObjectIpcContext context, params Action[] actions)
            => new(context.PluginInterface, context.Logger, Label, actions);

        public static EventProvider Provider(ObjectIpcContext context)
            => new(context.PluginInterface, context.Logger, Label);
    }

    /// <summary> get the api version </summary>
    internal static class ApiVersion
    {
        public const string Label = ObjectIpcEndpoints.State.ApiVersion;

        public static FuncProvider<ObjectApiVersion> Provider(ObjectIpcContext context, ObjectPluginStateApi api)
            => new(context.PluginInterface, context.Logger, Label, api.GetApiVersion);
    }

    /// <summary> get the api breaking version </summary>
    internal static class ApiBreakingVersion
    {
        public const string Label = ObjectIpcEndpoints.State.ApiBreakingVersion;

        public static FuncProvider<int> Provider(ObjectIpcContext context, ObjectPluginStateApi api)
            => new(context.PluginInterface, context.Logger, Label, () => api.GetApiVersion().Breaking);
    }

    /// <summary> get saved local layouts </summary>
    internal static class GetLayouts
    {
        public const string Label = ObjectIpcEndpoints.Layouts.GetLayouts;

        public static FuncProvider<IReadOnlyList<SavedObjectLayout>> Provider(ObjectIpcContext context, ObjectLayoutApi api)
            => new(context.PluginInterface, context.Logger, Label, api.GetLayouts);
    }

    /// <summary> get loaded layouts </summary>
    internal static class GetLoadedLayouts
    {
        public const string Label = ObjectIpcEndpoints.Layouts.GetLoadedLayouts;

        public static FuncProvider<IReadOnlyList<LoadedObjectLayout>> Provider(ObjectIpcContext context, ObjectLayoutApi api)
            => new(context.PluginInterface, context.Logger, Label, api.GetLoadedLayouts);
    }

    /// <summary> get the default layout id </summary>
    internal static class GetDefaultLayout
    {
        public const string Label = ObjectIpcEndpoints.Layouts.GetDefaultLayout;

        public static FuncProvider<Guid?> Provider(ObjectIpcContext context, ObjectLayoutApi api)
            => new(context.PluginInterface, context.Logger, Label, api.GetDefaultLayoutId);
    }

    /// <summary> create an empty layout </summary>
    internal static class CreateLayout
    {
        public const string Label = ObjectIpcEndpoints.Layouts.CreateLayout;

        public static FuncProvider<string, Guid> Provider(ObjectIpcContext context, ObjectLayoutApi api)
            => new(context.PluginInterface, context.Logger, Label, api.CreateLayout);
    }

    /// <summary> save current local objects as a layout </summary>
    internal static class SaveCurrentLayout
    {
        public const string Label = ObjectIpcEndpoints.Layouts.SaveCurrentLayout;

        public static FuncProvider<string, Guid?> Provider(ObjectIpcContext context, ObjectLayoutApi api)
            => new(context.PluginInterface, context.Logger, Label, api.SaveCurrentAsLayout);
    }

    /// <summary> set the default layout </summary>
    internal static class SetDefaultLayout
    {
        public const string Label = ObjectIpcEndpoints.Layouts.SetDefaultLayout;

        public static FuncProvider<Guid, bool> Provider(ObjectIpcContext context, ObjectLayoutApi api)
            => new(context.PluginInterface, context.Logger, Label, api.SetDefaultLayout);
    }

    /// <summary> clear the default layout </summary>
    internal static class ClearDefaultLayout
    {
        public const string Label = ObjectIpcEndpoints.Layouts.ClearDefaultLayout;

        public static FuncProvider<bool> Provider(ObjectIpcContext context, ObjectLayoutApi api)
            => new(context.PluginInterface, context.Logger, Label, api.ClearDefaultLayout);
    }

    /// <summary> delete a saved layout </summary>
    internal static class DeleteLayout
    {
        public const string Label = ObjectIpcEndpoints.Layouts.DeleteLayout;

        public static FuncProvider<Guid, bool> Provider(ObjectIpcContext context, ObjectLayoutApi api)
            => new(context.PluginInterface, context.Logger, Label, api.DeleteLayout);
    }

    /// <summary> get loaded temporary layouts </summary>
    internal static class GetTemporaryLayouts
    {
        public const string Label = ObjectIpcEndpoints.Temporary.GetTemporaryLayouts;

        public static FuncProvider<IReadOnlyList<LoadedObjectLayout>> Provider(ObjectIpcContext context, ObjectTemporaryLayoutApi api)
            => new(context.PluginInterface, context.Logger, Label, api.GetLoadedLayouts);
    }

    /// <summary> apply a full temporary layout for one source </summary>
    internal static class ApplyTemporaryLayout
    {
        public const string Label = ObjectIpcEndpoints.Temporary.ApplyTemporaryLayout;

        public static FuncProvider<TemporaryLayoutApplyRequest, TemporarySourceMutationResult> Provider(
            ObjectIpcContext context,
            ObjectTemporaryLayoutApi api)
            => new(context.PluginInterface, context.Logger, Label, api.ApplyLayout);
    }

    /// <summary> apply batched temporary object changes for one source </summary>
    internal static class ApplyTemporaryObjectChanges
    {
        public const string Label = ObjectIpcEndpoints.Temporary.ApplyTemporaryObjectChanges;

        public static FuncProvider<TemporaryObjectChangeSet, TemporarySourceMutationResult> Provider(
            ObjectIpcContext context,
            ObjectTemporaryObjectApi api)
            => new(context.PluginInterface, context.Logger, Label, api.ApplyChanges);
    }

    /// <summary> upsert one temporary object </summary>
    internal static class UpsertTemporaryObject
    {
        public const string Label = ObjectIpcEndpoints.Temporary.UpsertTemporaryObject;

        public static FuncProvider<TemporaryObjectUpsert, TemporarySourceMutationResult> Provider(
            ObjectIpcContext context,
            ObjectTemporaryObjectApi api)
            => new(context.PluginInterface, context.Logger, Label, api.UpsertObject);
    }

    /// <summary> patch one temporary object </summary>
    internal static class PatchTemporaryObject
    {
        public const string Label = ObjectIpcEndpoints.Temporary.PatchTemporaryObject;

        public static FuncProvider<TemporaryObjectPatch, TemporarySourceMutationResult> Provider(
            ObjectIpcContext context,
            ObjectTemporaryObjectApi api)
            => new(context.PluginInterface, context.Logger, Label, api.PatchObject);
    }

    /// <summary> remove one temporary object </summary>
    internal static class RemoveTemporaryObject
    {
        public const string Label = ObjectIpcEndpoints.Temporary.RemoveTemporaryObject;

        public static FuncProvider<TemporaryObjectRemoval, TemporarySourceMutationResult> Provider(
            ObjectIpcContext context,
            ObjectTemporaryObjectApi api)
            => new(context.PluginInterface, context.Logger, Label, api.RemoveObject);
    }

    /// <summary> remove a full temporary layout source </summary>
    internal static class RemoveTemporaryLayout
    {
        public const string Label = ObjectIpcEndpoints.Temporary.RemoveTemporaryLayout;

        public static FuncProvider<TemporaryLayoutRemoval, TemporarySourceMutationResult> Provider(
            ObjectIpcContext context,
            ObjectTemporaryLayoutApi api)
            => new(context.PluginInterface, context.Logger, Label, api.RemoveLayout);
    }

    /// <summary> apply a full temporary collection set for one source </summary>
    internal static class ApplyTemporaryCollections
    {
        public const string Label = ObjectIpcEndpoints.Temporary.ApplyTemporaryCollections;

        public static FuncProvider<TemporaryCollectionsApplyRequest, TemporarySourceMutationResult> Provider(
            ObjectIpcContext context,
            ObjectTemporaryCollectionApi api)
            => new(context.PluginInterface, context.Logger, Label, api.ApplyCollections);
    }

    /// <summary> upsert one temporary object collection </summary>
    internal static class UpsertTemporaryCollection
    {
        public const string Label = ObjectIpcEndpoints.Temporary.UpsertTemporaryCollection;

        public static FuncProvider<TemporaryCollectionUpsert, TemporarySourceMutationResult> Provider(
            ObjectIpcContext context,
            ObjectTemporaryCollectionApi api)
            => new(context.PluginInterface, context.Logger, Label, api.UpsertCollection);
    }

    /// <summary> remove one or more temporary object collections </summary>
    internal static class RemoveTemporaryCollections
    {
        public const string Label = ObjectIpcEndpoints.Temporary.RemoveTemporaryCollections;

        public static FuncProvider<TemporaryCollectionsRemoveRequest, TemporarySourceMutationResult> Provider(
            ObjectIpcContext context,
            ObjectTemporaryCollectionApi api)
            => new(context.PluginInterface, context.Logger, Label, api.RemoveCollections);
    }

    /// <summary> build temporary layout and collection apply requests </summary>
    internal static class BuildTemporarySource
    {
        public const string Label = ObjectIpcEndpoints.Temporary.BuildTemporarySource;

        public static FuncProvider<TemporarySourceBuildRequest, Task<TemporarySourceBuildResult>> Provider(
            ObjectIpcContext context,
            ObjectTemporarySourceBuildApi api)
            => new(context.PluginInterface, context.Logger, Label, api.BuildTemporarySource);
    }

    /// <summary> get the current scene snapshot </summary>
    internal static class GetSceneSnapshot
    {
        public const string Label = ObjectIpcEndpoints.Queries.GetSceneSnapshot;

        public static FuncProvider<ObjectSceneSnapshot> Provider(ObjectIpcContext context, ObjectQueryApi api)
            => new(context.PluginInterface, context.Logger, Label, api.GetSceneSnapshot);
    }

    /// <summary> get one scene object by id </summary>
    internal static class GetObject
    {
        public const string Label = ObjectIpcEndpoints.Queries.GetObject;

        public static FuncProvider<Guid, WorldObject?> Provider(ObjectIpcContext context, ObjectQueryApi api)
            => new(context.PluginInterface, context.Logger, Label, api.GetObject);
    }

    /// <summary> create one local object </summary>
    internal static class CreateObject
    {
        public const string Label = ObjectIpcEndpoints.Mutations.CreateObject;

        public static FuncProvider<WorldObject, Guid?> Provider(ObjectIpcContext context, ObjectMutationApi api)
            => new(context.PluginInterface, context.Logger, Label, api.Create);
    }

    /// <summary> import one local object </summary>
    internal static class ImportObject
    {
        public const string Label = ObjectIpcEndpoints.Mutations.ImportObject;

        public static FuncProvider<WorldObject, Guid?> Provider(ObjectIpcContext context, ObjectMutationApi api)
            => new(context.PluginInterface, context.Logger, Label, api.Import);
    }

    /// <summary> update one local object </summary>
    internal static class UpdateObject
    {
        public const string Label = ObjectIpcEndpoints.Mutations.UpdateObject;

        public static FuncProvider<WorldObject, bool> Provider(ObjectIpcContext context, ObjectMutationApi api)
            => new(context.PluginInterface, context.Logger, Label, api.Update);
    }

    /// <summary> patch one local object </summary>
    internal static class PatchObject
    {
        public const string Label = ObjectIpcEndpoints.Mutations.PatchObject;

        public static FuncProvider<ObjectPatchUpdate, bool> Provider(ObjectIpcContext context, ObjectMutationApi api)
            => new(context.PluginInterface, context.Logger, Label, api.Patch);
    }

    /// <summary> remove one local object </summary>
    internal static class RemoveObject
    {
        public const string Label = ObjectIpcEndpoints.Mutations.RemoveObject;

        public static FuncProvider<Guid, bool> Provider(ObjectIpcContext context, ObjectMutationApi api)
            => new(context.PluginInterface, context.Logger, Label, api.Remove);
    }

    /// <summary> duplicate one local object </summary>
    internal static class DuplicateObject
    {
        public const string Label = ObjectIpcEndpoints.Mutations.DuplicateObject;

        public static FuncProvider<Guid, Guid?> Provider(ObjectIpcContext context, ObjectMutationApi api)
            => new(context.PluginInterface, context.Logger, Label, api.Duplicate);
    }

    /// <summary> get runtime states for the scene </summary>
    internal static class GetRuntimeStates
    {
        public const string Label = ObjectIpcEndpoints.Runtime.GetRuntimeStates;

        public static FuncProvider<IReadOnlyList<RuntimeObjectState>> Provider(ObjectIpcContext context, ObjectRuntimeApi api)
            => new(context.PluginInterface, context.Logger, Label, api.GetStates);
    }

    /// <summary> get one runtime state by id </summary>
    internal static class GetRuntimeState
    {
        public const string Label = ObjectIpcEndpoints.Runtime.GetRuntimeState;

        public static FuncProvider<Guid, RuntimeObjectState?> Provider(ObjectIpcContext context, ObjectRuntimeApi api)
            => new(context.PluginInterface, context.Logger, Label, api.GetState);
    }
}
