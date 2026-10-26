namespace Intoner.Scene;

internal enum SceneHistoryOutcome
{
    Success,
    Conflict,
    Failed,
    RecoveryRequired,
}

/// <summary> reports replay success or a reason to leave history unchanged </summary>
internal readonly record struct SceneHistoryResult(SceneHistoryOutcome Outcome, string Reason)
{
    public string Message { get; init; } = Reason;
    public bool IsSuccess => Outcome == SceneHistoryOutcome.Success;
    public static SceneHistoryResult Success => new(SceneHistoryOutcome.Success, string.Empty);
    public static SceneHistoryResult Conflict(string message) => new(SceneHistoryOutcome.Conflict, message);
    public static SceneHistoryResult Failed(string message) => new(SceneHistoryOutcome.Failed, message);
    public static SceneHistoryResult RecoveryRequired(string message) => new(SceneHistoryOutcome.RecoveryRequired, message);

    public SceneHistoryResult WithActionContext(string title, bool apply)
    {
        if (IsSuccess)
        {
            return this;
        }

        string operation = apply ? "redo" : "undo";
        return this with { Message = $"Cannot {operation} \"{title}\": {Reason}" };
    }

    public static SceneHistoryResult FromMutation(SceneMutationStatus status, string message)
        => status switch
        {
            SceneMutationStatus.Applied or SceneMutationStatus.AppliedWithRuntimeFailure => Success,
            SceneMutationStatus.RecoveryRequired => RecoveryRequired(message),
            _ => Failed(message),
        };
}
