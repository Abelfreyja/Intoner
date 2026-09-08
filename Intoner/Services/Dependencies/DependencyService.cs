using System.Runtime.InteropServices;

namespace Intoner.Services.Dependencies;

internal enum DependencyRequirement
{
    Optional,
    Required,
}

internal enum DependencyState
{
    Unknown,
    Missing,
    Disabled,
    FeatureDisabled,
    Incompatible,
    NotReady,
    Available,
    Error,
}

[StructLayout(LayoutKind.Auto)]
internal readonly record struct DependencyApiVersion(int Major, int Minor)
{
    public override string ToString()
        => $"{Major}.{Minor}";
}

internal sealed record DependencyDefinition(
    string Id,
    string Name,
    string Description,
    DependencyRequirement Requirement,
    string Keywords);

internal sealed record DependencyStatus(
    DependencyDefinition Definition,
    DependencyState State,
    string Message,
    Version? PluginVersion = null,
    DependencyApiVersion? ApiVersion = null,
    DependencyApiVersion? RequiredApiVersion = null)
{
    public bool IsAvailable
        => State == DependencyState.Available;
}

[StructLayout(LayoutKind.Auto)]
internal readonly record struct DependencySummary(
    int TotalCount,
    int AvailableCount,
    int RequiredUnavailableCount,
    int OptionalUnavailableCount)
{
    public int UnavailableCount
        => TotalCount - AvailableCount;
}

internal sealed record DependencyRegistration(IDependency Dependency);

/// <summary> exposes one external dependency and its current status </summary>
internal interface IDependency
{
    /// <summary> raised after the dependency status changes </summary>
    event Action? StatusChanged;

    /// <summary> gets the dependency metadata </summary>
    DependencyDefinition Definition { get; }

    /// <summary> gets the latest dependency status snapshot </summary>
    DependencyStatus Status { get; }

    /// <summary> refreshes the dependency status from its external source </summary>
    void Refresh();
}

/// <summary> provides immutable status snapshots for external feature dependencies </summary>
internal interface IDependencyService
{
    /// <summary> raised after any dependency status changes </summary>
    event Action? Changed;

    /// <summary> gets all dependency status snapshots in display order </summary>
    IReadOnlyList<DependencyStatus> Statuses { get; }

    /// <summary> gets the current aggregate dependency state </summary>
    DependencySummary Summary { get; }

    /// <summary> gets a dependency status by its stable identifier </summary>
    /// <param name="id">the dependency identifier</param>
    /// <returns>the current dependency status</returns>
    DependencyStatus GetStatus(string id);

    /// <summary> attempts to get a dependency status by its stable identifier </summary>
    /// <param name="id">the dependency identifier</param>
    /// <param name="status">the current dependency status when found</param>
    /// <returns>true when the dependency is registered</returns>
    bool TryGetStatus(string id, out DependencyStatus status);

    /// <summary> refreshes every registered dependency </summary>
    void Refresh();
}

internal sealed class DependencyService : IDependencyService, IDisposable
{
    private sealed record Snapshot(
        IReadOnlyList<DependencyStatus> Statuses,
        DependencySummary Summary);

    private readonly IReadOnlyList<IDependency> _dependencies;
    private Snapshot _snapshot = new([], default);

    public DependencyService(IEnumerable<DependencyRegistration> registrations)
    {
        IDependency[] dependencies = registrations
            .Select(static registration => registration.Dependency)
            .ToArray();
        ValidateDependencies(dependencies);
        _dependencies = dependencies;
        foreach (IDependency dependency in dependencies)
        {
            dependency.StatusChanged += HandleStatusChanged;
        }

        UpdateSnapshot();
    }

    public event Action? Changed;

    public IReadOnlyList<DependencyStatus> Statuses
        => Volatile.Read(ref _snapshot).Statuses;

    public DependencySummary Summary
        => Volatile.Read(ref _snapshot).Summary;

    public DependencyStatus GetStatus(string id)
        => TryGetStatus(id, out DependencyStatus status)
            ? status
            : throw new KeyNotFoundException($"unknown dependency '{id}'");

    public bool TryGetStatus(string id, out DependencyStatus status)
    {
        Snapshot snapshot = Volatile.Read(ref _snapshot);
        DependencyStatus? match = snapshot.Statuses.FirstOrDefault(candidate =>
            string.Equals(candidate.Definition.Id, id, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
        {
            status = match;
            return true;
        }

        status = default!;
        return false;
    }

    public void Refresh()
    {
        foreach (IDependency dependency in _dependencies)
        {
            dependency.Refresh();
        }

        UpdateSnapshot();
    }

    public void Dispose()
    {
        foreach (IDependency dependency in _dependencies)
        {
            dependency.StatusChanged -= HandleStatusChanged;
        }
    }

    private static void ValidateDependencies(IReadOnlyList<IDependency> dependencies)
    {
        HashSet<string> ids = new(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < dependencies.Count; ++index)
        {
            DependencyDefinition definition = dependencies[index].Definition;
            if (string.IsNullOrWhiteSpace(definition.Id))
            {
                throw new InvalidOperationException("dependency identifiers cannot be empty");
            }

            if (!ids.Add(definition.Id))
            {
                throw new InvalidOperationException($"duplicate dependency '{definition.Id}'");
            }
        }
    }

    private void HandleStatusChanged()
    {
        UpdateSnapshot();
        Changed?.Invoke();
    }

    private void UpdateSnapshot()
    {
        DependencyStatus[] statuses = new DependencyStatus[_dependencies.Count];
        int availableCount = 0;
        int requiredUnavailableCount = 0;
        for (int index = 0; index < _dependencies.Count; ++index)
        {
            DependencyStatus status = _dependencies[index].Status;
            statuses[index] = status;
            if (status.IsAvailable)
            {
                ++availableCount;
            }
            else if (status.Definition.Requirement == DependencyRequirement.Required)
            {
                ++requiredUnavailableCount;
            }
        }

        Volatile.Write(
            ref _snapshot,
            new Snapshot(
                Array.AsReadOnly(statuses),
                new DependencySummary(
                    statuses.Length,
                    availableCount,
                    requiredUnavailableCount,
                    statuses.Length - availableCount - requiredUnavailableCount)));
    }
}
