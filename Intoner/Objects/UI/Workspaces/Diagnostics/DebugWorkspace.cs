using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Utility;
using Dalamud.Interface;
using Intoner.Objects.Api.Ipc;
using Intoner.Objects.Api;
using Intoner.Objects.Models;
using Intoner.Objects.Runtime;
using Intoner.Objects.UI.Components;
using Intoner.Services;
using Intoner.UI;
using System.Numerics;
using static Intoner.Objects.UI.Components.EditorCard;

namespace Intoner.Objects.UI;

internal sealed class DebugWorkspace
{
    private readonly DebugIpcSession _session;
    private readonly DebugIpcSamples _samples;
    private readonly IObjectSceneView _sceneView;
    private readonly IObjectRevisionTracker _revisionTracker;
    private readonly IObjectLayoutManager _layoutManager;
    private readonly IClipboardTextService _clipboardText;
    private readonly EditorOverlayLayer _editorOverlayLayer;
    private const string ObjectIpcTemporarySourceKeySample = "ipc-tester";
    private const string ObjectIpcTemporarySourceNameSample = "IPC Tester Temporary";
    private const int ObjectIpcTextInputMaxLength = 256;
    private const int ObjectIpcGuidInputMaxLength = 64;
    private const int ObjectIpcJsonInputMaxLength = 200_000;
    private const float ObjectIpcSideButtonWidth = 68f;
    private const float ObjectIpcResultHeight = 220f;
    private const float ObjectIpcJsonPopupWidth = 760f;
    private const float ObjectIpcJsonPopupHeight = 540f;
    private bool _objectIpcPayloadsInitialized;
    private string _objectIpcLayoutNameInput = "IPC Tester Layout";
    private string _objectIpcSaveLayoutNameInput = "IPC Tester Save";
    private string _objectIpcLayoutIdInput = string.Empty;
    private string _objectIpcObjectIdInput = string.Empty;
    private string _objectIpcPersistentObjectJson = string.Empty;
    private string _objectIpcPatchObjectJson = string.Empty;
    private string _objectIpcApplyTemporarySourceJson = string.Empty;
    private string _objectIpcApplyTemporaryChangesJson = string.Empty;
    private string _objectIpcRemoveTemporarySourceJson = string.Empty;

    public DebugWorkspace(
        DebugIpcSession session,
        DebugIpcSamples samples,
        IObjectSceneView sceneView,
        IObjectRevisionTracker revisionTracker,
        IObjectLayoutManager layoutManager,
        IClipboardTextService clipboardText,
        EditorOverlayLayer editorOverlayLayer)
    {
        _session            = session;
        _samples            = samples;
        _sceneView          = sceneView;
        _revisionTracker    = revisionTracker;
        _layoutManager      = layoutManager;
        _clipboardText      = clipboardText;
        _editorOverlayLayer = editorOverlayLayer;
    }

    private static string FormatOnOff(bool value)
        => value ? "on" : "off";

    private void DrawObjectIpcTesterCard(string id)
    {
        var padding = new Vector2(10f * ImGuiHelpers.GlobalScale, 8f * ImGuiHelpers.GlobalScale);
        var availableHeight = MathF.Max(1f, ImGui.GetContentRegionAvail().Y - ImGui.GetStyle().ItemSpacing.Y);
        var innerHeight = MathF.Max(1f, availableHeight - (padding.Y * 2f) - ImGui.GetStyle().ItemSpacing.Y);
        var background = ThemeColors.ButtonDefault with { W = 0.24f };
        var rounding = 8f * ImGuiHelpers.GlobalScale;

        EditorCard.DrawPanelCard(
            id,
            background,
            ThemeColors.AccentPrimary with { W = 0.18f },
            rounding,
            padding,
            () =>
            {
                using var childBg = ImRaii.PushColor(ImGuiCol.ChildBg, Vector4.Zero);
                using var child = EditorScrollList.Begin(
                    $"##{id}_scroll",
                    new Vector2(0f, innerHeight),
                    _editorOverlayLayer.CreateScrollPanelOptions(background, rounding, ThemeColors.AccentPrimary));
                if (child)
                {
                    DrawObjectIpcTesterContent();
                }
            });
    }

    private void DrawObjectIpcTesterContent()
    {
        EnsureObjectIpcTesterPayloads();

        ImGui.TextUnformatted("Object IPC Tester");
        DrawObjectIpcTesterToolbar();
        DrawObjectIpcTesterOverview();
        ImGuiHelpers.ScaledDummy(6f);

        DrawObjectIpcTesterPluginStateSection();
        DrawObjectIpcTesterEventSection();
        DrawObjectIpcTesterQuerySection();
        DrawObjectIpcTesterLayoutSection();
        DrawObjectIpcTesterMutationSection();
        DrawObjectIpcTesterTemporarySection();
        DrawObjectIpcTesterResultSection();
    }

