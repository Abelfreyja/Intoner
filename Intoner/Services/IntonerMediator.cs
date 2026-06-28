using Microsoft.Extensions.Logging;

namespace Intoner.Services;

/// <summary> mediates typed in process messages between Intoner services (copied from mare/lightless, thank you) </summary>
internal sealed class IntonerMediator(ILogger<IntonerMediator> logger) : IIntonerMediator, IDisposable
{
    private static readonly TimeSpan ErrorRepeatDelay = TimeSpan.FromSeconds(10);

    private readonly Lock _lock = new();
    private readonly Dictionary<Type, List<Subscription>> _subscriptions = [];
    private readonly Dictionary<Type, Subscription[]> _snapshots = [];
    private readonly Dictionary<object, List<Subscription>> _subscriptionsByOwner = new(ReferenceEqualityComparer.Instance);
    private long _nextOrder;
    private bool _disposed;

    public IDisposable Subscribe<T>(object owner, Action<T> handler, int priority = 0)
        where T : notnull
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(handler);

        Type messageType = typeof(T);
        Subscription subscription = new(
            this,
            messageType,
            owner,
            handler,
            static (callback, message) => ((Action<T>)callback)((T)message),
            priority,
            Interlocked.Increment(ref _nextOrder));

        lock (_lock)
        {
            ThrowIfDisposed();

            List<Subscription> subscriptions = GetSubscriptions(messageType);
            if (HasSubscription(subscriptions, owner, handler))
            {
                throw new InvalidOperationException($"{owner.GetType().Name} is already subscribed to {messageType.Name}");
            }

            subscriptions.Add(subscription);
            UpdateSnapshot(messageType, subscriptions);
            AddOwnerSubscription(owner, subscription);
        }

