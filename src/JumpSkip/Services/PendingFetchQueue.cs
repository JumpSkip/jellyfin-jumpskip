namespace JumpSkip.Services;

internal sealed class PendingFetchQueue(int capacity)
{
    private readonly object _sync = new();
    private readonly Dictionary<Guid, DateTimeOffset> _items = new();

    public void Enqueue(Guid itemId, DateTimeOffset due)
    {
        lock (_sync)
        {
            if (_items.TryGetValue(itemId, out var previous))
            {
                // Playback can bring an existing item forward even when the queue is full.
                if (due < previous) _items[itemId] = due;
            }
            else if (_items.Count < capacity) _items.Add(itemId, due);
        }
    }

    public void Remove(Guid itemId)
    {
        lock (_sync) _items.Remove(itemId);
    }

    public Guid[] TakeDue(DateTimeOffset now, int count)
    {
        lock (_sync)
        {
            var ready = _items.Where(item => item.Value <= now).OrderBy(item => item.Value)
                .Take(count).Select(item => item.Key).ToArray();
            // Claim the batch atomically so workers cannot remove a newer event for the same item.
            foreach (var itemId in ready) _items.Remove(itemId);
            return ready;
        }
    }
}
