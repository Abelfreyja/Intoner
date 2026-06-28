using FFXIVClientStructs.FFXIV.Client.System.Resource.Handle;
using System.Globalization;

namespace Intoner.Objects.Resources;

/// <summary> checks model resource handle state before xiv parses it </summary>
internal static unsafe class ObjectModelResourceLoadGuard
{
    private const byte NativeModelLoadFailure = 0xF6;

    private const uint MinModelVersion = 0x01000003;
    private const uint MaxModelVersion = 0x01000006;

    // ModelResourceHandle.Load reads the parsed mdl version from this field
    private const int ModelVersionOffset = 0xB0;

    public static byte FailureResult
        => NativeModelLoadFailure;

    public static ObjectModelResourceValidationResult Validate(ModelResourceHandle* handle)
    {
        if (handle == null)
        {
            return ObjectModelResourceValidationResult.Invalid("model handle is null", 0, 0);
        }

        var resourceHandle = (ResourceHandle*)handle;
        uint length = ResolveModelDataLength(resourceHandle);
        uint version = *(uint*)((byte*)handle + ModelVersionOffset);

        if (handle->ModelData == null)
        {
            return ObjectModelResourceValidationResult.Invalid("model data pointer is null", length, version);
        }

        if (length == 0)
        {
            return ObjectModelResourceValidationResult.Invalid("model data length is zero", length, version);
        }

        if (version is < MinModelVersion or > MaxModelVersion)
        {
            return ObjectModelResourceValidationResult.Invalid(
                $"unsupported model version 0x{version.ToString("X8", CultureInfo.InvariantCulture)}",
                length,
                version);
        }

        return ObjectModelResourceValidationResult.Valid(length, version);
    }

    private static uint ResolveModelDataLength(ResourceHandle* handle)
    {
        uint fileSize = handle->FileSize;
        uint fullFileSize = handle->FileSize2;
        return fileSize == 0 || fullFileSize == 0
            ? Math.Max(fileSize, fullFileSize)
            : Math.Min(fileSize, fullFileSize);
    }
}

internal readonly record struct ObjectModelResourceValidationResult(bool IsValid, string Reason, uint Length, uint Version)
{
    public static ObjectModelResourceValidationResult Valid(uint length, uint version)
        => new(true, string.Empty, length, version);

    public static ObjectModelResourceValidationResult Invalid(string reason, uint length, uint version)
        => new(false, reason, length, version);
}


