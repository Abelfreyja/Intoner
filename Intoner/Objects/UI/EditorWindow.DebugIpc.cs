using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Intoner.Ipc;
using Intoner.Objects.Api;
using Intoner.Objects.Interop.Ipc;
using Intoner.Objects.Models;
using Intoner.Objects.UI.Components;
using Intoner.UI;
using System.Numerics;
using System.Text.Json;

namespace Intoner.Objects.UI;

internal sealed partial class EditorWindow
{
    private const string ObjectIpcTemporarySourceKeySample = "ipc-tester";
    private const string ObjectIpcTemporarySourceNameSample = "IPC Tester Temporary";
    private const int ObjectIpcTextInputMaxLength = 256;
    private const int ObjectIpcGuidInputMaxLength = 64;
    private const int ObjectIpcJsonInputMaxLength = 200_000;
    private const float ObjectIpcSideButtonWidth = 68f;
    private const float ObjectIpcResultHeight = 220f;
    private const float ObjectIpcJsonPopupWidth = 760f;
    private const float ObjectIpcJsonPopupHeight = 540f;

    private static readonly JsonSerializerOptions ObjectIpcTesterJsonOptions = CreateObjectIpcTesterJsonOptions();

    private enum IpcTesterEventKind
    {
        Initialized,
        Disposed,
        PersistentSceneChanged,
    }

    private EventSubscriber? _objectIpcInitializedSubscriber;
    private EventSubscriber? _objectIpcDisposedSubscriber;
    private EventSubscriber? _objectIpcPersistentSceneChangedSubscriber;
    private bool _objectIpcTesterInitialized;
    private bool _objectIpcEventMonitorEnabled;
    private bool _objectIpcPayloadsInitialized;
    private int _objectIpcInitializedCount;
    private int _objectIpcDisposedCount;
    private int _objectIpcPersistentSceneChangedCount;
    private DateTime _objectIpcLastEventAtUtc;
    private string _objectIpcLastEventLabel = string.Empty;
    private string _objectIpcLayoutNameInput = "IPC Tester Layout";
    private string _objectIpcSaveLayoutNameInput = "IPC Tester Save";
    private string _objectIpcLayoutIdInput = string.Empty;
    private string _objectIpcObjectIdInput = string.Empty;
    private string _objectIpcWorldObjectJson = string.Empty;
    private string _objectIpcPatchObjectJson = string.Empty;
    private string _objectIpcApplyTemporaryLayoutJson = string.Empty;
    private string _objectIpcApplyTemporaryChangesJson = string.Empty;
    private string _objectIpcUpsertTemporaryObjectJson = string.Empty;
    private string _objectIpcPatchTemporaryObjectJson = string.Empty;
    private string _objectIpcRemoveTemporaryObjectJson = string.Empty;
    private string _objectIpcRemoveTemporaryLayoutJson = string.Empty;
    private string _objectIpcLastAction = string.Empty;
    private string _objectIpcLastOutput = string.Empty;
    private string _objectIpcLastError = string.Empty;
    private bool _objectIpcLastSucceeded;
    private DateTime _objectIpcLastInvokedAtUtc;

    private static JsonSerializerOptions CreateObjectIpcTesterJsonOptions()
        => new()
        {
            WriteIndented = true,
            IncludeFields = true,
            RespectRequiredConstructorParameters = true,
        };

    private void EnsureObjectIpcTesterInitialized()
    {
        if (_objectIpcTesterInitialized)
        {
            return;
        }

        ObjectIpcContext context = new(_pluginInterface, _logger);
        _objectIpcInitializedSubscriber = ObjectIpcSubscribers.Initialized.Subscriber(context, () => RecordObjectIpcEvent(IpcTesterEventKind.Initialized));
        _objectIpcDisposedSubscriber = ObjectIpcSubscribers.Disposed.Subscriber(context, () => RecordObjectIpcEvent(IpcTesterEventKind.Disposed));
        _objectIpcPersistentSceneChangedSubscriber = ObjectIpcSubscribers.PersistentSceneChanged.Subscriber(context, () => RecordObjectIpcEvent(IpcTesterEventKind.PersistentSceneChanged));
        _objectIpcInitializedSubscriber.Disable();
        _objectIpcDisposedSubscriber.Disable();
        _objectIpcPersistentSceneChangedSubscriber.Disable();
        _objectIpcTesterInitialized = true;
    }

    private void DisposeObjectIpcTester()
    {
        _objectIpcInitializedSubscriber?.Dispose();
        _objectIpcDisposedSubscriber?.Dispose();
        _objectIpcPersistentSceneChangedSubscriber?.Dispose();
        _objectIpcInitializedSubscriber = null;
        _objectIpcDisposedSubscriber = null;
        _objectIpcPersistentSceneChangedSubscriber = null;
        _objectIpcEventMonitorEnabled = false;
        _objectIpcTesterInitialized = false;
    }

