namespace Intoner.Objects.Runtime;

/// <summary>
/// Shares one synchronization lock across the object runtime state users in the current scope.
/// </summary>
internal sealed class ObjectStateLock
{
    public Lock Value { get; } = new();
}

