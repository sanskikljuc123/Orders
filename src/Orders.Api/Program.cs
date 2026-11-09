using System.Security.Claims;
using Autofac;
using Autofac.Extensions.DependencyInjection;
using FluentValidation;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Serilog;
using ZiggyCreatures.Caching.Fusion;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((ctx, lc) => lc.WriteTo.Console());
builder.Host.UseServiceProviderFactory(new AutofacServiceProviderFactory());
builder.Host.ConfigureContainer<ContainerBuilder>(c =>
{
    c.RegisterType<OrderService>();
    c.RegisterType<CreateOrderValidator>().As<IValidator<CreateOrderRequest>>().SingleInstance();
});
builder.Services.AddFusionCache();

var paymentUrl = builder.Configuration["Payment:BaseUrl"];
if (!string.IsNullOrEmpty(paymentUrl))
    builder.Services.AddHttpClient<IPaymentClient, HttpPaymentClient>(c => c.BaseAddress = new Uri(paymentUrl));
else
    builder.Services.AddSingleton<IPaymentClient, AlwaysApprovedPaymentClient>();

var connectionString = builder.Configuration.GetConnectionString("ordersdb");
var dbEnabled = !string.IsNullOrEmpty(connectionString);
if (dbEnabled)
{
    builder.Services.AddDbContext<OrdersDbContext>(o => o.UseNpgsql(connectionString));
    builder.Services.AddScoped<IOrderRepository, EfOrderRepository>();
}
else
{
    builder.Services.AddSingleton<IOrderRepository, OrderStore>();
}

var authority = builder.Configuration["Auth:Authority"];
var authEnabled = !string.IsNullOrEmpty(authority);
if (authEnabled)
{
    builder.Services
        .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(o =>
        {
            o.Authority = authority;
            o.RequireHttpsMetadata = false; // dev Keycloak runs on plain http
            o.TokenValidationParameters.ValidateAudience = false;
        });
    builder.Services.AddAuthorization();
}

var app = builder.Build();

if (dbEnabled)
{
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<OrdersDbContext>().Database.MigrateAsync();
}

app.UseSerilogRequestLogging();
app.UseDefaultFiles();
app.UseStaticFiles();

if (authEnabled)
{
    app.UseAuthentication();
    app.UseAuthorization();
}

var orders = app.MapGroup("/orders");
if (authEnabled)
    orders.RequireAuthorization();

orders.MapPost("", async (
    CreateOrderRequest req,
    ClaimsPrincipal user,
    [FromServices] IValidator<CreateOrderRequest> validator,
    [FromServices] OrderService orderService) =>
{
    var result = await validator.ValidateAsync(req);
    if (!result.IsValid)
        return Results.ValidationProblem(result.ToDictionary());

    var order = await orderService.PlaceOrderAsync(req, UserIds.Get(user));
    return Results.Created($"/orders/{order.Id}", order);
});

orders.MapGet("", async (
    ClaimsPrincipal user,
    [FromServices] IOrderRepository repo) =>
    Results.Ok(await repo.ListAsync(UserIds.Get(user))));

orders.MapGet("/{id:guid}", async (
    Guid id,
    ClaimsPrincipal user,
    [FromServices] IOrderRepository repo,
    [FromServices] IFusionCache cache) =>
{
    var userId = UserIds.Get(user);
    var order = await cache.GetOrSetAsync<Order?>(
        $"order:{userId}:{id}",
        (ctx, ct) => repo.FindAsync(id, userId),
        options: new FusionCacheEntryOptions(TimeSpan.FromSeconds(30)));
    return order is null ? Results.NotFound() : Results.Ok(order);
});

app.Run();

public record CreateOrderRequest(string ProductName, int Quantity);

public record Order(
    Guid Id,
    string ProductName,
    int Quantity,
    string Status,
    string UserId = "anonymous",
    DateTimeOffset CreatedAt = default);

public class CreateOrderValidator : AbstractValidator<CreateOrderRequest>
{
    public CreateOrderValidator()
    {
        RuleFor(x => x.ProductName).NotEmpty().MaximumLength(50);
        RuleFor(x => x.Quantity).InclusiveBetween(1, 100);
    }
}

public static class UserIds
{
    public static string Get(ClaimsPrincipal user) =>
        user.FindFirst("sub")?.Value
        ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value
        ?? "anonymous";
}

public interface IPaymentClient { Task<bool> ChargeAsync(decimal amount); }

public class AlwaysApprovedPaymentClient : IPaymentClient
{
    public Task<bool> ChargeAsync(decimal amount) => Task.FromResult(true);
}

public class HttpPaymentClient(HttpClient http) : IPaymentClient
{
    public async Task<bool> ChargeAsync(decimal amount)
    {
        var res = await http.PostAsJsonAsync("/charge", new { amount });
        if (!res.IsSuccessStatusCode) return false;
        var body = await res.Content.ReadFromJsonAsync<ChargeResponse>();
        return body?.Approved ?? false;
    }
}

public record ChargeResponse(bool Approved);

public partial class Program { }