    private void DrawObjectIpcTesterCard(string id)
    {
        var padding = new Vector2(10f * ImGuiHelpers.GlobalScale, 8f * ImGuiHelpers.GlobalScale);
        var availableHeight = MathF.Max(1f, ImGui.GetContentRegionAvail().Y - ImGui.GetStyle().ItemSpacing.Y);
        var innerHeight = MathF.Max(1f, availableHeight - (padding.Y * 2f) - ImGui.GetStyle().ItemSpacing.Y);
        var background = EditorColors.ButtonDefault with { W = 0.24f };
        var rounding = 8f * ImGuiHelpers.GlobalScale;

        DrawPanelCard(
            id,
            background,
            EditorColors.AccentPurple with { W = 0.18f },
            rounding,
            padding,
            () =>
            {
                using var childBg = ImRaii.PushColor(ImGuiCol.ChildBg, Vector4.Zero);
                using var child = ObjectScrollList.Begin(
                    $"##{id}_scroll",
                    new Vector2(0f, innerHeight),
                    CreateOverlayScrollPanelOptions(background, rounding, EditorColors.AccentPurple));
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
        var hasSelectedObject = TryResolveSelectedObjectIpcSnapshot(out _);
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
        using (ImRaii.Disabled(string.IsNullOrWhiteSpace(_objectIpcLastOutput)))
        {
            if (ImGui.Button("Copy Last Result"))
            {
                _clipboardExportService.CopyText(_objectIpcLastOutput);
            }
        }
    }

    private void DrawObjectIpcTesterOverview()
    {
        var referenceLabel = TryResolveReferenceObjectIpcSnapshot(out var snapshot)
            ? $"{snapshot.Name} | {snapshot.Id}"
            : "none";
        var defaultLayoutLabel = _layoutManager.GetDefaultLayoutId()?.ToString() ?? "none";
        var eventLabel = string.IsNullOrWhiteSpace(_objectIpcLastEventLabel)
            ? "none"
            : $"{_objectIpcLastEventLabel} | {_objectIpcLastEventAtUtc:yyyy-MM-dd HH:mm:ss} UTC";

        ImGui.TextDisabled($"reference object: {referenceLabel}");
        ImGui.TextDisabled($"default layout: {defaultLayoutLabel}");
        ImGui.TextDisabled($"event monitor: {FormatOnOff(_objectIpcEventMonitorEnabled)} | last event: {eventLabel}");
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

        DrawObjectIpcQueryRow<ObjectApiVersion>(
            "ApiVersion",
            ObjectIpcSubscribers.ApiVersion.Label,
            "Query##objectIpcApiVersion",
            nameof(ObjectIpcSubscribers.ApiVersion));
        DrawObjectIpcQueryRow<int>(
            "ApiBreakingVersion",
            ObjectIpcSubscribers.ApiBreakingVersion.Label,
            "Query##objectIpcApiBreakingVersion",
            nameof(ObjectIpcSubscribers.ApiBreakingVersion));
    }

    private void DrawObjectIpcTesterEventSection()
    {
        if (!ImGui.CollapsingHeader("Events", ImGuiTreeNodeFlags.DefaultOpen))
        {
            return;
        }

        if (_objectIpcEventMonitorEnabled)
        {
            if (ImGui.Button("Unsubscribe"))
            {
                DisableObjectIpcEventMonitor();
            }
        }
        else if (ImGui.Button("Subscribe"))
        {
            EnableObjectIpcEventMonitor();
        }

        ImGui.SameLine();
        if (ImGui.Button("Reset Counters"))
        {
            ResetObjectIpcEventMonitor();
        }

        ImGui.TextDisabled($"{ObjectIpcSubscribers.Initialized.Label}: {_objectIpcInitializedCount}");
        ImGui.TextDisabled($"{ObjectIpcSubscribers.Disposed.Label}: {_objectIpcDisposedCount}");
        ImGui.TextDisabled($"{ObjectIpcSubscribers.PersistentSceneChanged.Label}: {_objectIpcPersistentSceneChangedCount}");
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
            ObjectIpcSubscribers.GetSceneSnapshot.Label,
            "Query##objectIpcGetSceneSnapshot",
            nameof(ObjectIpcSubscribers.GetSceneSnapshot));
        DrawObjectIpcObjectIdActionRow<WorldObject?>(
            "GetObject",
            ObjectIpcSubscribers.GetObject.Label,
            "##objectIpcQueryObjectId",
            "Query##objectIpcGetObject",
            nameof(ObjectIpcSubscribers.GetObject));
        DrawObjectIpcQueryRow<IReadOnlyList<RuntimeObjectState>>(
            "GetRuntimeStates",
            ObjectIpcSubscribers.GetRuntimeStates.Label,
            "Query##objectIpcGetRuntimeStates",
            nameof(ObjectIpcSubscribers.GetRuntimeStates));
        DrawObjectIpcObjectIdActionRow<RuntimeObjectState?>(
            "GetRuntimeState",
            ObjectIpcSubscribers.GetRuntimeState.Label,
            "##objectIpcRuntimeObjectId",
            "Query##objectIpcGetRuntimeState",
            nameof(ObjectIpcSubscribers.GetRuntimeState));
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

        DrawObjectIpcQueryRow<IReadOnlyList<SavedObjectLayout>>(
            "GetLayouts",
            ObjectIpcSubscribers.GetLayouts.Label,
            "Query##objectIpcGetLayouts",
            nameof(ObjectIpcSubscribers.GetLayouts));
        DrawObjectIpcQueryRow<IReadOnlyList<LoadedObjectLayout>>(
            "GetLoadedLayouts",
            ObjectIpcSubscribers.GetLoadedLayouts.Label,
            "Query##objectIpcGetLoadedLayouts",
            nameof(ObjectIpcSubscribers.GetLoadedLayouts));
        DrawObjectIpcQueryRow<Guid?>(
            "GetDefaultLayout",
            ObjectIpcSubscribers.GetDefaultLayout.Label,
            "Query##objectIpcGetDefaultLayout",
            nameof(ObjectIpcSubscribers.GetDefaultLayout));
        DrawObjectIpcActionRow(
            "CreateLayout",
            ObjectIpcSubscribers.CreateLayout.Label,
            () => DrawObjectIpcTextInput("##objectIpcCreateLayoutName", ref _objectIpcLayoutNameInput, ObjectIpcTextInputMaxLength, "layout name"),
            () => DrawObjectIpcInvokeButton("Invoke##objectIpcCreateLayout", () =>
                InvokeObjectIpcFunc<string, Guid>(ObjectIpcSubscribers.CreateLayout.Label, _objectIpcLayoutNameInput, nameof(ObjectIpcSubscribers.CreateLayout))));
        DrawObjectIpcActionRow(
            "SaveCurrentLayout",
            ObjectIpcSubscribers.SaveCurrentLayout.Label,
            () => DrawObjectIpcTextInput("##objectIpcSaveLayoutName", ref _objectIpcSaveLayoutNameInput, ObjectIpcTextInputMaxLength, "saved layout name"),
            () => DrawObjectIpcInvokeButton("Invoke##objectIpcSaveLayout", () =>
                InvokeObjectIpcFunc<string, Guid?>(ObjectIpcSubscribers.SaveCurrentLayout.Label, _objectIpcSaveLayoutNameInput, nameof(ObjectIpcSubscribers.SaveCurrentLayout))));
        DrawObjectIpcLayoutIdActionRow<bool>(
            "SetDefaultLayout",
            ObjectIpcSubscribers.SetDefaultLayout.Label,
            "##objectIpcSetDefaultLayoutId",
            "Invoke##objectIpcSetDefaultLayout",
            nameof(ObjectIpcSubscribers.SetDefaultLayout));
        DrawObjectIpcQueryRow<bool>(
            "ClearDefaultLayout",
            ObjectIpcSubscribers.ClearDefaultLayout.Label,
            "Invoke##objectIpcClearDefaultLayout",
            nameof(ObjectIpcSubscribers.ClearDefaultLayout));
        DrawObjectIpcLayoutIdActionRow<bool>(
            "DeleteLayout",
            ObjectIpcSubscribers.DeleteLayout.Label,
            "##objectIpcDeleteLayoutId",
            "Invoke##objectIpcDeleteLayout",
            nameof(ObjectIpcSubscribers.DeleteLayout));
    }

    private void DrawObjectIpcTesterMutationSection()
    {
        if (!ImGui.CollapsingHeader("Mutation", ImGuiTreeNodeFlags.DefaultOpen))
        {
            return;
        }

        DrawObjectIpcSampleToolbar(
            "Load Selected Samples##objectIpcMutation",
            TryResolveSelectedPersistedObjectIpcSnapshot(out _),
            LoadObjectIpcMutationSamplesFromSelectedObject,
            "Reset Samples##objectIpcMutation",
            ResetObjectIpcMutationSamples);

        DrawObjectIpcJsonPayloadBlock(
            "objectIpcWorldObjectPayload",
            "WorldObject Payload",
            "shared payload for CreateObject, ImportObject, and UpdateObject",
            ref _objectIpcWorldObjectJson,
            () =>
            {
                DrawObjectIpcInvokeButton("Create##objectIpcCreateObject", () =>
                    InvokeObjectIpcPayload<WorldObject, Guid?>(ObjectIpcSubscribers.CreateObject.Label, nameof(ObjectIpcSubscribers.CreateObject), _objectIpcWorldObjectJson), stretch: false);
                ImGui.SameLine();
                DrawObjectIpcInvokeButton("Import##objectIpcImportObject", () =>
                    InvokeObjectIpcPayload<WorldObject, Guid?>(ObjectIpcSubscribers.ImportObject.Label, nameof(ObjectIpcSubscribers.ImportObject), _objectIpcWorldObjectJson), stretch: false);
                ImGui.SameLine();
                DrawObjectIpcInvokeButton("Update##objectIpcUpdateObject", () =>
                    InvokeObjectIpcPayload<WorldObject, bool>(ObjectIpcSubscribers.UpdateObject.Label, nameof(ObjectIpcSubscribers.UpdateObject), _objectIpcWorldObjectJson), stretch: false);
            });

        DrawObjectIpcJsonPayloadBlock(
            "objectIpcPatchPayload",
            "ObjectPatchUpdate Payload",
            "payload for PatchObject",
            ref _objectIpcPatchObjectJson,
            () => DrawObjectIpcInvokeButton("Patch##objectIpcPatchObject", () =>
                InvokeObjectIpcPayload<ObjectPatchUpdate, bool>(ObjectIpcSubscribers.PatchObject.Label, nameof(ObjectIpcSubscribers.PatchObject), _objectIpcPatchObjectJson), stretch: false),
            drawTopSeparator: true);

        using var table = BeginObjectIpcActionTable("##objectIpcMutationById");
        if (!table)
        {
            return;
        }

        DrawObjectIpcObjectIdActionRow<bool>(
            "RemoveObject",
            ObjectIpcSubscribers.RemoveObject.Label,
            "##objectIpcRemoveObjectId",
            "Invoke##objectIpcRemoveObject",
            nameof(ObjectIpcSubscribers.RemoveObject));
        DrawObjectIpcObjectIdActionRow<Guid?>(
            "DuplicateObject",
            ObjectIpcSubscribers.DuplicateObject.Label,
            "##objectIpcDuplicateObjectId",
            "Invoke##objectIpcDuplicateObject",
            nameof(ObjectIpcSubscribers.DuplicateObject));
    }

    private void DrawObjectIpcTesterTemporarySection()
    {
        if (!ImGui.CollapsingHeader("Temporary", ImGuiTreeNodeFlags.DefaultOpen))
        {
            return;
        }

        DrawObjectIpcSampleToolbar(
            "Load Selected Samples##objectIpcTemporary",
            TryResolveSelectedObjectIpcSnapshot(out _),
            LoadObjectIpcTemporarySamplesFromSelectedObject,
            "Reset Samples##objectIpcTemporary",
            ResetObjectIpcTemporarySamples);

        using (var table = BeginObjectIpcActionTable("##objectIpcTemporaryQuery"))
        {
            if (table)
            {
                DrawObjectIpcQueryRow<IReadOnlyList<LoadedObjectLayout>>(
                    "GetTemporaryLayouts",
                    ObjectIpcSubscribers.GetTemporaryLayouts.Label,
                    "Query##objectIpcGetTemporaryLayouts",
                    nameof(ObjectIpcSubscribers.GetTemporaryLayouts));
            }
        }

        DrawObjectIpcJsonPayloadBlock(
            "objectIpcApplyTemporaryLayoutPayload",
            "TemporaryLayoutApplyRequest Payload",
            "payload for ApplyTemporaryLayout",
            ref _objectIpcApplyTemporaryLayoutJson,
            () => DrawObjectIpcInvokeButton("Apply##objectIpcApplyTemporaryLayout", () =>
                InvokeObjectIpcPayload<TemporaryLayoutApplyRequest, TemporarySourceMutationResult>(ObjectIpcSubscribers.ApplyTemporaryLayout.Label, nameof(ObjectIpcSubscribers.ApplyTemporaryLayout), _objectIpcApplyTemporaryLayoutJson), stretch: false));
        DrawObjectIpcJsonPayloadBlock(
            "objectIpcApplyTemporaryChangesPayload",
            "TemporaryObjectChangeSet Payload",
            "payload for ApplyTemporaryObjectChanges",
            ref _objectIpcApplyTemporaryChangesJson,
            () => DrawObjectIpcInvokeButton("Apply##objectIpcApplyTemporaryChanges", () =>
                InvokeObjectIpcPayload<TemporaryObjectChangeSet, TemporarySourceMutationResult>(ObjectIpcSubscribers.ApplyTemporaryObjectChanges.Label, nameof(ObjectIpcSubscribers.ApplyTemporaryObjectChanges), _objectIpcApplyTemporaryChangesJson), stretch: false),
            drawTopSeparator: true);
        DrawObjectIpcJsonPayloadBlock(
            "objectIpcUpsertTemporaryObjectPayload",
            "TemporaryObjectUpsert Payload",
            "payload for UpsertTemporaryObject",
            ref _objectIpcUpsertTemporaryObjectJson,
            () => DrawObjectIpcInvokeButton("Upsert##objectIpcUpsertTemporaryObject", () =>
                InvokeObjectIpcPayload<TemporaryObjectUpsert, TemporarySourceMutationResult>(ObjectIpcSubscribers.UpsertTemporaryObject.Label, nameof(ObjectIpcSubscribers.UpsertTemporaryObject), _objectIpcUpsertTemporaryObjectJson), stretch: false),
            drawTopSeparator: true);
        DrawObjectIpcJsonPayloadBlock(
            "objectIpcPatchTemporaryObjectPayload",
            "TemporaryObjectPatch Payload",
            "payload for PatchTemporaryObject",
            ref _objectIpcPatchTemporaryObjectJson,
            () => DrawObjectIpcInvokeButton("Patch##objectIpcPatchTemporaryObject", () =>
                InvokeObjectIpcPayload<TemporaryObjectPatch, TemporarySourceMutationResult>(ObjectIpcSubscribers.PatchTemporaryObject.Label, nameof(ObjectIpcSubscribers.PatchTemporaryObject), _objectIpcPatchTemporaryObjectJson), stretch: false),
            drawTopSeparator: true);
        DrawObjectIpcJsonPayloadBlock(
            "objectIpcRemoveTemporaryObjectPayload",
            "TemporaryObjectRemoval Payload",
            "payload for RemoveTemporaryObject",
            ref _objectIpcRemoveTemporaryObjectJson,
            () => DrawObjectIpcInvokeButton("Remove Object##objectIpcRemoveTemporaryObject", () =>
                InvokeObjectIpcPayload<TemporaryObjectRemoval, TemporarySourceMutationResult>(ObjectIpcSubscribers.RemoveTemporaryObject.Label, nameof(ObjectIpcSubscribers.RemoveTemporaryObject), _objectIpcRemoveTemporaryObjectJson), stretch: false),
            drawTopSeparator: true);
        DrawObjectIpcJsonPayloadBlock(
            "objectIpcRemoveTemporaryLayoutPayload",
            "TemporaryLayoutRemoval Payload",
            "payload for RemoveTemporaryLayout",
            ref _objectIpcRemoveTemporaryLayoutJson,
            () => DrawObjectIpcInvokeButton("Remove Layout##objectIpcRemoveTemporaryLayout", () =>
                InvokeObjectIpcPayload<TemporaryLayoutRemoval, TemporarySourceMutationResult>(ObjectIpcSubscribers.RemoveTemporaryLayout.Label, nameof(ObjectIpcSubscribers.RemoveTemporaryLayout), _objectIpcRemoveTemporaryLayoutJson), stretch: false),
            drawTopSeparator: true);
    }

    private void DrawObjectIpcTesterResultSection()
    {
        if (!ImGui.CollapsingHeader("Last Result", ImGuiTreeNodeFlags.DefaultOpen))
        {
            return;
        }

        var statusLabel = string.IsNullOrWhiteSpace(_objectIpcLastAction)
            ? "idle"
            : _objectIpcLastSucceeded
                ? "success"
                : "error";
        ImGui.TextDisabled($"status: {statusLabel}");
        if (!string.IsNullOrWhiteSpace(_objectIpcLastAction))
        {
            ImGui.TextDisabled($"call: {_objectIpcLastAction}");
            ImGui.TextDisabled($"time: {_objectIpcLastInvokedAtUtc:yyyy-MM-dd HH:mm:ss} UTC");
        }

        if (!string.IsNullOrWhiteSpace(_objectIpcLastError))
        {
            ImGui.TextColored(EditorColors.DimRed, _objectIpcLastError);
        }

        var output = string.IsNullOrWhiteSpace(_objectIpcLastOutput)
            ? "No IPC call executed yet."
            : _objectIpcLastOutput;
        ImGui.InputTextMultiline(
            "##objectIpcLastResultOutput",
            ref output,
            ObjectIpcJsonInputMaxLength,
            new Vector2(-1f, ObjectIpcResultHeight * ImGuiHelpers.GlobalScale),
            ImGuiInputTextFlags.ReadOnly);
    }

    private void DrawObjectIpcSampleToolbar(string loadButtonId, bool canLoadSelected, Action loadAction, string resetButtonId, Action resetAction)
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
            () => DrawObjectIpcInvokeButton(buttonId, () => InvokeObjectIpcFunc<TResult>(ipcLabel, actionName)));
    }

    private void DrawObjectIpcObjectIdActionRow<TResult>(string label, string ipcLabel, string inputId, string buttonId, string actionName)
    {
        DrawObjectIpcActionRow(
            label,
            ipcLabel,
            () => DrawObjectIpcObjectIdInput(inputId),
            () => DrawObjectIpcInvokeButton(buttonId, () => InvokeObjectIpcGuidFunc<TResult>(ipcLabel, _objectIpcObjectIdInput, actionName)));
    }

    private void DrawObjectIpcLayoutIdActionRow<TResult>(string label, string ipcLabel, string inputId, string buttonId, string actionName)
    {
        DrawObjectIpcActionRow(
            label,
            ipcLabel,
            () => DrawObjectIpcLayoutIdInput(inputId),
            () => DrawObjectIpcInvokeButton(buttonId, () => InvokeObjectIpcGuidFunc<TResult>(ipcLabel, _objectIpcLayoutIdInput, actionName)));
    }

    private void EnableObjectIpcEventMonitor()
    {
        _objectIpcInitializedSubscriber?.Enable();
        _objectIpcDisposedSubscriber?.Enable();
        _objectIpcPersistentSceneChangedSubscriber?.Enable();
        _objectIpcEventMonitorEnabled = true;
    }

    private void DisableObjectIpcEventMonitor()
    {
        _objectIpcInitializedSubscriber?.Disable();
        _objectIpcDisposedSubscriber?.Disable();
        _objectIpcPersistentSceneChangedSubscriber?.Disable();
        _objectIpcEventMonitorEnabled = false;
    }

    private void ResetObjectIpcEventMonitor()
    {
        _objectIpcInitializedCount = 0;
        _objectIpcDisposedCount = 0;
        _objectIpcPersistentSceneChangedCount = 0;
        _objectIpcLastEventLabel = string.Empty;
        _objectIpcLastEventAtUtc = default;
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
        if (TryResolveReferenceObjectIpcSnapshot(out var snapshot))
        {
            _objectIpcObjectIdInput = snapshot.Id.ToString();
        }
    }

    private void LoadObjectIpcSamplesFromSelectedObject()
    {
        if (!TryResolveSelectedObjectIpcSnapshot(out var snapshot))
        {
            RecordObjectIpcFailure("LoadSelectedObject", "No selected object is available for IPC samples.");
            return;
        }

        _objectIpcObjectIdInput = snapshot.Id.ToString();
        _objectIpcLayoutIdInput = _layoutManager.GetDefaultLayoutId()?.ToString() ?? string.Empty;
        if (TryResolveSelectedPersistedObjectIpcSnapshot(out var persistedSnapshot))
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
        if (!TryResolveSelectedObjectIpcSnapshot(out var snapshot))
        {
            RecordObjectIpcFailure("LoadSelectedTemporaryObject", "No selected object is available for temporary IPC samples.");
            return;
        }

        _objectIpcObjectIdInput = snapshot.Id.ToString();
        ResetObjectIpcTemporarySamples(snapshot);
    }

    private void LoadObjectIpcMutationSamplesFromSelectedObject()
    {
        if (!TryResolveSelectedPersistedObjectIpcSnapshot(out var snapshot))
        {
            RecordObjectIpcFailure("LoadSelectedMutationObject", "No selected persisted object is available for mutation IPC samples.");
            return;
        }

        _objectIpcObjectIdInput = snapshot.Id.ToString();
        ResetObjectIpcMutationSamples(snapshot);
    }

    private void ResetObjectIpcMutationSamples()
        => ResetObjectIpcMutationSamples(snapshot: null);

    private void ResetObjectIpcMutationSamples(ObjectSnapshot? snapshot)
    {
        var worldObject = BuildObjectIpcMutationWorldObjectSample(snapshot);
        var patch = BuildObjectIpcPatchSample(worldObject);
        _objectIpcWorldObjectJson = SerializeObjectIpcPayload(worldObject);
        _objectIpcPatchObjectJson = SerializeObjectIpcPayload(patch);
    }

    private void ResetObjectIpcTemporarySamples()
        => ResetObjectIpcTemporarySamples(snapshot: null);

    private void ResetObjectIpcTemporarySamples(ObjectSnapshot? snapshot)
    {
        var worldObject = BuildObjectIpcWorldObjectSample(snapshot);
        var sessionId = Guid.NewGuid();
        var patch = BuildObjectIpcPatchSample(worldObject).Patch;

        _objectIpcApplyTemporaryLayoutJson = SerializeObjectIpcPayload(new TemporaryLayoutApplyRequest(
            ObjectIpcTemporarySourceKeySample,
            sessionId,
            ObjectIpcTemporarySourceNameSample,
            0,
            [worldObject]));
        _objectIpcApplyTemporaryChangesJson = SerializeObjectIpcPayload(new TemporaryObjectChangeSet(
            ObjectIpcTemporarySourceKeySample,
            sessionId,
            ObjectIpcTemporarySourceNameSample,
            0,
            [new TemporaryObjectChange(TemporaryObjectChangeKind.Upsert, worldObject)]));
        _objectIpcUpsertTemporaryObjectJson = SerializeObjectIpcPayload(new TemporaryObjectUpsert(
            ObjectIpcTemporarySourceKeySample,
            sessionId,
            ObjectIpcTemporarySourceNameSample,
            0,
            worldObject));
        _objectIpcPatchTemporaryObjectJson = SerializeObjectIpcPayload(new TemporaryObjectPatch(
            ObjectIpcTemporarySourceKeySample,
            sessionId,
            ObjectIpcTemporarySourceNameSample,
            0,
            worldObject.Id,
            patch));
        _objectIpcRemoveTemporaryObjectJson = SerializeObjectIpcPayload(new TemporaryObjectRemoval(
            ObjectIpcTemporarySourceKeySample,
            sessionId,
            worldObject.Id,
            0));
        _objectIpcRemoveTemporaryLayoutJson = SerializeObjectIpcPayload(new TemporaryLayoutRemoval(
            ObjectIpcTemporarySourceKeySample,
            sessionId,
            0));
    }

    private WorldObject BuildObjectIpcWorldObjectSample(ObjectSnapshot? snapshot = null)
    {
        if (snapshot is not null || TryResolveReferenceObjectIpcSnapshot(out snapshot))
        {
            return ObjectApiMapper.ToDto(snapshot);
        }

        return BuildFallbackObjectIpcWorldObjectSample();
    }

    private WorldObject BuildObjectIpcMutationWorldObjectSample(ObjectSnapshot? snapshot = null)
    {
        if (snapshot is not null || TryResolveReferencePersistedObjectIpcSnapshot(out snapshot))
        {
            return ObjectApiMapper.ToDto(snapshot);
        }

        return BuildFallbackObjectIpcWorldObjectSample();
    }

    private WorldObject BuildFallbackObjectIpcWorldObjectSample()
    {
        var createdIn = BuildObjectIpcCreationData();
        var vfxModel = _vfxCreate.Model;
        if (!string.IsNullOrWhiteSpace(vfxModel.VfxPath))
        {
            return new WorldObject(
                Guid.Empty,
                "IPC Tester VFX",
                WorldObjectKind.Vfx,
                _vfxCreate.Visible,
                new WorldObjectTransform(
                    new ObjectVector3(0f, 0f, 0f),
                    new ObjectVector3(0f, 0f, 0f),
                    new ObjectVector3(_vfxCreate.Scale.X, _vfxCreate.Scale.Y, _vfxCreate.Scale.Z)),
                default,
                createdIn,
                string.Empty,
                new WorldObjectModelData(
                    Vfx: new VfxModelData(
                        vfxModel.VfxPath,
                        new ObjectVector4(
                            vfxModel.Color.X,
                            vfxModel.Color.Y,
                            vfxModel.Color.Z,
                            vfxModel.Color.W),
                        vfxModel.Speed,
                        vfxModel.Paused,
                        vfxModel.FadeInSeconds,
                        vfxModel.ReplayOnTransform,
                        vfxModel.Loop,
                        vfxModel.LoopIntervalSeconds)));
        }

        if (!string.IsNullOrWhiteSpace(_furnitureCreate.SharedGroupPath))
        {
            return new WorldObject(
                Guid.Empty,
                "IPC Tester Furniture",
                WorldObjectKind.Furniture,
                _furnitureCreate.Visible,
                new WorldObjectTransform(
                    new ObjectVector3(0f, 0f, 0f),
                    new ObjectVector3(0f, 0f, 0f),
                    new ObjectVector3(_furnitureCreate.Scale.X, _furnitureCreate.Scale.Y, _furnitureCreate.Scale.Z)),
                default,
                createdIn,
                string.Empty,
                new WorldObjectModelData(
                    Furniture: new FurnitureModelData(
                        _furnitureCreate.SharedGroupPath,
                        new FurnitureColorData(
                            _furnitureCreate.StainId,
                            _furnitureCreate.UseCustomColor,
                            new ObjectVector4(
                                _furnitureCreate.CustomColor.X,
                                _furnitureCreate.CustomColor.Y,
                                _furnitureCreate.CustomColor.Z,
                                _furnitureCreate.CustomColor.W)),
                        _furnitureCreate.Transparency,
                        (Api.ObjectOutlineColor)_furnitureCreate.OutlineColor,
                        _furnitureCreate.HousingRowId,
                        _furnitureCreate.ItemRowId,
                        null)));
        }

        if (!string.IsNullOrWhiteSpace(_bgObjectCreate.ModelPath))
        {
            return new WorldObject(
                Guid.Empty,
                "IPC Tester BgObject",
                WorldObjectKind.BgObject,
                _bgObjectCreate.Visible,
                new WorldObjectTransform(
                    new ObjectVector3(0f, 0f, 0f),
                    new ObjectVector3(0f, 0f, 0f),
                    new ObjectVector3(_bgObjectCreate.Scale.X, _bgObjectCreate.Scale.Y, _bgObjectCreate.Scale.Z)),
                default,
                createdIn,
                string.Empty,
                new WorldObjectModelData(
                    BgObject: new BgObjectModelData(
                        _bgObjectCreate.ModelPath,
                        _bgObjectCreate.Transparency,
                        new ObjectVector4(
                            _bgObjectCreate.DyeColor.X,
                            _bgObjectCreate.DyeColor.Y,
                            _bgObjectCreate.DyeColor.Z,
                            _bgObjectCreate.DyeColor.W),
                        _bgObjectCreate.IsCoveredFromRain)));
        }

        return new WorldObject(
            Guid.Empty,
            "IPC Tester Light",
            WorldObjectKind.Light,
            _lightCreate.Visible,
            new WorldObjectTransform(
                new ObjectVector3(0f, 0f, 0f),
                new ObjectVector3(0f, 0f, 0f),
                new ObjectVector3(1f, 1f, 1f)),
            default,
            createdIn,
            string.Empty,
            new WorldObjectModelData(
                Light: new LightModelData(
                    new ObjectVector3(1f, 1f, 1f),
                    Api.ObjectLightType.AreaLight,
                    Api.ObjectLightFalloffType.Quadratic,
                    new LightFlagsData(true, true, false, false),
                    1f,
                    new LightShapeData(4f, 1f, 45f, 60f, new ObjectVector2(45f, 60f)),
                    new LightShadowData(0f, 0.01f, 4f))));
    }

    private ObjectPatchUpdate BuildObjectIpcPatchSample(WorldObject worldObject)
    {
        var position = worldObject.Transform.Position;
        return new ObjectPatchUpdate(
            worldObject.Id,
            new WorldObjectPatch(
                Name: $"{worldObject.Name} [IPC]",
                Transform: worldObject.Transform with
                {
                    Position = position with { X = position.X + 0.5f },
                }));
    }

    private ObjectCreationData BuildObjectIpcCreationData()
    {
        var context = _sceneView.GetCurrentLocationContext();
        return new ObjectCreationData(
            context.WorldId,
            context.WorldName,
            context.TerritoryId,
            context.TerritoryName,
            context.DivisionId,
            context.WardId,
            context.HouseId,
            context.RoomId);
    }

    private bool TryResolveSelectedObjectIpcSnapshot(out ObjectSnapshot snapshot)
        => TryResolveSelectedObjectIpcSnapshot(persistedOnly: false, out snapshot);

    private bool TryResolveReferenceObjectIpcSnapshot(out ObjectSnapshot snapshot)
        => TryResolveReferenceObjectIpcSnapshot(persistedOnly: false, out snapshot);

    private bool TryResolveSelectedPersistedObjectIpcSnapshot(out ObjectSnapshot snapshot)
        => TryResolveSelectedObjectIpcSnapshot(persistedOnly: true, out snapshot);

    private bool TryResolveReferencePersistedObjectIpcSnapshot(out ObjectSnapshot snapshot)
        => TryResolveReferenceObjectIpcSnapshot(persistedOnly: true, out snapshot);

    private bool TryResolveSelectedObjectIpcSnapshot(bool persistedOnly, out ObjectSnapshot snapshot)
    {
        var selectedObjectId = _editorSelection.PrimaryObjectId;
        if (!selectedObjectId.HasValue)
        {
            snapshot = null!;
            return false;
        }

        var id = selectedObjectId.Value;
        if (!persistedOnly && _sceneView.TryGetSceneObjectSnapshot(id, out snapshot))
        {
            return true;
        }

        if (_sceneView.TryGetPersistedObjectSnapshot(id, out snapshot))
        {
            return true;
        }

        snapshot = null!;
        return false;
    }

    private bool TryResolveReferenceObjectIpcSnapshot(bool persistedOnly, out ObjectSnapshot snapshot)
    {
        if (TryResolveSelectedObjectIpcSnapshot(persistedOnly, out snapshot))
        {
            return true;
        }

        var placedSnapshots = _sceneView.GetPlacedObjectSnapshots();
        if (placedSnapshots.Count > 0)
        {
            snapshot = placedSnapshots[0];
            return true;
        }

        snapshot = null!;
        return false;
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
            _clipboardExportService.CopyText(json);
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
            json = TryFormatObjectIpcJson(json);
        }

        ImGui.SameLine();
        if (ImGui.Button($"Copy##{popupId}"))
        {
            _clipboardExportService.CopyText(json);
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
        using (ImRaii.Disabled(!TryResolveSelectedObjectIpcSnapshot(out _)))
        {
            if (ImGui.Button($"Selected{id}", new Vector2(buttonWidth, 0f))
                && TryResolveSelectedObjectIpcSnapshot(out var snapshot))
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

    private void DrawObjectIpcInvokeButton(string id, Action onClick, bool stretch = true)
    {
        var size = stretch ? new Vector2(-1f, 0f) : Vector2.Zero;
        if (ImGui.Button(id, size))
        {
            onClick();
        }
    }

    private void InvokeObjectIpcGuidFunc<TResult>(string label, string input, string actionName)
    {
        if (!TryParseObjectIpcGuid(input, actionName, out var value))
        {
            return;
        }

        InvokeObjectIpcFunc<Guid, TResult>(label, value, actionName);
    }

    private void InvokeObjectIpcFunc<TResult>(string label, string actionName)
    {
        try
        {
            var result = _pluginInterface.GetIpcSubscriber<TResult>(label).InvokeFunc();
            RecordObjectIpcSuccess(actionName, result);
        }
        catch (Exception ex)
        {
            RecordObjectIpcFailure(actionName, ex);
        }
    }

    private void InvokeObjectIpcFunc<T1, TResult>(string label, T1 argument, string actionName)
    {
        try
        {
            var result = _pluginInterface.GetIpcSubscriber<T1, TResult>(label).InvokeFunc(argument);
            RecordObjectIpcSuccess(actionName, result);
        }
        catch (Exception ex)
        {
            RecordObjectIpcFailure(actionName, ex);
        }
    }

    private void InvokeObjectIpcPayload<TPayload, TResult>(string label, string actionName, string json)
    {
        if (!TryDeserializeObjectIpcPayload(json, actionName, out TPayload payload))
        {
            return;
        }

        InvokeObjectIpcFunc<TPayload, TResult>(label, payload, actionName);
    }

    private bool TryDeserializeObjectIpcPayload<TPayload>(string json, string actionName, out TPayload payload)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            payload = default!;
            RecordObjectIpcFailure(actionName, "The JSON payload is empty.");
            return false;
        }

        try
        {
            var deserialized = JsonSerializer.Deserialize<TPayload>(json, ObjectIpcTesterJsonOptions);
            if (deserialized is null)
            {
                payload = default!;
                RecordObjectIpcFailure(actionName, "The JSON payload deserialized to null.");
                return false;
            }

            payload = deserialized;
            return true;
        }
        catch (Exception ex)
        {
            payload = default!;
            RecordObjectIpcFailure(actionName, $"JSON parse failed: {ex.Message}");
            return false;
        }
    }

    private bool TryParseObjectIpcGuid(string input, string actionName, out Guid value)
    {
        if (Guid.TryParse(input, out value))
        {
            return true;
        }

        RecordObjectIpcFailure(actionName, $"Could not parse GUID from '{input}'.");
        return false;
    }

    private void RecordObjectIpcEvent(IpcTesterEventKind eventKind)
    {
        switch (eventKind)
        {
            case IpcTesterEventKind.Initialized:
                ++_objectIpcInitializedCount;
                break;
            case IpcTesterEventKind.Disposed:
                ++_objectIpcDisposedCount;
                break;
            case IpcTesterEventKind.PersistentSceneChanged:
                ++_objectIpcPersistentSceneChangedCount;
                break;
        }

        _objectIpcLastEventLabel = eventKind.ToString();
        _objectIpcLastEventAtUtc = DateTime.UtcNow;
    }

    private void RecordObjectIpcSuccess(string actionName, object? result)
    {
        _objectIpcLastAction = actionName;
        _objectIpcLastSucceeded = true;
        _objectIpcLastError = string.Empty;
        _objectIpcLastInvokedAtUtc = DateTime.UtcNow;
        _objectIpcLastOutput = FormatObjectIpcResult(result);
    }

    private void RecordObjectIpcFailure(string actionName, Exception exception)
        => RecordObjectIpcFailure(actionName, exception.ToString());

    private void RecordObjectIpcFailure(string actionName, string message)
    {
        _objectIpcLastAction = actionName;
        _objectIpcLastSucceeded = false;
        _objectIpcLastError = message;
        _objectIpcLastInvokedAtUtc = DateTime.UtcNow;
        _objectIpcLastOutput = string.Empty;
    }

    private static string SerializeObjectIpcPayload<TPayload>(TPayload payload)
        => JsonSerializer.Serialize(payload, ObjectIpcTesterJsonOptions);

    private static string FormatObjectIpcResult(object? result)
        => result switch
        {
            null => "null",
            string text => text,
            _ => JsonSerializer.Serialize(result, ObjectIpcTesterJsonOptions),
        };

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

    private static string TryFormatObjectIpcJson(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(document.RootElement, ObjectIpcTesterJsonOptions);
        }
        catch
        {
            return json;
        }
    }
}

