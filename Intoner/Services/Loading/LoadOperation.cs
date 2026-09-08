namespace Intoner.Services.Loading;

internal enum LoadPhase
{
    Waiting,
    Running,
    Ready,
    Failed,
}

/// <summary> exposes the lifecycle shared by independently loadable services </summary>
internal interface ILoadOperation
{
    /// <summary> gets the current load status with progress normalized between zero and one </summary>
    LoadStatus Status { get; }

    /// <summary> starts loading if it has not already started </summary>
    void EnsureLoaded();
}

/// <summary> describes the visible state of one load operation </summary>
internal readonly record struct LoadStatus(LoadPhase Phase, string Message, double Progress)
{
    public bool IsReady
        => Phase == LoadPhase.Ready;

    public bool HasFailed
        => Phase == LoadPhase.Failed;

    public LoadStatus WithProgress(double progress)
    {
        if (!double.IsFinite(progress))
        {
            throw new ArgumentOutOfRangeException(nameof(progress), "load progress must be finite");
        }

        return this with { Progress = Math.Clamp(progress, 0d, 1d) };
    }
}

/// <summary> starts and combines any number of weighted load operations without owning their lifetime </summary>
internal sealed class LoadGroup : ILoadOperation
{
    private readonly (ILoadOperation Operation, double Weight)[] _operations;
    private readonly double _totalWeight;

    public LoadGroup(params (ILoadOperation Operation, double Weight)[] operations)
    {
        ArgumentNullException.ThrowIfNull(operations);
        if (operations.Length == 0)
        {
            throw new ArgumentException("load group requires at least one operation", nameof(operations));
        }

        _operations = ((ILoadOperation Operation, double Weight)[])operations.Clone();
        foreach ((ILoadOperation operation, double weight) in _operations)
        {
            if (operation is null)
            {
                throw new ArgumentException("load group operations cannot contain null", nameof(operations));
            }

            ValidateWeight(weight);
            _totalWeight += weight;
            if (!double.IsFinite(_totalWeight))
            {
                throw new ArgumentOutOfRangeException(nameof(operations), "combined load weight must be finite");
            }
        }
    }

    public LoadStatus Status
    {
        get
        {
            double weightedProgress = 0d;
            LoadStatus firstIncomplete = default;
            LoadStatus firstFailure = default;
            LoadStatus last = default;
            bool hasIncomplete = false;
            bool hasFailure = false;

            foreach ((ILoadOperation operation, double weight) in _operations)
            {
                LoadStatus status = operation.Status;
                if (!double.IsFinite(status.Progress) || status.Progress < 0d || status.Progress > 1d)
                {
                    throw new InvalidOperationException("load operation reported progress outside the normalized range");
                }

                last = status;
                weightedProgress += status.Progress * weight;
                if (!hasFailure && status.HasFailed)
                {
                    firstFailure = status;
                    hasFailure = true;
                }

                if (!hasIncomplete && !status.IsReady)
                {
                    firstIncomplete = status;
                    hasIncomplete = true;
                }
            }

            LoadStatus current = last;
            if (hasFailure)
            {
                current = firstFailure;
            }
            else if (hasIncomplete)
            {
                current = firstIncomplete;
            }

            return current.WithProgress(weightedProgress / _totalWeight);
        }
    }

    public void EnsureLoaded()
    {
        foreach ((ILoadOperation operation, _) in _operations)
        {
            operation.EnsureLoaded();
        }
    }

    private static void ValidateWeight(double weight)
    {
        if (!double.IsFinite(weight) || weight <= 0d)
        {
            throw new ArgumentOutOfRangeException(nameof(weight), "load weight must be positive and finite");
        }
    }
}
