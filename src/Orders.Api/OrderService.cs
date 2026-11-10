public enum PlaceOutcome
{
    Created,
    /// <summary>Same key, same order: the first order, not charged again.</summary>
    Replayed,
    /// <summary>Same key, different product or quantity: rejected, nothing charged or stored.</summary>
    KeyUsedForDifferentOrder,
}

/// <summary>Order is the new order, or for a reused key the order that key already belongs to.</summary>
public record PlacedOrder(Order Order, PlaceOutcome Outcome)
{
    public bool Replayed => Outcome == PlaceOutcome.Replayed;
}

public class OrderService(IPaymentClient payment, IOrderRepository orders, TimeProvider clock, IdempotencyLocks locks)
{
    /// <summary>
    /// Places an order and charges for it. With an idempotency key, repeating the request (double
    /// click, client retry) returns the first order instead of charging again; concurrent requests
    /// with the same key are serialized so only one of them charges. Reusing a key for a different
    /// product or quantity is rejected instead of silently returning the first order.
    /// </summary>
    public async Task<PlacedOrder> PlaceOrderAsync(CreateOrderRequest req, string userId = "anonymous", string? idempotencyKey = null)
    {
        if (idempotencyKey is null)
            return new PlacedOrder(await ChargeAndStoreAsync(req, userId, null), PlaceOutcome.Created);

        using var _ = await locks.AcquireAsync($"{userId}\n{idempotencyKey}");
        var existing = await orders.FindByIdempotencyKeyAsync(userId, idempotencyKey);
        if (existing is not null)
            return new PlacedOrder(existing,
                existing.ProductName == req.ProductName && existing.Quantity == req.Quantity
                    ? PlaceOutcome.Replayed
                    : PlaceOutcome.KeyUsedForDifferentOrder);

        return new PlacedOrder(await ChargeAndStoreAsync(req, userId, idempotencyKey), PlaceOutcome.Created);
    }

    private async Task<Order> ChargeAndStoreAsync(CreateOrderRequest req, string userId, string? idempotencyKey)
    {
        var paid = await payment.ChargeAsync(req.Quantity * 10m);
        var order = new Order(Guid.NewGuid(), req.ProductName, req.Quantity,
            paid ? "Paid" : "PaymentFailed", userId, clock.GetUtcNow(), idempotencyKey);
        await orders.AddAsync(order);
        return order;
    }
}

/// <summary>
/// One async lock per key, removed again when nobody holds or waits for it. In-process only: with
/// several API replicas the database's unique (UserId, IdempotencyKey) index is the backstop
/// (same single-replica limitation as the session store, see CLAUDE.md).
/// </summary>
public sealed class IdempotencyLocks
{
    private readonly Dictionary<string, (SemaphoreSlim Gate, int Users)> _locks = new();

    public int Count { get { lock (_locks) return _locks.Count; } }

    /// <summary>How many requests hold or wait for this key (lets tests wait deterministically).</summary>
    public int UsersOf(string key) { lock (_locks) return _locks.TryGetValue(key, out var e) ? e.Users : 0; }

    public async Task<IDisposable> AcquireAsync(string key)
    {
        SemaphoreSlim gate;
        lock (_locks)
        {
            (SemaphoreSlim Gate, int Users) entry = _locks.TryGetValue(key, out var e) ? e : (new SemaphoreSlim(1, 1), 0);
            _locks[key] = (entry.Gate, entry.Users + 1);
            gate = entry.Gate;
        }
        await gate.WaitAsync();
        return new Release(this, key, gate);
    }

    private sealed class Release(IdempotencyLocks owner, string key, SemaphoreSlim gate) : IDisposable
    {
        public void Dispose()
        {
            gate.Release();
            lock (owner._locks)
            {
                var (g, users) = owner._locks[key];
                if (users == 1) owner._locks.Remove(key);
                else owner._locks[key] = (g, users - 1);
            }
        }
    }
}
