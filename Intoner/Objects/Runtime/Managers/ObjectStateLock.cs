namespace Intoner.Objects.Runtime;

/// <summary>
/// Shares one synchronization lock across scene state owners in the current scope.
/// </summary>
internal sealed class ObjectStateLock
{
    public Lock Value { get; } = new();
}

