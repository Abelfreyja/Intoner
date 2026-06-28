namespace Intoner.Objects.Runtime;

/// <summary>
/// Defines shared runtime failure codes reported by the object scene.
/// </summary>
internal static class ObjectRuntimeFailureCodes
{
    /// <summary>
    /// The requested object kind is not currently creatable in this runtime.
    /// </summary>
    public const string ServiceMissing = "service_missing";

    /// <summary>
    /// The snapshot payload is not valid for its object kind.
    /// </summary>
    public const string InvalidObject = "invalid_object";

    /// <summary>
    /// The runtime failed while creating the scene object for the snapshot.
    /// </summary>
    public const string CreateFailed = "create_failed";

    /// <summary>
    /// The snapshot root resource path is empty, malformed, or not valid for the object kind.
    /// </summary>
    public const string InvalidAssetPath = "invalid_asset_path";

    /// <summary>
    /// The snapshot root resource is not installed in the local game data.
    /// </summary>
    public const string MissingAsset = "missing_asset";

    /// <summary>
    /// The collection redirected root resource is not available locally.
    /// </summary>
    public const string MissingRedirectAsset = "missing_redirect_asset";

    /// <summary>
    /// The collection redirected root local file cannot be loaded by the current resource hooks.
    /// </summary>
    public const string UnsupportedLocalFile = "unsupported_local_file";
    public const string UnsupportedMemoryResource = "unsupported_memory_resource";

    /// <summary>
    /// The collection redirected root resource does not match the requested resource kind.
    /// </summary>
    public const string InvalidRedirectKind = "invalid_redirect_kind";

    /// <summary>
    /// The collection redirected root resource cannot be applied because required hooks are unavailable.
    /// </summary>
    public const string ResourceHooksUnavailable = "resource_hooks_unavailable";

    /// <summary>
    /// The scene object rejected the snapshot update and was not recreated.
    /// </summary>
    public const string UpdateRejected = "update_rejected";

    /// <summary>
    /// Native creation returned a layout instance outside the object runtime contract.
    /// </summary>
    public const string NativeLayoutRejected = "native_layout_rejected";

    /// <summary>
    /// Housing mode rejected the object because it violates housing constraints.
    /// </summary>
    public const string HousingModeRejected = "housing_mode_rejected";

    /// <summary>
    /// Checks whether a failed scene load should be retried without a layout or resource state change.
    /// </summary>
    /// <param name="code">the runtime failure code</param>
    /// <returns>true when the failure may be transient</returns>
    public static bool ShouldRetrySceneLoad(string? code)
        => string.Equals(code, CreateFailed, StringComparison.Ordinal);
}