    private void DrawObjectIpcTesterToolbar()
    {
        var hasSelectedObject = _samples.TryResolveSelectedObjectIpcSnapshot(out _);
        var hasDefaultLayout = _layoutManager.GetDefaultLayoutId().HasValue;
        using (ImRaii.Disabled(!hasSelectedObject))
        {
            if (ImGui.Button("Load Selected Object"))
            {
                LoadObjectIpcSamplesFromSelectedObject();
            }
        }

        ImGui.SameLine();
        using (ImRaii.Disabled(!hasDefaultLayout))
        {
            if (ImGui.Button("Use Default Layout Id"))
            {
                _objectIpcLayoutIdInput = _layoutManager.GetDefaultLayoutId()?.ToString() ?? string.Empty;
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("Reset Mutation Samples"))
        {
            ResetObjectIpcMutationSamples();
        }

        ImGui.SameLine();
        if (ImGui.Button("Reset Temporary Samples"))
        {
            ResetObjectIpcTemporarySamples();
        }

        ImGui.SameLine();
        using (ImRaii.Disabled(string.IsNullOrWhiteSpace(_session.LastOutput)))
        {
            if (ImGui.Button("Copy Last Result"))
            {
                _clipboardText.WriteText(_session.LastOutput);
            }
        }
    }

    private void DrawObjectIpcTesterOverview()
    {
        var referenceLabel = _samples.TryResolveReferenceObjectIpcSnapshot(out var snapshot)
            ? $"{snapshot.Name} | {snapshot.Id}"
            : "none";
        var defaultLayoutLabel = _layoutManager.GetDefaultLayoutId()?.ToString() ?? "none";
        var eventLabel = string.IsNullOrWhiteSpace(_session.LastEventLabel)
            ? "none"
            : $"{_session.LastEventLabel} | {EditorTimestampFormatter.FormatFull(_session.LastEventAtUtc)}";

        ImGui.TextDisabled($"reference object: {referenceLabel}");
        ImGui.TextDisabled($"default layout: {defaultLayoutLabel}");
        ImGui.TextDisabled($"event monitor: {FormatOnOff(_session.EventMonitorEnabled)} | last event: {eventLabel}");
    }

    private void DrawObjectIpcTesterPluginStateSection()
    {
        if (!ImGui.CollapsingHeader("Plugin State", ImGuiTreeNodeFlags.DefaultOpen))
        {
            return;
        }

        using var table = BeginObjectIpcActionTable("##objectIpcPluginState");
        if (!table)
        {
            return;
        }

        DrawObjectIpcQueryRow<ObjectApiInfo>(
            "GetInfo",
            ObjectIpcEndpoints.State.GetInfo.Name,
            "Query##objectIpcGetInfo",
            nameof(ObjectIpcEndpoints.State.GetInfo));
    }

    private void DrawObjectIpcTesterEventSection()
    {
        if (!ImGui.CollapsingHeader("Events", ImGuiTreeNodeFlags.DefaultOpen))
        {
            return;
        }

        if (_session.EventMonitorEnabled)
        {
            if (ImGui.Button("Unsubscribe"))
            {
                _session.DisableObjectIpcEventMonitor();
            }
        }
        else if (ImGui.Button("Subscribe"))
        {
            _session.EnableObjectIpcEventMonitor();
        }

        ImGui.SameLine();
        if (ImGui.Button("Reset Counters"))
        {
            _session.ResetObjectIpcEventMonitor();
        }

        ImGui.TextDisabled($"{ObjectIpcEndpoints.Events.StateChanged.Name}: {_session.StateChangedCount}");
        ImGui.TextDisabled($"{ObjectIpcEndpoints.Events.SceneChanged.Name}: {_session.SceneChangedCount}");
        ImGui.TextDisabled($"{ObjectIpcEndpoints.Events.PersistentSceneChanged.Name}: {_session.PersistentSceneChangedCount}");
        ImGui.TextDisabled($"{ObjectIpcEndpoints.Events.SavedLayoutsChanged.Name}: {_session.SavedLayoutsChangedCount}");
    }

    private void DrawObjectIpcTesterQuerySection()
    {
        if (!ImGui.CollapsingHeader("Query and Runtime", ImGuiTreeNodeFlags.DefaultOpen))
        {
            return;
        }

        using var table = BeginObjectIpcActionTable("##objectIpcQuery");
        if (!table)
        {
            return;
        }

        DrawObjectIpcQueryRow<ObjectSceneSnapshot>(
            "GetSceneSnapshot",
            ObjectIpcEndpoints.Scene.GetSnapshot.Name,
            "Query##objectIpcGetSceneSnapshot",
            nameof(ObjectIpcEndpoints.Scene.GetSnapshot));
        DrawObjectIpcQueryRow<IReadOnlyList<LoadedObjectLayout>>(
            "GetLoadedLayouts",
            ObjectIpcEndpoints.Scene.GetLoadedLayouts.Name,
            "Query##objectIpcGetLoadedLayouts",
            nameof(ObjectIpcEndpoints.Scene.GetLoadedLayouts));
        DrawObjectIpcQueryRow<PersistentObjectSceneSnapshot>(
            "GetPersistentSceneSnapshot",
            ObjectIpcEndpoints.PersistentScene.GetSnapshot.Name,
            "Query##objectIpcGetPersistentSceneSnapshot",
            nameof(ObjectIpcEndpoints.PersistentScene.GetSnapshot));
        DrawObjectIpcObjectIdActionRow<WorldObject?>(
            "GetObject",
            ObjectIpcEndpoints.Scene.GetObject.Name,
            "##objectIpcQueryObjectId",
            "Query##objectIpcGetObject",
            nameof(ObjectIpcEndpoints.Scene.GetObject));
        DrawObjectIpcObjectIdActionRow<PersistentObject?>(
            "GetPersistentObject",
            ObjectIpcEndpoints.PersistentScene.GetObject.Name,
            "##objectIpcQueryPersistentObjectId",
            "Query##objectIpcGetPersistentObject",
            nameof(ObjectIpcEndpoints.PersistentScene.GetObject));
        DrawObjectIpcQueryRow<IReadOnlyList<RuntimeObjectState>>(
            "GetRuntimeStates",
            ObjectIpcEndpoints.Runtime.GetAll.Name,
            "Query##objectIpcGetRuntimeStates",
            nameof(ObjectIpcEndpoints.Runtime.GetAll));
        DrawObjectIpcObjectIdActionRow<RuntimeObjectState?>(
            "GetRuntimeState",
            ObjectIpcEndpoints.Runtime.Get.Name,
            "##objectIpcRuntimeObjectId",
            "Query##objectIpcGetRuntimeState",
            nameof(ObjectIpcEndpoints.Runtime.Get));
    }

    private void DrawObjectIpcTesterLayoutSection()
    {
        if (!ImGui.CollapsingHeader("Layouts", ImGuiTreeNodeFlags.DefaultOpen))
        {
            return;
        }

        using var table = BeginObjectIpcActionTable("##objectIpcLayouts");
        if (!table)
        {
            return;
        }

        DrawObjectIpcQueryRow<SavedObjectLayoutsSnapshot>(
            "GetLayouts",
            ObjectIpcEndpoints.Layouts.GetAll.Name,
            "Query##objectIpcGetLayouts",
            nameof(ObjectIpcEndpoints.Layouts.GetAll));
        DrawObjectIpcLayoutIdActionRow(
            "GetLayout",
            ObjectIpcEndpoints.Layouts.Get.Name,
            "##objectIpcGetLayoutId",
            "Query##objectIpcGetLayout",
            nameof(ObjectIpcEndpoints.Layouts.Get),
            layoutId => _session.InvokeObjectIpcFunc<Guid, SavedObjectLayout?>(
                ObjectIpcEndpoints.Layouts.Get.Name,
                layoutId,
                nameof(ObjectIpcEndpoints.Layouts.Get)));
        DrawObjectIpcQueryRow<Guid?>(
            "GetDefaultLayout",
            ObjectIpcEndpoints.Layouts.GetDefault.Name,
            "Query##objectIpcGetDefaultLayout",
            nameof(ObjectIpcEndpoints.Layouts.GetDefault));
        DrawObjectIpcActionRow(
            "CreateLayout",
            ObjectIpcEndpoints.Layouts.Create.Name,
            () => DrawObjectIpcTextInput("##objectIpcCreateLayoutName", ref _objectIpcLayoutNameInput, ObjectIpcTextInputMaxLength, "layout name"),
            () => DrawObjectIpcInvokeButton("Invoke##objectIpcCreateLayout", () =>
                _session.InvokeObjectIpcFunc<string, SavedObjectLayoutMutationResult>(ObjectIpcEndpoints.Layouts.Create.Name, _objectIpcLayoutNameInput, nameof(ObjectIpcEndpoints.Layouts.Create))));
        DrawObjectIpcActionRow(
            "SaveCurrentLayout",
            ObjectIpcEndpoints.Layouts.SaveCurrent.Name,
            () => DrawObjectIpcTextInput("##objectIpcSaveLayoutName", ref _objectIpcSaveLayoutNameInput, ObjectIpcTextInputMaxLength, "saved layout name"),
            () => DrawObjectIpcInvokeButton("Invoke##objectIpcSaveLayout", () =>
                _session.InvokeObjectIpcFunc<SavedObjectLayoutSaveRequest, SavedObjectLayoutMutationResult>(
                    ObjectIpcEndpoints.Layouts.SaveCurrent.Name,
                    new SavedObjectLayoutSaveRequest(
                        _revisionTracker.GetPersistentSceneRevision(),
                        _objectIpcSaveLayoutNameInput),
                    nameof(ObjectIpcEndpoints.Layouts.SaveCurrent))));
        DrawObjectIpcLayoutIdActionRow(
            "SetDefaultLayout",
            ObjectIpcEndpoints.Layouts.SetDefault.Name,
            "##objectIpcSetDefaultLayoutId",
            "Invoke##objectIpcSetDefaultLayout",
            nameof(ObjectIpcEndpoints.Layouts.SetDefault),
            layoutId =>
            {
                ObjectRevisionSnapshot revisions = _revisionTracker.GetSnapshot();
                long layoutRevision = _layoutManager.TryGetLayout(layoutId, out ObjectLayoutSnapshot layout)
                    ? layout.Revision
                    : 1;
                _session.InvokeObjectIpcFunc<SavedObjectLayoutSetDefaultRequest, SavedObjectLayoutMutationResult>(
                    ObjectIpcEndpoints.Layouts.SetDefault.Name,
                    new SavedObjectLayoutSetDefaultRequest(
                        revisions.PersistentSceneRevision,
                        layoutRevision,
                        layoutId),
                    nameof(ObjectIpcEndpoints.Layouts.SetDefault));
            });
        DrawObjectIpcActionRow(
            "ClearDefaultLayout",
            ObjectIpcEndpoints.Layouts.ClearDefault.Name,
            DrawEmptyObjectIpcInput,
            () => DrawObjectIpcInvokeButton("Invoke##objectIpcClearDefaultLayout", () =>
                _session.InvokeObjectIpcFunc<long, SavedObjectLayoutMutationResult>(
                    ObjectIpcEndpoints.Layouts.ClearDefault.Name,
                    _revisionTracker.GetPersistentSceneRevision(),
                    nameof(ObjectIpcEndpoints.Layouts.ClearDefault))));
        DrawObjectIpcLayoutIdActionRow(
            "DeleteLayout",
            ObjectIpcEndpoints.Layouts.Delete.Name,
            "##objectIpcDeleteLayoutId",
            "Invoke##objectIpcDeleteLayout",
            nameof(ObjectIpcEndpoints.Layouts.Delete),
            layoutId =>
            {
                ObjectRevisionSnapshot revisions = _revisionTracker.GetSnapshot();
                long layoutRevision = _layoutManager.TryGetLayout(layoutId, out ObjectLayoutSnapshot layout)
                    ? layout.Revision
                    : 1;
                _session.InvokeObjectIpcFunc<SavedObjectLayoutDeleteRequest, SavedObjectLayoutMutationResult>(
                    ObjectIpcEndpoints.Layouts.Delete.Name,
                    new SavedObjectLayoutDeleteRequest(
                        revisions.PersistentSceneRevision,
                        layoutRevision,
                        layoutId),
                    nameof(ObjectIpcEndpoints.Layouts.Delete));
            });
    }

    private void DrawObjectIpcTesterMutationSection()
    {
        if (!ImGui.CollapsingHeader("Mutation", ImGuiTreeNodeFlags.DefaultOpen))
        {
            return;
        }

        DrawObjectIpcSampleToolbar(
            "Load Selected Samples##objectIpcMutation",
            _samples.TryResolveSelectedPersistedObjectIpcSnapshot(out _),
            LoadObjectIpcMutationSamplesFromSelectedObject,
            "Reset Samples##objectIpcMutation",
            ResetObjectIpcMutationSamples);

        DrawObjectIpcJsonPayloadBlock(
            "objectIpcWorldObjectPayload",
            "PersistentObject Payload",
            "shared payload for CreateObject, ImportObject, and UpdateObject",
            ref _objectIpcPersistentObjectJson,
            () =>
            {
                DrawObjectIpcInvokeButton("Create##objectIpcCreateObject", () =>
                    InvokeObjectIpcWritePayload(ObjectIpcEndpoints.Objects.Create.Name, nameof(ObjectIpcEndpoints.Objects.Create)), stretch: false);
                ImGui.SameLine();
                DrawObjectIpcInvokeButton("Import##objectIpcImportObject", () =>
                    InvokeObjectIpcWritePayload(ObjectIpcEndpoints.Objects.Import.Name, nameof(ObjectIpcEndpoints.Objects.Import)), stretch: false);
                ImGui.SameLine();
                DrawObjectIpcInvokeButton("Update##objectIpcUpdateObject", () =>
                    InvokeObjectIpcWritePayload(ObjectIpcEndpoints.Objects.Update.Name, nameof(ObjectIpcEndpoints.Objects.Update)), stretch: false);
            });

        DrawObjectIpcJsonPayloadBlock(
            "objectIpcPatchPayload",
            "PersistentObjectPatchRequest Payload",
            "payload for PatchObject",
            ref _objectIpcPatchObjectJson,
            () => DrawObjectIpcInvokeButton("Patch##objectIpcPatchObject", () =>
                _session.InvokeObjectIpcPayload<PersistentObjectPatchRequest, ObjectMutationResult>(ObjectIpcEndpoints.Objects.Patch.Name, nameof(ObjectIpcEndpoints.Objects.Patch), _objectIpcPatchObjectJson), stretch: false),
            drawTopSeparator: true);

        using var table = BeginObjectIpcActionTable("##objectIpcMutationById");
        if (!table)
        {
            return;
        }

        DrawObjectIpcPersistentTargetActionRow(
            "RemoveObject",
            ObjectIpcEndpoints.Objects.Remove.Name,
            "##objectIpcRemoveObjectId",
            "Invoke##objectIpcRemoveObject",
            nameof(ObjectIpcEndpoints.Objects.Remove));
        DrawObjectIpcPersistentTargetActionRow(
            "DuplicateObject",
            ObjectIpcEndpoints.Objects.Duplicate.Name,
            "##objectIpcDuplicateObjectId",
            "Invoke##objectIpcDuplicateObject",
            nameof(ObjectIpcEndpoints.Objects.Duplicate));
    }

    private void DrawObjectIpcTesterTemporarySection()
    {
        if (!ImGui.CollapsingHeader("Temporary", ImGuiTreeNodeFlags.DefaultOpen))
        {
            return;
        }

        DrawObjectIpcSampleToolbar(
            "Load Selected Samples##objectIpcTemporary",
            _samples.TryResolveSelectedObjectIpcSnapshot(out _),
            LoadObjectIpcTemporarySamplesFromSelectedObject,
            "Reset Samples##objectIpcTemporary",
            ResetObjectIpcTemporarySamples);

        using (var table = BeginObjectIpcActionTable("##objectIpcTemporaryQuery"))
        {
            if (table)
            {
                DrawObjectIpcQueryRow<IReadOnlyList<TemporarySourceInfo>>(
                    "GetTemporarySources",
                    ObjectIpcEndpoints.TemporarySources.GetAll.Name,
                    "Query##objectIpcGetTemporarySources",
                    nameof(ObjectIpcEndpoints.TemporarySources.GetAll));
            }
        }

        DrawObjectIpcJsonPayloadBlock(
            "objectIpcApplyTemporarySourcePayload",
            "TemporarySourceApplyRequest Payload",
            "complete source payload for ApplyTemporarySource",
            ref _objectIpcApplyTemporarySourceJson,
            () => DrawObjectIpcInvokeButton("Apply##objectIpcApplyTemporarySource", () =>
                _session.InvokeObjectIpcPayload<TemporarySourceApplyRequest, TemporarySourceMutationResult>(ObjectIpcEndpoints.TemporarySources.Apply.Name, nameof(ObjectIpcEndpoints.TemporarySources.Apply), _objectIpcApplyTemporarySourceJson), stretch: false));
        DrawObjectIpcJsonPayloadBlock(
            "objectIpcApplyTemporaryChangesPayload",
            "TemporaryObjectChangeSet Payload",
            "payload for ApplyTemporaryObjectChanges",
            ref _objectIpcApplyTemporaryChangesJson,
            () => DrawObjectIpcInvokeButton("Apply##objectIpcApplyTemporaryChanges", () =>
                _session.InvokeObjectIpcPayload<TemporaryObjectChangeSet, TemporarySourceMutationResult>(ObjectIpcEndpoints.TemporarySources.ApplyObjectChanges.Name, nameof(ObjectIpcEndpoints.TemporarySources.ApplyObjectChanges), _objectIpcApplyTemporaryChangesJson), stretch: false),
            drawTopSeparator: true);
        DrawObjectIpcJsonPayloadBlock(
            "objectIpcRemoveTemporarySourcePayload",
            "TemporarySourceRemoveRequest Payload",
            "payload for RemoveTemporarySource",
            ref _objectIpcRemoveTemporarySourceJson,
            () => DrawObjectIpcInvokeButton("Remove##objectIpcRemoveTemporarySource", () =>
                _session.InvokeObjectIpcPayload<TemporarySourceRemoveRequest, TemporarySourceMutationResult>(ObjectIpcEndpoints.TemporarySources.Remove.Name, nameof(ObjectIpcEndpoints.TemporarySources.Remove), _objectIpcRemoveTemporarySourceJson), stretch: false),
            drawTopSeparator: true);
    }

    private void DrawObjectIpcTesterResultSection()
    {
        if (!ImGui.CollapsingHeader("Last Result", ImGuiTreeNodeFlags.DefaultOpen))
        {
            return;
        }

        string statusLabel = "idle";
        if (!string.IsNullOrWhiteSpace(_session.LastAction))
        {
            statusLabel = _session.LastSucceeded ? "success" : "error";
        }
        ImGui.TextDisabled($"status: {statusLabel}");
        if (!string.IsNullOrWhiteSpace(_session.LastAction))
        {
            ImGui.TextDisabled($"call: {_session.LastAction}");
            ImGui.TextDisabled($"time: {EditorTimestampFormatter.FormatFull(_session.LastInvokedAtUtc)}");
        }

        if (!string.IsNullOrWhiteSpace(_session.LastError))
        {
            ImGui.TextColored(ThemeColors.DimRed, _session.LastError);
        }

        var output = string.IsNullOrWhiteSpace(_session.LastOutput)
            ? "No IPC call executed yet."
            : _session.LastOutput;
        ImGui.InputTextMultiline(
            "##objectIpcLastResultOutput",
            ref output,
            ObjectIpcJsonInputMaxLength,
            new Vector2(-1f, ObjectIpcResultHeight * ImGuiHelpers.GlobalScale),
            ImGuiInputTextFlags.ReadOnly);
    }

    private static void DrawObjectIpcSampleToolbar(string loadButtonId, bool canLoadSelected, Action loadAction, string resetButtonId, Action resetAction)
    {
        using (ImRaii.Disabled(!canLoadSelected))
        {
            if (ImGui.Button(loadButtonId))
            {
                loadAction();
            }
        }

        ImGui.SameLine();
        if (ImGui.Button(resetButtonId))
        {
            resetAction();
        }
    }

    private void DrawObjectIpcQueryRow<TResult>(string label, string ipcLabel, string buttonId, string actionName)
    {
        DrawObjectIpcActionRow(
            label,
            ipcLabel,
            DrawEmptyObjectIpcInput,
            () => DrawObjectIpcInvokeButton(buttonId, () => _session.InvokeObjectIpcFunc<TResult>(ipcLabel, actionName)));
    }

    private void DrawObjectIpcObjectIdActionRow<TResult>(string label, string ipcLabel, string inputId, string buttonId, string actionName)
    {
        DrawObjectIpcActionRow(
            label,
            ipcLabel,
            () => DrawObjectIpcObjectIdInput(inputId),
            () => DrawObjectIpcInvokeButton(buttonId, () => _session.InvokeObjectIpcGuidFunc<TResult>(ipcLabel, _objectIpcObjectIdInput, actionName)));
    }

    private void DrawObjectIpcLayoutIdActionRow(
        string label,
        string ipcLabel,
        string inputId,
        string buttonId,
        string actionName,
        Action<Guid> invoke)
    {
        DrawObjectIpcActionRow(
            label,
            ipcLabel,
            () => DrawObjectIpcLayoutIdInput(inputId),
            () => DrawObjectIpcInvokeButton(buttonId, () =>
            {
                if (_session.TryParseObjectIpcGuid(_objectIpcLayoutIdInput, actionName, out Guid layoutId))
                {
                    invoke(layoutId);
                }
            }));
    }

    private void EnsureObjectIpcTesterPayloads()
    {
        if (_objectIpcPayloadsInitialized)
        {
            return;
        }

        SyncObjectIpcTesterInputsFromScene();
        ResetObjectIpcMutationSamples();
        ResetObjectIpcTemporarySamples();
        _objectIpcPayloadsInitialized = true;
    }

    private void SyncObjectIpcTesterInputsFromScene()
    {
        _objectIpcLayoutIdInput = _layoutManager.GetDefaultLayoutId()?.ToString() ?? string.Empty;
        if (_samples.TryResolveReferenceObjectIpcSnapshot(out var snapshot))
        {
            _objectIpcObjectIdInput = snapshot.Id.ToString();
        }
    }

    private void LoadObjectIpcSamplesFromSelectedObject()
    {
        if (!_samples.TryResolveSelectedObjectIpcSnapshot(out var snapshot))
        {
            _session.RecordObjectIpcFailure("LoadSelectedObject", "No selected object is available for IPC samples.");
            return;
        }

        _objectIpcObjectIdInput = snapshot.Id.ToString();
        _objectIpcLayoutIdInput = _layoutManager.GetDefaultLayoutId()?.ToString() ?? string.Empty;
        if (_samples.TryResolveSelectedPersistedObjectIpcSnapshot(out var persistedSnapshot))
        {
            ResetObjectIpcMutationSamples(persistedSnapshot);
        }
        else
        {
            ResetObjectIpcMutationSamples();
        }

        ResetObjectIpcTemporarySamples(snapshot);
    }

    private void LoadObjectIpcTemporarySamplesFromSelectedObject()
    {
        if (!_samples.TryResolveSelectedObjectIpcSnapshot(out var snapshot))
        {
            _session.RecordObjectIpcFailure("LoadSelectedTemporaryObject", "No selected object is available for temporary IPC samples.");
            return;
        }

        _objectIpcObjectIdInput = snapshot.Id.ToString();
        ResetObjectIpcTemporarySamples(snapshot);
    }

    private void LoadObjectIpcMutationSamplesFromSelectedObject()
    {
        if (!_samples.TryResolveSelectedPersistedObjectIpcSnapshot(out var snapshot))
        {
            _session.RecordObjectIpcFailure("LoadSelectedMutationObject", "No selected persisted object is available for mutation IPC samples.");
            return;
        }

        _objectIpcObjectIdInput = snapshot.Id.ToString();
        ResetObjectIpcMutationSamples(snapshot);
    }

    private void ResetObjectIpcMutationSamples()
        => ResetObjectIpcMutationSamples(snapshot: null);

    private void ResetObjectIpcMutationSamples(ObjectSnapshot? snapshot)
    {
        var persistentObject = _samples.BuildObjectIpcMutationSample(snapshot);
        var patch = _samples.BuildObjectIpcPatchSample(persistentObject);
        _objectIpcPersistentObjectJson = DebugIpcSession.SerializeObjectIpcPayload(persistentObject);
        _objectIpcPatchObjectJson = DebugIpcSession.SerializeObjectIpcPayload(patch);
    }

    private void ResetObjectIpcTemporarySamples()
        => ResetObjectIpcTemporarySamples(snapshot: null);

    private void ResetObjectIpcTemporarySamples(ObjectSnapshot? snapshot)
    {
        var worldObject = _samples.BuildObjectIpcWorldObjectSample(snapshot) with { CollectionId = string.Empty };
        var sessionId = Guid.NewGuid();

        _objectIpcApplyTemporarySourceJson = DebugIpcSession.SerializeObjectIpcPayload(new TemporarySourceApplyRequest(
            ObjectIpcTemporarySourceKeySample,
            sessionId,
            ObjectIpcTemporarySourceNameSample,
            1,
            [worldObject],
            []));
        _objectIpcApplyTemporaryChangesJson = DebugIpcSession.SerializeObjectIpcPayload(new TemporaryObjectChangeSet(
            ObjectIpcTemporarySourceKeySample,
            sessionId,
            ObjectIpcTemporarySourceNameSample,
            2,
            [new TemporaryObjectChange(TemporaryObjectChangeKind.Upsert, worldObject)]));
        _objectIpcRemoveTemporarySourceJson = DebugIpcSession.SerializeObjectIpcPayload(new TemporarySourceRemoveRequest(
            ObjectIpcTemporarySourceKeySample,
            sessionId,
            3));
    }

    private void DrawObjectIpcJsonPayloadBlock(string id, string title, string description, ref string json, Action drawActions, bool drawTopSeparator = false)
    {
        var popupId = $"##{id}_popup";
        if (drawTopSeparator)
        {
            ImGuiHelpers.ScaledDummy(2f);
            ImGui.Separator();
            ImGuiHelpers.ScaledDummy(6f);
        }

        ImGui.TextUnformatted(title);
        ImGui.TextDisabled(description);
        drawActions();

        if (ImGui.Button($"Edit JSON##{id}"))
        {
            ImGui.OpenPopup(popupId);
        }

        ImGui.SameLine();
        if (ImGui.Button($"Copy JSON##{id}"))
        {
            _clipboardText.WriteText(json);
        }

        ImGui.SameLine();
        ImGui.TextDisabled(FormatObjectIpcJsonSummary(json));
        DrawObjectIpcJsonEditorPopup(popupId, title, ref json);
        ImGuiHelpers.ScaledDummy(6f);
    }

    private void DrawObjectIpcJsonEditorPopup(string popupId, string title, ref string json)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var popupSize = new Vector2(ObjectIpcJsonPopupWidth * scale, ObjectIpcJsonPopupHeight * scale);
        ImGui.SetNextWindowSize(popupSize, ImGuiCond.Appearing);
        ImGui.SetNextWindowSizeConstraints(popupSize, popupSize);

        using var popup = ImRaii.Popup(popupId, ImGuiWindowFlags.NoSavedSettings);
        if (!popup)
        {
            return;
        }

        ImGui.TextUnformatted(title);

        if (ImGui.Button($"Format##{popupId}"))
        {
            json = DebugIpcSession.TryFormatObjectIpcJson(json);
        }

        ImGui.SameLine();
        if (ImGui.Button($"Copy##{popupId}"))
        {
            _clipboardText.WriteText(json);
        }

        ImGui.SameLine();
        if (ImGui.Button($"Close##{popupId}"))
        {
            ImGui.CloseCurrentPopup();
        }

        var editorHeight = MathF.Max(1f, ImGui.GetContentRegionAvail().Y);
        ImGui.InputTextMultiline(
            $"##{popupId}_editor",
            ref json,
            ObjectIpcJsonInputMaxLength,
            new Vector2(-1f, editorHeight));
    }

