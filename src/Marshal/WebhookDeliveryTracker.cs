namespace Marshal;

/// <summary>Bounded in-memory record of authenticated webhook deliveries used to suppress provider retries</summary>
public sealed class WebhookDeliveryTracker
{
    /// <summary>Maximum delivery IDs retained at one time</summary>
    public const int MaxTrackedDeliveries = 4096;

    /// <summary>Hours an accepted delivery ID remains a duplicate</summary>
    public const int RetentionHours = 24;

    private static readonly TimeSpan Retention = TimeSpan.FromHours(RetentionHours);

    private readonly Dictionary<string, LinkedListNode<DeliveryEntry>> _deliveries = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private readonly LinkedList<DeliveryEntry> _oldestFirst = [];

    /// <summary>Claims a provider delivery ID at the current UTC time; false means the same authenticated delivery was already claimed recently</summary>
    public bool TryClaim(WebhookProvider provider, string deliveryId) => TryClaimCore(provider, deliveryId, null);

    /// <summary>Claims a provider delivery ID at a supplied time, exposed for deterministic expiry tests</summary>
    public bool TryClaim(WebhookProvider provider, string deliveryId, DateTimeOffset receivedAt) => TryClaimCore(provider, deliveryId, receivedAt);

    /// <summary>Forgets a claimed delivery so the provider may retry after an enqueue failure</summary>
    public void Forget(WebhookProvider provider, string deliveryId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deliveryId);
        string key = BuildKey(provider, deliveryId);

        lock (_gate)
        {
            if (_deliveries.Remove(key, out LinkedListNode<DeliveryEntry>? node))
            {
                _oldestFirst.Remove(node);
            }
        }
    }

    private bool TryClaimCore(WebhookProvider provider, string deliveryId, DateTimeOffset? receivedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deliveryId);
        string key = BuildKey(provider, deliveryId);

        lock (_gate)
        {
            DateTimeOffset timestamp = receivedAt ?? DateTimeOffset.UtcNow;
            PruneExpired(timestamp);
            if (_deliveries.ContainsKey(key))
            {
                return false;
            }

            while (_deliveries.Count >= MaxTrackedDeliveries)
            {
                RemoveOldest();
            }

            var entry = new DeliveryEntry(key, timestamp);
            LinkedListNode<DeliveryEntry> node = _oldestFirst.AddLast(entry);
            _deliveries.Add(key, node);
            return true;
        }
    }

    private static string BuildKey(WebhookProvider provider, string deliveryId) => $"{provider}:{deliveryId.Trim()}";

    private void PruneExpired(DateTimeOffset now)
    {
        DateTimeOffset cutoff = now - Retention;
        while (_oldestFirst.First is { } node && node.Value.ReceivedAt <= cutoff)
        {
            RemoveOldest();
        }
    }

    private void RemoveOldest()
    {
        LinkedListNode<DeliveryEntry>? node = _oldestFirst.First;
        if (node is null)
        {
            return;
        }

        _oldestFirst.RemoveFirst();
        _deliveries.Remove(node.Value.Key);
    }

    private sealed record DeliveryEntry(string Key, DateTimeOffset ReceivedAt);
}
