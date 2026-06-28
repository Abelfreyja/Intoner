using Intoner.Objects.Models;

namespace Intoner.Objects.Runtime;

internal sealed class PlacementEvaluationFactory(PlacementFixService fixService)
{
    private static readonly IReadOnlyList<PlacementValidationIssue> NoIssues = [];
    private static readonly IReadOnlyList<PlacementFixProposal> NoFixes = [];

    public PlacementEvaluation Valid(Guid objectId)
        => new(objectId, PlacementValidationStatus.Valid, string.Empty, NoIssues, NoFixes);

    public PlacementEvaluation Unknown(Guid objectId, PlacementIssueCode issueCode, string message)
        => new(objectId, PlacementValidationStatus.Unknown, message, CreateIssues(issueCode, message), NoFixes);

    public PlacementEvaluation Unknown(ObjectSnapshot snapshot, PlacementIssueCode issueCode, string message)
        => new(
            snapshot.Id,
            PlacementValidationStatus.Unknown,
            message,
            CreateIssues(issueCode, message),
            fixService.CreateFixes(snapshot, issueCode));

    public PlacementEvaluation Invalid(ObjectSnapshot snapshot, PlacementIssueCode issueCode, string message)
        => new(
            snapshot.Id,
            PlacementValidationStatus.Invalid,
            message,
            CreateIssues(issueCode, message),
            fixService.CreateFixes(snapshot, issueCode));

    private static IReadOnlyList<PlacementValidationIssue> CreateIssues(PlacementIssueCode issueCode, string message)
        => issueCode == PlacementIssueCode.None
            ? NoIssues
            : [new PlacementValidationIssue(issueCode, message)];
}

