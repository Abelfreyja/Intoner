using Dalamud.Plugin;
using Intoner.Ipc;
using Intoner.Objects.Api.Ipc;
using Intoner.Objects.Api;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Intoner.Objects.UI;

internal sealed class DebugIpcSession : IDisposable
{
    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly ILogger<DebugIpcSession> _logger;
    private static readonly JsonSerializerOptions ObjectIpcTesterJsonOptions = CreateObjectIpcTesterJsonOptions();
    private EventSubscriber<ObjectApiStateChanged>? _objectIpcStateChangedSubscriber;
    private EventSubscriber<ObjectSceneChanged>? _objectIpcSceneChangedSubscriber;
    private EventSubscriber<PersistentObjectSceneChanged>? _objectIpcPersistentSceneChangedSubscriber;
    private EventSubscriber<SavedObjectLayoutsChanged>? _objectIpcSavedLayoutsChangedSubscriber;
    private bool _objectIpcTesterInitialized;

    public bool EventMonitorEnabled { get; private set; }

    public int StateChangedCount { get; private set; }

    public int SceneChangedCount { get; private set; }

    public int PersistentSceneChangedCount { get; private set; }

    public int SavedLayoutsChangedCount { get; private set; }

    public DateTime LastEventAtUtc { get; private set; }

    public string LastEventLabel { get; private set; } = string.Empty;

    public string LastAction { get; private set; } = string.Empty;

    public string LastOutput { get; private set; } = string.Empty;

    public string LastError { get; private set; } = string.Empty;

    public bool LastSucceeded { get; private set; }

    public DateTime LastInvokedAtUtc { get; private set; }

    public DebugIpcSession(IDalamudPluginInterface pluginInterface, ILogger<DebugIpcSession> logger)
    {
        _pluginInterface = pluginInterface;
        _logger          = logger;
    }

    public void Dispose()
        => DisposeObjectIpcTester();

    internal enum IpcTesterEventKind
    {
        StateChanged,
        SceneChanged,
        PersistentSceneChanged,
        SavedLayoutsChanged,
    }

    private static JsonSerializerOptions CreateObjectIpcTesterJsonOptions()
        => new()
        {
            WriteIndented = true,
            IncludeFields = true,
            RespectRequiredConstructorParameters = true,
        };

    internal void EnsureObjectIpcTesterInitialized()
    {
        if (_objectIpcTesterInitialized)
        {
            return;
        }

        _objectIpcStateChangedSubscriber = new EventSubscriber<ObjectApiStateChanged>(
            _pluginInterface,
            _logger,
            ObjectIpcEndpoints.Events.StateChanged,
            state => RecordObjectIpcEvent(IpcTesterEventKind.StateChanged, state.Info.State.ToString()));
        _objectIpcSceneChangedSubscriber = new EventSubscriber<ObjectSceneChanged>(
            _pluginInterface,
            _logger,
            ObjectIpcEndpoints.Events.SceneChanged,
            change => RecordObjectIpcEvent(IpcTesterEventKind.SceneChanged, change.SceneRevision.ToString()));
        _objectIpcPersistentSceneChangedSubscriber = new EventSubscriber<PersistentObjectSceneChanged>(
            _pluginInterface,
            _logger,
            ObjectIpcEndpoints.Events.PersistentSceneChanged,
            change => RecordObjectIpcEvent(IpcTesterEventKind.PersistentSceneChanged, change.PersistentRevision.ToString()));
        _objectIpcSavedLayoutsChangedSubscriber = new EventSubscriber<SavedObjectLayoutsChanged>(
            _pluginInterface,
            _logger,
            ObjectIpcEndpoints.Events.SavedLayoutsChanged,
            change => RecordObjectIpcEvent(IpcTesterEventKind.SavedLayoutsChanged, change.SavedLayoutsRevision.ToString()));
        _objectIpcStateChangedSubscriber.Disable();
        _objectIpcSceneChangedSubscriber.Disable();
        _objectIpcPersistentSceneChangedSubscriber.Disable();
        _objectIpcSavedLayoutsChangedSubscriber.Disable();
        _objectIpcTesterInitialized = true;
    }

