using MessagePack;
using System.Buffers.Binary;
using System.IO.Compression;

namespace Intoner.Objects.Api;

internal enum ObjectTransferDecodeStatus
{
    Success,
    Empty,
    NotIntoner,
    UnsupportedVersion,
    TooLarge,
    Invalid,
}

internal readonly record struct ObjectTransferDecodeResult(
    ObjectTransferDecodeStatus Status,
    ObjectTransferDocument? Document)
{
    public bool IsSuccess => Status == ObjectTransferDecodeStatus.Success;
}

/// <summary> encodes versioned intoner object transfers as clipboard safe text </summary>
internal static class ObjectTransferCodec
{
    internal const int MaximumTextLength = 4 * 1024 * 1024;
    internal const int MaximumObjectCount = 4096;

    private const byte RawPayload = 0;
    private const byte BrotliPayload = 1;
    private const int MaximumPayloadLength = 8 * 1024 * 1024;
    private const string RootPrefix = "INTONER:";
    private const string FormatVersion = "1";

    internal const string Prefix = RootPrefix + FormatVersion + ":";

    private static readonly MessagePackSerializerOptions StandardOptions =
        MessagePackSerializerOptions.Standard.WithSecurity(MessagePackSecurity.UntrustedData);

    public static bool TryEncode(ObjectTransferDocument? document, out string text)
    {
        text = string.Empty;
        if (document is null || !IsValid(document))
        {
            return false;
        }

        byte[] messagePack = MessagePackSerializer.Serialize(document, StandardOptions);
        if (messagePack.Length > MaximumPayloadLength)
        {
            return false;
        }

        byte[] payload = EncodePayload(messagePack);
        string encoded = Prefix + EncodeBase64Url(payload);
        if (encoded.Length > MaximumTextLength)
        {
            return false;
        }

        text = encoded;
        return true;
    }

    public static ObjectTransferDecodeResult Decode(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new ObjectTransferDecodeResult(ObjectTransferDecodeStatus.Empty, null);
        }

        string value = text.Trim();
        if (value.Length > MaximumTextLength)
        {
            return new ObjectTransferDecodeResult(ObjectTransferDecodeStatus.TooLarge, null);
        }

        if (!value.StartsWith(RootPrefix, StringComparison.Ordinal))
        {
            return new ObjectTransferDecodeResult(ObjectTransferDecodeStatus.NotIntoner, null);
        }

        int versionEnd = value.IndexOf(':', RootPrefix.Length);
        if (versionEnd < 0
            || !value.AsSpan(RootPrefix.Length, versionEnd - RootPrefix.Length)
                .SequenceEqual(FormatVersion.AsSpan()))
        {
            return new ObjectTransferDecodeResult(ObjectTransferDecodeStatus.UnsupportedVersion, null);
        }

