using MessagePack;

namespace Intoner.Objects.Api;

/// <summary> temporary source build result status </summary>
public enum TemporarySourceBuildStatus
{
    Success = 0,
    CompletedWithWarnings = 1,
    InvalidRequest = 2,
}

/// <summary> temporary source build diagnostic severity </summary>
public enum TemporarySourceBuildDiagnosticSeverity
{
    Info = 1,
    Warning = 2,
    Error = 3,
}

/// <summary> request to build temporary layout and collection payloads from local objects </summary>
/// <param name="SourceKey"> temporary source key used by the consumer </param>
/// <param name="SourceSessionId"> source session id used by the consumer </param>
/// <param name="Name"> temporary source display name </param>
/// <param name="Revision"> source revision, or '0' to advance it locally on the consumer </param>
/// <param name="Objects"> local objects to include in the temporary layout payload </param>
[MessagePackObject(keyAsPropertyName: true)]
public sealed record TemporarySourceBuildRequest(
    string SourceKey,
    Guid SourceSessionId,
    string Name,
    long Revision,
    IReadOnlyList<WorldObject> Objects);

/// <summary> one diagnostic from temporary source payload construction </summary>
/// <param name="ObjectId"> object id associated with the diagnostic, or empty when global </param>
/// <param name="CollectionId"> authored collection id associated with the diagnostic </param>
/// <param name="Severity"> diagnostic severity </param>
/// <param name="Message"> diagnostic text </param>
[MessagePackObject(keyAsPropertyName: true)]
public sealed record TemporarySourceBuildDiagnostic(
    Guid ObjectId,
    string CollectionId,
    TemporarySourceBuildDiagnosticSeverity Severity,
    string Message);

/// <summary> temporary layout and collection payloads built from local objects </summary>
/// <param name="Status"> build status </param>
/// <param name="Message"> summary message </param>
/// <param name="Layout"> temporary layout replacement with generated collection ids </param>
/// <param name="Collections"> temporary collection replacement with compiled redirect set </param>
/// <param name="LocalFilePaths"> collection redirects </param>
/// <param name="Diagnostics"> build diagnostics </param>
[MessagePackObject(keyAsPropertyName: true)]
public sealed record TemporarySourceBuildResult(
    TemporarySourceBuildStatus Status,
    string Message,
    TemporaryLayoutApplyRequest Layout,
    TemporaryCollectionsApplyRequest Collections,
    IReadOnlyList<string> LocalFilePaths,
    IReadOnlyList<TemporarySourceBuildDiagnostic> Diagnostics);

