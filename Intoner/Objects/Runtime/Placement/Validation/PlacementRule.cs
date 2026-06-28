using Intoner.Objects.Models;

namespace Intoner.Objects.Runtime;

internal interface IPlacementRule
{
    /// <summary> evaluates one focused placement rule and returns true when the rule produced a terminal result </summary>
    /// <param name="context">the placement validation context for the current scene</param>
    /// <param name="snapshot">the furniture snapshot being evaluated</param>
    /// <param name="boundsSnapshot">the current bounds snapshot for the furniture</param>
    /// <param name="evaluation">the terminal evaluation produced by the rule</param>
    /// <returns>true when validation should stop for this object</returns>
    bool TryEvaluate(
        PlacementValidationContext context,
        ObjectSnapshot snapshot,
        ObjectBoundsSnapshot boundsSnapshot,
        out PlacementEvaluation evaluation);
}

internal sealed class PlacementRuleRunner(
    IEnumerable<IPlacementRule> rules,
    PlacementEvaluationFactory evaluationFactory)
{
    public PlacementEvaluation Evaluate(
        PlacementValidationContext context,
        ObjectSnapshot snapshot,
        ObjectBoundsSnapshot boundsSnapshot)
    {
        foreach (IPlacementRule rule in rules)
        {
            if (rule.TryEvaluate(context, snapshot, boundsSnapshot, out PlacementEvaluation evaluation))
            {
                return evaluation;
            }
        }

        return evaluationFactory.Valid(snapshot.Id);
    }
}