        return subscription;
    }

    public void Publish<T>(T message)
        where T : notnull
    {
        ArgumentNullException.ThrowIfNull(message);

        Subscription[] snapshot;
        lock (_lock)
        {
            ThrowIfDisposed();

            if (!_snapshots.TryGetValue(typeof(T), out snapshot!))
            {
                return;
            }
        }

        foreach (Subscription subscription in snapshot)
        {
            Dispatch(subscription, message);
        }
    }

    public void UnsubscribeAll(object owner)
    {
        ArgumentNullException.ThrowIfNull(owner);

        lock (_lock)
        {
            ThrowIfDisposed();

            if (!_subscriptionsByOwner.Remove(owner, out List<Subscription>? ownerSubscriptions))
            {
                return;
            }

            foreach (Subscription subscription in ownerSubscriptions)
            {
                RemoveSubscription(subscription);
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            RemoveAllSubscriptions();
        }
    }

    private void RemoveAllSubscriptions()
    {
        foreach (List<Subscription> subscriptions in _subscriptions.Values)
        {
            foreach (Subscription subscription in subscriptions)
            {
                subscription.MarkRemoved();
            }
        }

        _subscriptions.Clear();
        _snapshots.Clear();
        _subscriptionsByOwner.Clear();
    }

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(_disposed, this);

    private List<Subscription> GetSubscriptions(Type messageType)
    {
        if (!_subscriptions.TryGetValue(messageType, out List<Subscription>? subscriptions))
        {
            subscriptions = [];
            _subscriptions[messageType] = subscriptions;
        }

        return subscriptions;
    }

    private void AddOwnerSubscription(object owner, Subscription subscription)
    {
        if (!_subscriptionsByOwner.TryGetValue(owner, out List<Subscription>? ownerSubscriptions))
        {
            ownerSubscriptions = [];
            _subscriptionsByOwner[owner] = ownerSubscriptions;
        }

        ownerSubscriptions.Add(subscription);
    }

    private static bool HasSubscription(List<Subscription> subscriptions, object owner, object handler)
    {
        foreach (Subscription subscription in subscriptions)
        {
            if (ReferenceEquals(subscription.Owner, owner) && Equals(subscription.Handler, handler))
            {
                return true;
            }
        }

        return false;
    }

    private void Unsubscribe(Subscription subscription)
    {
        lock (_lock)
        {
            if (_subscriptionsByOwner.TryGetValue(subscription.Owner, out List<Subscription>? ownerSubscriptions))
            {
                ownerSubscriptions.Remove(subscription);
                if (ownerSubscriptions.Count == 0)
                {
                    _subscriptionsByOwner.Remove(subscription.Owner);
                }
            }

            RemoveSubscription(subscription);
        }
    }

    private void RemoveSubscription(Subscription subscription)
    {
        if (!_subscriptions.TryGetValue(subscription.MessageType, out List<Subscription>? subscriptions))
        {
            return;
        }

        if (!subscriptions.Remove(subscription))
        {
            return;
        }

        subscription.MarkRemoved();
        if (subscriptions.Count == 0)
        {
            _subscriptions.Remove(subscription.MessageType);
            _snapshots.Remove(subscription.MessageType);
            return;
        }

        _snapshots[subscription.MessageType] = [.. subscriptions];
    }

    private void UpdateSnapshot(Type messageType, List<Subscription> subscriptions)
    {
        SortSubscriptions(subscriptions);
        _snapshots[messageType] = [.. subscriptions];
    }

    private void Dispatch(Subscription subscription, object message)
    {
        if (subscription.IsRemoved)
        {
            return;
        }

        try
        {
            subscription.Invoke(message);
        }
        catch (Exception ex)
        {
            LogSubscriberError(subscription, ex);
        }
    }

    private void LogSubscriberError(Subscription subscription, Exception ex)
    {
        if (!subscription.ShouldLogError(ErrorRepeatDelay))
        {
            return;
        }

        logger.LogError(
            ex,
            "Error publishing {MessageType} to {SubscriberType}",
            subscription.MessageType.Name,
            subscription.Owner.GetType().Name);
    }

    private static void SortSubscriptions(List<Subscription> subscriptions)
    {
        subscriptions.Sort(static (left, right) =>
        {
            int priority = left.Priority.CompareTo(right.Priority);
            return priority != 0 ? priority : left.Order.CompareTo(right.Order);
        });
    }

    private sealed class Subscription(
        IntonerMediator mediator,
        Type messageType,
        object owner,
        object handler,
        Action<object, object> invoker,
        int priority,
        long order) : IDisposable
    {
        private int _disposed;
        private readonly Lock _errorLock = new();

        public Type MessageType { get; } = messageType;
        public object Owner { get; } = owner;
        public object Handler { get; } = handler;
        public int Priority { get; } = priority;
        public long Order { get; } = order;
        public bool IsRemoved => Volatile.Read(ref _disposed) != 0;

        public void Invoke(object message)
            => invoker(Handler, message);

        public bool ShouldLogError(TimeSpan repeatDelay)
        {
            lock (_errorLock)
            {
                DateTime utcNow = DateTime.UtcNow;
                if (LastErrorUtc.Add(repeatDelay) > utcNow)
                {
                    return false;
                }

                LastErrorUtc = utcNow;
                return true;
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                mediator.Unsubscribe(this);
            }
        }

        public void MarkRemoved()
            => Interlocked.Exchange(ref _disposed, 1);

        private DateTime LastErrorUtc { get; set; } = DateTime.MinValue;
    }
}

/// <summary> mediates typed messages between local subscribers </summary>
internal interface IIntonerMediator
{
    /// <summary> subscribes an owner to a message type and returns a handle that removes the subscription </summary>
    /// <param name="owner">object that owns the subscription lifetime</param>
    /// <param name="handler">callback invoked when the message is published</param>
    /// <param name="priority">lower values run earlier, equal priorities keep subscription order</param>
    /// <returns>subscription handle that can be disposed to unsubscribe</returns>
    IDisposable Subscribe<T>(object owner, Action<T> handler, int priority = 0)
        where T : notnull;

    /// <summary> publishes a message to current subscribers synchronously on the caller thread </summary>
    /// <param name="message">message instance to publish</param>
    void Publish<T>(T message)
        where T : notnull;

    /// <summary> removes every subscription owned by the given object </summary>
    /// <param name="owner">owner passed to subscribe</param>
    void UnsubscribeAll(object owner);
}