        try
        {
            byte[] payload = DecodeBase64Url(value[(versionEnd + 1)..]);
            if (!TryDecodePayload(payload, out ReadOnlyMemory<byte> messagePack))
            {
                return new ObjectTransferDecodeResult(ObjectTransferDecodeStatus.Invalid, null);
            }

            ObjectTransferDocument? document = MessagePackSerializer.Deserialize<ObjectTransferDocument>(
                messagePack,
                StandardOptions);
            return document is not null && IsValid(document)
                ? new ObjectTransferDecodeResult(ObjectTransferDecodeStatus.Success, document)
                : new ObjectTransferDecodeResult(ObjectTransferDecodeStatus.Invalid, null);
        }
        catch (Exception exception) when (exception is FormatException
            or MessagePackSerializationException
            or OverflowException)
        {
            return new ObjectTransferDecodeResult(ObjectTransferDecodeStatus.Invalid, null);
        }
    }

    private static bool IsValid(ObjectTransferDocument document)
        => document.Kind switch
        {
            ObjectTransferKind.SceneObjects => document is
            {
                SceneObjects: { } transfer,
                ObjectLibrary: null,
                Transform: null,
            }
                && IsValid(transfer),
            ObjectTransferKind.Transform => document is
            {
                SceneObjects: null,
                ObjectLibrary: null,
                Transform: { } transform,
            }
                && Enum.IsDefined(transform.Part)
                && IsFinite(transform.Value),
            ObjectTransferKind.ObjectLibrary => document is
            {
                SceneObjects: null,
                ObjectLibrary: { } transfer,
                Transform: null,
            }
                && IsValid(transfer),
            _ => false,
        };

    private static bool IsValid(SceneObjectTransfer transfer)
    {
        bool isFolder = transfer.FolderName is not null;
        return transfer.Content is not null
            && (isFolder || transfer.Content.Objects.Count > 0)
            && IsValid(transfer.Content, allowEmpty: isFolder);
    }

    private static bool IsValid(PersistentObjectSet content, bool allowEmpty)
    {
        if (content.Objects is null
            || content.Folders is null
            || content.FolderColors is null
            || content.Objects.Count > MaximumObjectCount
            || (!allowEmpty && content.Objects.Count == 0)
            || content.Folders.Count > MaximumObjectCount
            || content.FolderColors.Count > MaximumObjectCount)
        {
            return false;
        }

        HashSet<Guid> objectIds = [];
        foreach (PersistentObject? entry in content.Objects)
        {
            if (entry?.Object is null
                || entry.Object.Id == Guid.Empty
                || !objectIds.Add(entry.Object.Id))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsValid(ObjectLibraryTransfer transfer)
    {
        if (transfer.RootGroupId == Guid.Empty
            || transfer.Groups is null
            || transfer.Entries is null
            || transfer.Groups.Count == 0
            || transfer.Groups.Count > MaximumObjectCount
            || transfer.Entries.Count > MaximumObjectCount
            || !transfer.Groups.Any(group => group?.Id == transfer.RootGroupId))
        {
            return false;
        }

        HashSet<Guid> groupIds = [];
        foreach (ObjectLibraryTransferGroup? group in transfer.Groups)
        {
            if (group is null
                || group.Id == Guid.Empty
                || group.Name is null
                || group.Color is null
                || group.EntryIds is null
                || !Enum.IsDefined(group.Kind)
                || !groupIds.Add(group.Id))
            {
                return false;
            }
        }

        HashSet<Guid> entryIds = [];
        foreach (ObjectLibraryTransferEntry? entry in transfer.Entries)
        {
            if (entry is null
                || entry.Id == Guid.Empty
                || entry.Name is null
                || entry.Model is null
                || !entryIds.Add(entry.Id)
                || !IsFinite(entry.Scale))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsFinite(ObjectVector3 value)
        => float.IsFinite(value.X)
        && float.IsFinite(value.Y)
        && float.IsFinite(value.Z);

    private static string EncodeBase64Url(ReadOnlySpan<byte> bytes)
        => Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static byte[] DecodeBase64Url(string value)
    {
        string padded = value
            .Replace('-', '+')
            .Replace('_', '/');
        padded = (padded.Length % 4) switch
        {
            0 => padded,
            2 => padded + "==",
            3 => padded + "=",
            _ => throw new FormatException("invalid base64url payload"),
        };
        return Convert.FromBase64String(padded);
    }

    private static byte[] EncodePayload(ReadOnlySpan<byte> messagePack)
    {
        byte[] compressed = GC.AllocateUninitializedArray<byte>(BrotliEncoder.GetMaxCompressedLength(messagePack.Length) + 5);
        if (BrotliEncoder.TryCompress(messagePack, compressed.AsSpan(5), out int compressedLength, quality: 5, window: 22)
            && compressedLength + 5 < messagePack.Length + 1)
        {
            compressed[0] = BrotliPayload;
            BinaryPrimitives.WriteInt32LittleEndian(compressed.AsSpan(1, 4), messagePack.Length);
            return compressed[..(compressedLength + 5)];
        }

        byte[] raw = GC.AllocateUninitializedArray<byte>(messagePack.Length + 1);
        raw[0] = RawPayload;
        messagePack.CopyTo(raw.AsSpan(1));
        return raw;
    }

    private static bool TryDecodePayload(byte[] payload, out ReadOnlyMemory<byte> messagePack)
    {
        messagePack = default;
        if (payload.Length < 2)
        {
            return false;
        }

        if (payload[0] == RawPayload)
        {
            if (payload.Length - 1 > MaximumPayloadLength)
            {
                return false;
            }

            messagePack = payload.AsMemory(1);
            return true;
        }

        if (payload[0] != BrotliPayload || payload.Length < 6)
        {
            return false;
        }

        int uncompressedLength = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(1, 4));
        if (uncompressedLength is < 1 or > MaximumPayloadLength)
        {
            return false;
        }

        byte[] uncompressed = GC.AllocateUninitializedArray<byte>(uncompressedLength);
        if (!BrotliDecoder.TryDecompress(payload.AsSpan(5), uncompressed, out int bytesWritten)
            || bytesWritten != uncompressedLength)
        {
            return false;
        }

        messagePack = uncompressed;
        return true;
    }
}
