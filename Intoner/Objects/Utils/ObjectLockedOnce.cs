namespace Intoner.Objects.Utils;

internal sealed class ObjectLockedOnce
{
    private readonly Lock _lock = new();
    private bool _completed;

    public void Execute(Action action)
        => _ = TryExecute(
            () =>
            {
                action();
                return true;
            });

    public bool TryExecute(Func<bool> action, Func<bool>? canExecute = null)
    {
        lock (_lock)
        {
            if (canExecute is not null && !canExecute())
            {
                return false;
            }

            if (_completed)
            {
                return true;
            }

            if (!action())
            {
                return false;
            }

            _completed = true;
            return true;
        }
    }
}