    private void DisposeObjectIpcTester()
    {
        _objectIpcStateChangedSubscriber?.Dispose();
        _objectIpcSceneChangedSubscriber?.Dispose();
        _objectIpcPersistentSceneChangedSubscriber?.Dispose();
        _objectIpcSavedLayoutsChangedSubscriber?.Dispose();
        _objectIpcStateChangedSubscriber = null;
        _objectIpcSceneChangedSubscriber = null;
        _objectIpcPersistentSceneChangedSubscriber = null;
        _objectIpcSavedLayoutsChangedSubscriber = null;
        EventMonitorEnabled = false;
        _objectIpcTesterInitialized = false;
    }

    internal void EnableObjectIpcEventMonitor()
    {
        _objectIpcStateChangedSubscriber?.Enable();
        _objectIpcSceneChangedSubscriber?.Enable();
        _objectIpcPersistentSceneChangedSubscriber?.Enable();
        _objectIpcSavedLayoutsChangedSubscriber?.Enable();
        EventMonitorEnabled = true;
    }

    internal void DisableObjectIpcEventMonitor()
    {
        _objectIpcStateChangedSubscriber?.Disable();
        _objectIpcSceneChangedSubscriber?.Disable();
        _objectIpcPersistentSceneChangedSubscriber?.Disable();
        _objectIpcSavedLayoutsChangedSubscriber?.Disable();
        EventMonitorEnabled = false;
    }

    internal void ResetObjectIpcEventMonitor()
    {
        StateChangedCount = 0;
        SceneChangedCount = 0;
        PersistentSceneChangedCount = 0;
        SavedLayoutsChangedCount = 0;
        LastEventLabel = string.Empty;
        LastEventAtUtc = default;
    }

    internal void InvokeObjectIpcGuidFunc<TResult>(string label, string input, string actionName)
    {
        if (!TryParseObjectIpcGuid(input, actionName, out var value))
        {
            return;
        }

        InvokeObjectIpcFunc<Guid, TResult>(label, value, actionName);
    }

    internal void InvokeObjectIpcFunc<TResult>(string label, string actionName)
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

    internal void InvokeObjectIpcFunc<T1, TResult>(string label, T1 argument, string actionName)
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

    internal void InvokeObjectIpcPayload<TPayload, TResult>(string label, string actionName, string json)
    {
        if (!TryDeserializeObjectIpcPayload(json, actionName, out TPayload payload))
        {
            return;
        }

        InvokeObjectIpcFunc<TPayload, TResult>(label, payload, actionName);
    }

    internal bool TryDeserializeObjectIpcPayload<TPayload>(string json, string actionName, out TPayload payload)
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

    internal bool TryParseObjectIpcGuid(string input, string actionName, out Guid value)
    {
        if (Guid.TryParse(input, out value))
        {
            return true;
        }

        RecordObjectIpcFailure(actionName, $"Could not parse GUID from '{input}'.");
        return false;
    }

    private void RecordObjectIpcEvent(IpcTesterEventKind eventKind, string detail)
    {
        switch (eventKind)
        {
            case IpcTesterEventKind.StateChanged:
                ++StateChangedCount;
                break;
            case IpcTesterEventKind.SceneChanged:
                ++SceneChangedCount;
                break;
            case IpcTesterEventKind.PersistentSceneChanged:
                ++PersistentSceneChangedCount;
                break;
            case IpcTesterEventKind.SavedLayoutsChanged:
                ++SavedLayoutsChangedCount;
                break;
        }

        LastEventLabel = $"{eventKind}: {detail}";
        LastEventAtUtc = DateTime.UtcNow;
    }

    private void RecordObjectIpcSuccess(string actionName, object? result)
    {
        LastAction = actionName;
        LastSucceeded = true;
        LastError = string.Empty;
        LastInvokedAtUtc = DateTime.UtcNow;
        LastOutput = FormatObjectIpcResult(result);
    }

    private void RecordObjectIpcFailure(string actionName, Exception exception)
        => RecordObjectIpcFailure(actionName, exception.ToString());

    internal void RecordObjectIpcFailure(string actionName, string message)
    {
        LastAction = actionName;
        LastSucceeded = false;
        LastError = message;
        LastInvokedAtUtc = DateTime.UtcNow;
        LastOutput = string.Empty;
    }

    internal static string SerializeObjectIpcPayload<TPayload>(TPayload payload)
        => JsonSerializer.Serialize(payload, ObjectIpcTesterJsonOptions);

    private static string FormatObjectIpcResult(object? result)
        => result switch
        {
            null => "null",
            string text => text,
            _ => JsonSerializer.Serialize(result, ObjectIpcTesterJsonOptions),
        };

    internal static string TryFormatObjectIpcJson(string json)
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
