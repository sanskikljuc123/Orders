using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

public interface IOrderRepository
{
    /// <summary>Stores a new order. False (and nothing stored) if the same user already has an
    /// order with the same idempotency key; the database decides, also across instances.</summary>
    Task<bool> TryAddAsync(Order order);
    /// <summary>Sets the final status only if the order is still Pending; false otherwise.</summary>
    Task<bool> CompletePendingAsync(Guid id, string status);
    Task<Order?> FindAsync(Guid id, string userId);
    Task<List<Order>> ListAsync(string userId);
    Task<Order?> FindByIdempotencyKeyAsync(string userId, string idempotencyKey);
}

// Used when no database is configured (unit/integration tests without a DB).
public class OrderStore : IOrderRepository
{
    private readonly ConcurrentDictionary<Guid, Order> _orders = new();

    public void Add(Order o) => _orders[o.Id] = o;
    public Order? Find(Guid id) => _orders.GetValueOrDefault(id);

    private readonly object _keys = new();

    public Task<bool> TryAddAsync(Order o)
    {
        lock (_keys)   // same rule as the database's unique (user, key) index
        {
            if (o.IdempotencyKey is not null
                && _orders.Values.Any(x => x.UserId == o.UserId && x.IdempotencyKey == o.IdempotencyKey))
                return Task.FromResult(false);
            Add(o);
            return Task.FromResult(true);
        }
    }

    public Task<bool> CompletePendingAsync(Guid id, string status) =>
        Task.FromResult(_orders.TryGetValue(id, out var o) && o.Status == OrderStatus.Pending
            && _orders.TryUpdate(id, o with { Status = status }, o));

    public Task<Order?> FindAsync(Guid id, string userId)
    {
        var o = Find(id);
        return Task.FromResult(o is not null && o.UserId == userId ? o : null);
    }

    public Task<List<Order>> ListAsync(string userId) =>
        Task.FromResult(_orders.Values.Where(o => o.UserId == userId).OrderBy(o => o.CreatedAt).ToList());

    public Task<Order?> FindByIdempotencyKeyAsync(string userId, string idempotencyKey) =>
        Task.FromResult(_orders.Values.FirstOrDefault(o => o.UserId == userId && o.IdempotencyKey == idempotencyKey));
}

public class OrdersDbContext(DbContextOptions<OrdersDbContext> options) : DbContext(options)
{
    public DbSet<Order> Orders => Set<Order>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Order>(e =>
        {
            e.HasKey(o => o.Id);
            e.Property(o => o.ProductName).HasMaxLength(50).IsRequired();
            e.Property(o => o.Status).HasMaxLength(20).IsRequired();
            e.Property(o => o.UserId).HasMaxLength(100).IsRequired();
            e.HasIndex(o => o.UserId);
            e.Property(o => o.IdempotencyKey).HasMaxLength(100);
            // Backstop for the in-process lock: one order per (user, key), also across replicas.
            e.HasIndex(o => new { o.UserId, o.IdempotencyKey }).IsUnique().HasFilter("\"IdempotencyKey\" IS NOT NULL");
        });
    }
}

// Only used by `dotnet ef` to create migrations; never connects to this database.
public class OrdersDbContextFactory : IDesignTimeDbContextFactory<OrdersDbContext>
{
    public OrdersDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<OrdersDbContext>()
            .UseNpgsql("Host=localhost;Database=ordersdb;Username=postgres;Password=design-time-only")
            .Options);
}