    private static ImRaiiScope.TableScope BeginObjectIpcActionTable(string id)
    {
        const ImGuiTableFlags flags =
            ImGuiTableFlags.SizingStretchProp
          | ImGuiTableFlags.RowBg
          | ImGuiTableFlags.BordersInnerV
          | ImGuiTableFlags.BordersInnerH
          | ImGuiTableFlags.NoSavedSettings
          | ImGuiTableFlags.NoPadOuterX;

        var table = ImRaiiScope.Table(id, 3, flags, new Vector2(8f * ImGuiHelpers.GlobalScale, 4f * ImGuiHelpers.GlobalScale));
        if (!table)
        {
            return table;
        }

        ImGui.TableSetupColumn("Call", ImGuiTableColumnFlags.WidthStretch, 0.40f);
        ImGui.TableSetupColumn("Input", ImGuiTableColumnFlags.WidthStretch, 0.40f);
        ImGui.TableSetupColumn("Action", ImGuiTableColumnFlags.WidthStretch, 0.20f);
        return table;
    }

    private static void DrawObjectIpcActionRow(string label, string ipcLabel, Action drawInput, Action drawAction)
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(label);
        ImGui.TextDisabled(ipcLabel);

        ImGui.TableNextColumn();
        drawInput();

        ImGui.TableNextColumn();
        drawAction();
    }

    private static void DrawEmptyObjectIpcInput()
        => ImGui.TextDisabled("no input");

    private static void DrawObjectIpcTextInput(string id, ref string value, int maxLength, string hint)
    {
        ImGui.SetNextItemWidth(-1f);
        ImGui.InputTextWithHint(id, hint, ref value, maxLength);
    }

    private void DrawObjectIpcObjectIdInput(string id)
    {
        var buttonWidth = ObjectIpcSideButtonWidth * ImGuiHelpers.GlobalScale;
        ImGui.SetNextItemWidth(MathF.Max(1f, ImGui.GetContentRegionAvail().X - buttonWidth - ImGui.GetStyle().ItemSpacing.X));
        ImGui.InputTextWithHint(id, "object id", ref _objectIpcObjectIdInput, ObjectIpcGuidInputMaxLength);
        ImGui.SameLine();
        using (ImRaii.Disabled(!_samples.TryResolveSelectedObjectIpcSnapshot(out _)))
        {
            if (ImGui.Button($"Selected{id}", new Vector2(buttonWidth, 0f))
                && _samples.TryResolveSelectedObjectIpcSnapshot(out var snapshot))
            {
                _objectIpcObjectIdInput = snapshot.Id.ToString();
            }
        }
    }

    private void DrawObjectIpcLayoutIdInput(string id)
    {
        var buttonWidth = ObjectIpcSideButtonWidth * ImGuiHelpers.GlobalScale;
        ImGui.SetNextItemWidth(MathF.Max(1f, ImGui.GetContentRegionAvail().X - buttonWidth - ImGui.GetStyle().ItemSpacing.X));
        ImGui.InputTextWithHint(id, "layout id", ref _objectIpcLayoutIdInput, ObjectIpcGuidInputMaxLength);
        ImGui.SameLine();
        using (ImRaii.Disabled(!_layoutManager.GetDefaultLayoutId().HasValue))
        {
            if (ImGui.Button($"Default{id}", new Vector2(buttonWidth, 0f)))
            {
                _objectIpcLayoutIdInput = _layoutManager.GetDefaultLayoutId()?.ToString() ?? string.Empty;
            }
        }
    }

    private static void DrawObjectIpcInvokeButton(string id, Action onClick, bool stretch = true)
    {
        var size = stretch ? new Vector2(-1f, 0f) : Vector2.Zero;
        if (ImGui.Button(id, size))
        {
            onClick();
        }
    }

    private void InvokeObjectIpcWritePayload(string label, string actionName)
    {
        if (!_session.TryDeserializeObjectIpcPayload(
                _objectIpcPersistentObjectJson,
                actionName,
                out PersistentObject persistentObject))
        {
            return;
        }

        _session.InvokeObjectIpcFunc<PersistentObjectWriteRequest, ObjectMutationResult>(
            label,
            new PersistentObjectWriteRequest(
                _sceneView.GetPersistentSceneRevision(),
                persistentObject),
            actionName);
    }

    private void DrawObjectIpcPersistentTargetActionRow(
        string label,
        string ipcLabel,
        string inputId,
        string buttonId,
        string actionName)
        => DrawObjectIpcActionRow(
            label,
            ipcLabel,
            () => DrawObjectIpcObjectIdInput(inputId),
            () => DrawObjectIpcInvokeButton(buttonId, () =>
            {
                if (!_session.TryParseObjectIpcGuid(_objectIpcObjectIdInput, actionName, out Guid objectId))
                {
                    return;
                }

                _session.InvokeObjectIpcFunc<PersistentObjectTargetRequest, ObjectMutationResult>(
                    ipcLabel,
                    new PersistentObjectTargetRequest(
                        _sceneView.GetPersistentSceneRevision(),
                        objectId),
                    actionName);
            }));

    private static string FormatObjectIpcJsonSummary(string json)
    {
        var lineCount = 1;
        for (var i = 0; i < json.Length; ++i)
        {
            if (json[i] == '\n')
            {
                ++lineCount;
            }
        }

        return $"{lineCount} lines | {json.Length:N0} chars";
    }

    internal void DrawDebugWorkspace()
    {
        _session.EnsureObjectIpcTesterInitialized();
        _editorOverlayLayer.DrawChildPanel("##objectDebugWorkspacePanel", Vector2.Zero, false, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse, () =>
        {
            DrawUtilityWorkspaceHero(
                "objectDebugWorkspaceHero",
                FontAwesomeIcon.Bug,
                "Debug",
                "just a placeholder2");
            DrawObjectIpcTesterCard("objectDebugIpcTester");
        }, transparentBackground: false);
    }

    private static void DrawUtilityWorkspaceHero(string id, FontAwesomeIcon icon, string title, string subtitle)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var accent = ThemeColors.AccentPrimary;

        EditorCard.DrawPanelCard(
            id,
            ThemeColors.ButtonDefault with { W = 0.30f },
            accent with { W = 0.24f },
            8f * scale,
            new Vector2(10f * scale, 8f * scale),
            () =>
            {
                DrawCardHeader($"{id}Header", icon, title, subtitle, accent);
            });
    }
}
