using Microsoft.EntityFrameworkCore;

public class EfOrderRepository(OrdersDbContext db) : IOrderRepository
{
    public async Task<bool> TryAddAsync(Order order)
    {
        db.Orders.Add(order);
        try
        {
            await db.SaveChangesAsync();
            return true;
        }
        catch (DbUpdateException e) when (e.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation })
        {
            db.Entry(order).State = EntityState.Detached;   // not stored: do not retry it on the next save
            return false;
        }
    }

    // A single conditional UPDATE: never overwrites a final status, whoever completes first wins.
    public async Task<bool> CompletePendingAsync(Guid id, string status) =>
        await db.Orders.Where(o => o.Id == id && o.Status == OrderStatus.Pending)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, status)) == 1;

    public Task<Order?> FindAsync(Guid id, string userId) =>
        db.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == id && o.UserId == userId);

    public Task<List<Order>> ListAsync(string userId) =>
        db.Orders.AsNoTracking().Where(o => o.UserId == userId).OrderBy(o => o.CreatedAt).ToListAsync();

    public Task<Order?> FindByIdempotencyKeyAsync(string userId, string idempotencyKey) =>
        db.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.UserId == userId && o.IdempotencyKey == idempotencyKey);
}
