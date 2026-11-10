using Microsoft.Extensions.Logging;

public static class OrderStatus
{
    /// <summary>Stored before the charge: a charge may be in progress or may have happened.</summary>
    public const string Pending = "Pending";
    public const string Paid = "Paid";
    public const string PaymentFailed = "PaymentFailed";
}

public enum PlaceOutcome
{
    Created,
    /// <summary>Same key, same order: the first order, not charged again.</summary>
    Replayed,
    /// <summary>Same key, different product or quantity: rejected, nothing charged or stored.</summary>
    KeyUsedForDifferentOrder,
    /// <summary>Charged (or charge attempted) but the result could not be saved: the order is still
    /// Pending. A retry with the same key settles it without charging twice.</summary>
    Pending,
}

/// <summary>Order is the new order, or for a reused key the order that key already belongs to.</summary>
public record PlacedOrder(Order Order, PlaceOutcome Outcome)
{
    public bool Replayed => Outcome == PlaceOutcome.Replayed;
}

/// <summary>
/// Places orders so that a paid order is never lost and a retry never charges twice:
/// 1. the order is stored as Pending (with its key) before anything is charged; for a key, the
///    database's unique (user, key) index decides which request owns it, also across instances;
/// 2. the payment is charged with the order id as reference (the provider's idempotency key,
///    FAKED for this example, see IPaymentClient);
/// 3. the order is completed to Paid or PaymentFailed, only if it is still Pending.
/// If step 1 fails nothing is charged. If step 3 fails the order stays Pending; a retry with the
/// same key charges the same reference again, which the provider answers with its first result,
/// and completes the order.
/// </summary>
public class OrderService(IPaymentClient payment, IOrderRepository orders, TimeProvider clock, IdempotencyLocks locks,
    ILogger<OrderService> log)
{
    public async Task<PlacedOrder> PlaceOrderAsync(CreateOrderRequest req, string userId = "anonymous", string? idempotencyKey = null)
    {
        if (idempotencyKey is null)
            return await ReserveAndChargeAsync(req, userId, null);

        // Fast path on one instance: same-key requests wait for each other here.
        using var _ = await locks.AcquireAsync($"{userId}\n{idempotencyKey}");
        var existing = await orders.FindByIdempotencyKeyAsync(userId, idempotencyKey);
        return existing is not null
            ? await ResumeAsync(existing, req)
            : await ReserveAndChargeAsync(req, userId, idempotencyKey);
    }

    private async Task<PlacedOrder> ReserveAndChargeAsync(CreateOrderRequest req, string userId, string? idempotencyKey)
    {
        var order = new Order(Guid.NewGuid(), req.ProductName, req.Quantity, OrderStatus.Pending,
            userId, clock.GetUtcNow(), idempotencyKey);
        if (!await orders.TryAddAsync(order))
        {
            // Another instance reserved this key first: it charges, we do not.
            var owner = await orders.FindByIdempotencyKeyAsync(userId, idempotencyKey!)
                ?? throw new InvalidOperationException("Idempotency key reserved but its order was not found.");
            return await ResumeAsync(owner, req);
        }
        return await ChargeAndCompleteAsync(order, PlaceOutcome.Created);
    }

    private async Task<PlacedOrder> ResumeAsync(Order existing, CreateOrderRequest req)
    {
        if (existing.ProductName != req.ProductName || existing.Quantity != req.Quantity)
            return new PlacedOrder(existing, PlaceOutcome.KeyUsedForDifferentOrder);
        if (existing.Status != OrderStatus.Pending)
            return new PlacedOrder(existing, PlaceOutcome.Replayed);
        // Pending: still in progress elsewhere, or its result was never saved (failed update, crash).
        // Charging the same reference is safe: the provider answers with its first result.
        var settled = await ChargeAndCompleteAsync(existing, PlaceOutcome.Replayed);
        return settled;
    }

    private async Task<PlacedOrder> ChargeAndCompleteAsync(Order pending, PlaceOutcome outcome)
    {
        var paid = await payment.ChargeAsync(pending.Quantity * 10m, pending.Id);
        var status = paid ? OrderStatus.Paid : OrderStatus.PaymentFailed;
        try
        {
            if (!await orders.CompletePendingAsync(pending.Id, status))
            {
                // Someone else completed it first (e.g. another instance): theirs is the result.
                var current = await orders.FindAsync(pending.Id, pending.UserId);
                return new PlacedOrder(current ?? pending, outcome);
            }
        }
        catch (Exception e)
        {
            log.LogError(e, "Order {OrderId}: charge {Result} but its status could not be saved; it stays Pending until a retry",
                pending.Id, paid ? "approved" : "not approved");
            return new PlacedOrder(pending, PlaceOutcome.Pending);
        }
        return new PlacedOrder(pending with { Status = status }, outcome);
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
