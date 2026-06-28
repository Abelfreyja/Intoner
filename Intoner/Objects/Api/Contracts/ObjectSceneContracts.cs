using MessagePack;

namespace Intoner.Objects.Api;

/// <summary> logical world object </summary>
/// <param name="Id"> object id, or 'Guid.Empty' to generate one on local create or import </param>
/// <param name="Name"> display name </param>
/// <param name="Kind"> object kind and required model branch </param>
/// <param name="Visible"> initial visible state </param>
/// <param name="Transform"> world transform </param>
/// <param name="CreatedAtUtc"> creation time, or the default value to use current utc on local create or import </param>
/// <param name="CreatedIn"> saved creation location </param>
/// <param name="CollectionId"> optional object owned collection id </param>
/// <param name="Model"> kind matching model payload </param>
[MessagePackObject(keyAsPropertyName: true)]
public sealed record WorldObject(
    Guid Id,
    string Name,
    WorldObjectKind Kind,
    bool Visible,
    WorldObjectTransform Transform,
    DateTime CreatedAtUtc,
    ObjectCreationData CreatedIn,
    string CollectionId,
    WorldObjectModelData Model);

/// <summary> partial object update </summary>
/// <param name="Name"> updated display name, or null to keep the current name </param>
/// <param name="Visible"> updated visible state, or null to keep the current state </param>
/// <param name="Transform"> updated transform, or null to keep the current transform </param>
/// <param name="Model"> updated model patch for the current object kind, or null to keep the current model </param>
[MessagePackObject(keyAsPropertyName: true)]
public sealed record WorldObjectPatch(
    string? Name = null,
    bool? Visible = null,
    WorldObjectTransform? Transform = null,
    WorldObjectModelPatch? Model = null);

/// <summary> local object patch update </summary>
/// <param name="ObjectId"> local object id </param>
/// <param name="Patch"> partial update payload; null fields keep the current value </param>
[MessagePackObject(keyAsPropertyName: true)]
public sealed record ObjectPatchUpdate(
    Guid ObjectId,
    WorldObjectPatch Patch);

/// <summary> saved local layout </summary>
/// <param name="Id"> saved layout id </param>
/// <param name="Name"> saved layout name </param>
/// <param name="CreatedAtUtc"> layout creation timestamp </param>
/// <param name="UpdatedAtUtc"> last layout update timestamp </param>
/// <param name="Objects"> full object list in the saved layout </param>
[MessagePackObject(keyAsPropertyName: true)]
public sealed record SavedObjectLayout(
    Guid Id,
    string Name,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    IReadOnlyList<WorldObject> Objects);

/// <summary> loaded layout source </summary>
/// <param name="Type"> whether this is the local default layout or a temporary source </param>
/// <param name="LayoutId"> saved layout id for the local default layout </param>
/// <param name="SourceKey"> source key for temporary layouts, or the layout id string for the default layout </param>
/// <param name="SourceSessionId"> source session id for temporary layouts, or 'Guid.Empty' for the local default layout </param>
/// <param name="Name"> loaded layout display name </param>
/// <param name="Revision"> current source revision for temporary layouts, or '0' for the local default layout </param>
/// <param name="UpdatedAtUtc"> last update time for this source </param>
/// <param name="Objects"> full object list for this source </param>
[MessagePackObject(keyAsPropertyName: true)]
public sealed record LoadedObjectLayout(
    LoadedObjectLayoutType Type,
    Guid? LayoutId,
    string SourceKey,
    Guid SourceSessionId,
    string Name,
    long Revision,
    DateTime UpdatedAtUtc,
    IReadOnlyList<WorldObject> Objects);

/// <summary> runtime state for one object </summary>
/// <param name="Id"> logical object id </param>
/// <param name="State"> current runtime state for this object </param>
/// <param name="FailureCode"> short failure code for 'LoadFailed' states </param>
[MessagePackObject(keyAsPropertyName: true)]
public sealed record RuntimeObjectState(
    Guid Id,
    RuntimeObjectStateKind State,
    string? FailureCode);

/// <summary> current composed object scene </summary>
/// <param name="Revision"> local scene revision </param>
/// <param name="PersistentRevision"> local persistent scene revision for standalone objects and the default layout </param>
/// <param name="DefaultLayoutId"> current local default layout id, if one is loaded </param>
/// <param name="StandaloneObjects"> local standalone objects that are not stored inside the default layout </param>
/// <param name="LoadedLayouts"> loaded layout sources, including the default layout and all temporary layouts </param>
/// <param name="RuntimeStates"> runtime state list for the composed scene </param>
/// <param name="CurrentLocation"> current local world and territory context </param>
[MessagePackObject(keyAsPropertyName: true)]
public sealed record ObjectSceneSnapshot(
    long Revision,
    long PersistentRevision,
    Guid? DefaultLayoutId,
    IReadOnlyList<WorldObject> StandaloneObjects,
    IReadOnlyList<LoadedObjectLayout> LoadedLayouts,
    IReadOnlyList<RuntimeObjectState> RuntimeStates,
    ObjectLocationData CurrentLocation);

