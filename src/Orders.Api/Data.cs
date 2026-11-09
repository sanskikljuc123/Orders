using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

public interface IOrderRepository
{
    Task AddAsync(Order order);
    Task<Order?> FindAsync(Guid id, string userId);
    Task<List<Order>> ListAsync(string userId);
}

// Used when no database is configured (unit/integration tests without a DB).
public class OrderStore : IOrderRepository
{
    private readonly ConcurrentDictionary<Guid, Order> _orders = new();

    public void Add(Order o) => _orders[o.Id] = o;
    public Order? Find(Guid id) => _orders.GetValueOrDefault(id);

    public Task AddAsync(Order o) { Add(o); return Task.CompletedTask; }

    public Task<Order?> FindAsync(Guid id, string userId)
    {
        var o = Find(id);
        return Task.FromResult(o is not null && o.UserId == userId ? o : null);
    }

    public Task<List<Order>> ListAsync(string userId) =>
        Task.FromResult(_orders.Values.Where(o => o.UserId == userId).OrderBy(o => o.CreatedAt).ToList());
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
        });
    }
}

public class EfOrderRepository(OrdersDbContext db) : IOrderRepository
{
    public async Task AddAsync(Order order)
    {
        db.Orders.Add(order);
        await db.SaveChangesAsync();
    }

    public Task<Order?> FindAsync(Guid id, string userId) =>
        db.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == id && o.UserId == userId);

    public Task<List<Order>> ListAsync(string userId) =>
        db.Orders.AsNoTracking().Where(o => o.UserId == userId).OrderBy(o => o.CreatedAt).ToListAsync();
}

// Only used by `dotnet ef` to create migrations; never connects to this database.
public class OrdersDbContextFactory : IDesignTimeDbContextFactory<OrdersDbContext>
{
    public OrdersDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<OrdersDbContext>()
            .UseNpgsql("Host=localhost;Database=ordersdb;Username=postgres;Password=design-time-only")
            .Options);
}