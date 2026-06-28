using Dalamud.Bindings.ImGui;
using Intoner.Objects.Models;
using Intoner.Objects.Utils;
using System.Numerics;
using System.Text.Json;

namespace Intoner.Objects.UI.Services;

internal enum TransformClipboardKind
{
    Position,
    Rotation,
    Scale,
}

/// <summary> reads and writes typed clipboard payloads </summary>
internal interface IClipboardExportService
{
    /// <summary> copies a object snapshot payload to the system clipboard </summary>
    /// <param name="snapshot">object snapshot to export</param>
    void CopySnapshot(ObjectSnapshot snapshot);

    /// <summary> attempts to read a object snapshot payload from the system clipboard </summary>
    /// <param name="snapshot">resolved object snapshot</param>
    /// <returns>true when the clipboard contains a valid snapshot payload</returns>
    bool TryPasteSnapshot(out ObjectSnapshot snapshot);

    /// <summary> copies one transform property payload to the system clipboard </summary>
    /// <param name="kind">transform property kind</param>
    /// <param name="value">transform property value</param>
    void CopyTransform(TransformClipboardKind kind, Vector3 value);

    /// <summary> attempts to read a matching transform property payload from the system clipboard </summary>
    /// <param name="kind">expected transform property kind</param>
    /// <param name="value">resolved transform property value</param>
    /// <returns>true when the clipboard contains a valid matching transform payload</returns>
    bool TryPasteTransform(TransformClipboardKind kind, out Vector3 value);

    /// <summary> copies plain text to the system clipboard </summary>
    /// <param name="text">text to copy</param>
    void CopyText(string text);
}

internal sealed class ClipboardExportService : IClipboardExportService
{
    private const string PayloadType = "intoner.clipboard";

    private static readonly JsonSerializerOptions JsonOptions =
        ObjectJsonSerializerOptionsUtility.CreateStrictIndented(JsonNamingPolicy.CamelCase);

    public void CopySnapshot(ObjectSnapshot snapshot)
        => ImGui.SetClipboardText(SerializePayload(ClipboardPayload.ForSnapshot(CreateDetachedSnapshot(snapshot))));

    public bool TryPasteSnapshot(out ObjectSnapshot snapshot)
    {
        snapshot = null!;
        if (!TryReadPayload(ClipboardPayloadKind.Snapshot, out ClipboardPayload payload)
            || payload.Snapshot is not { } payloadSnapshot)
        {
            return false;
        }

        snapshot = CreateDetachedSnapshot(payloadSnapshot);
        return true;
    }

    public void CopyTransform(TransformClipboardKind kind, Vector3 value)
    {
        if (!ObjectMathUtility.IsFinite(value))
        {
            return;
        }

        ImGui.SetClipboardText(SerializePayload(ClipboardPayload.ForTransform(kind, value)));
    }

    public bool TryPasteTransform(TransformClipboardKind kind, out Vector3 value)
        => TryReadTransform(kind, out value);

    public void CopyText(string text)
        => ImGui.SetClipboardText(text);

    private static string SerializePayload(ClipboardPayload payload)
        => JsonSerializer.Serialize(payload, JsonOptions);

    private static ObjectSnapshot CreateDetachedSnapshot(ObjectSnapshot snapshot)
        => snapshot with { LayoutId = null };

    private static bool TryReadPayload(ClipboardPayloadKind expectedKind, out ClipboardPayload payload)
    {
        payload = null!;
        string text = ImGui.GetClipboardText() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        try
        {
            ClipboardPayload? parsedPayload = JsonSerializer.Deserialize<ClipboardPayload>(text, JsonOptions);
            if (parsedPayload is null
                || !string.Equals(parsedPayload.Type, PayloadType, StringComparison.Ordinal)
                || parsedPayload.Kind != expectedKind)
            {
                return false;
            }

            payload = parsedPayload;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryReadTransform(TransformClipboardKind expectedKind, out Vector3 value)
    {
        value = default;
        if (!TryReadPayload(ClipboardPayloadKind.Transform, out ClipboardPayload payload)
            || payload.Transform is not { } transform
            || transform.Kind != expectedKind)
        {
            return false;
        }

        Vector3 parsedValue = transform.Value.ToVector3();
        if (!ObjectMathUtility.IsFinite(parsedValue))
        {
            return false;
        }

        value = parsedValue;
        return true;
    }

    private enum ClipboardPayloadKind
    {
        Snapshot,
        Transform,
    }

    private sealed record ClipboardPayload(
        string Type,
        ClipboardPayloadKind Kind,
        ObjectSnapshot? Snapshot = null,
        TransformClipboardValue? Transform = null)
    {
        public static ClipboardPayload ForSnapshot(ObjectSnapshot snapshot)
            => new(PayloadType, ClipboardPayloadKind.Snapshot, Snapshot: snapshot);

        public static ClipboardPayload ForTransform(TransformClipboardKind kind, Vector3 value)
            => new(
                PayloadType,
                ClipboardPayloadKind.Transform,
                Transform: new TransformClipboardValue(kind, ClipboardVector.From(value)));
    }

    private sealed record TransformClipboardValue(
        TransformClipboardKind Kind,
        ClipboardVector Value);

    private readonly record struct ClipboardVector(float X, float Y, float Z)
    {
        public static ClipboardVector From(Vector3 value)
            => new(value.X, value.Y, value.Z);

        public Vector3 ToVector3()
            => new(X, Y, Z);
    }
}

