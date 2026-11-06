using Autofac;
using Autofac.Extensions.DependencyInjection;
using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using Serilog;
using System.Collections.Concurrent;
using ZiggyCreatures.Caching.Fusion;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((ctx, lc) => lc.WriteTo.Console());
builder.Host.UseServiceProviderFactory(new AutofacServiceProviderFactory());
builder.Host.ConfigureContainer<ContainerBuilder>(c =>
{
    c.RegisterType<OrderStore>().SingleInstance();
    c.RegisterType<AlwaysApprovedPaymentClient>().As<IPaymentClient>().SingleInstance();
    c.RegisterType<CreateOrderValidator>().As<IValidator<CreateOrderRequest>>().SingleInstance();
});
builder.Services.AddFusionCache();

var app = builder.Build();
app.UseSerilogRequestLogging();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapPost("/orders", async (
    CreateOrderRequest req,
    [FromServices] IValidator<CreateOrderRequest> validator,
    [FromServices] IPaymentClient payment,
    [FromServices] OrderStore store) =>
{
    var result = await validator.ValidateAsync(req);
    if (!result.IsValid)
        return Results.ValidationProblem(result.ToDictionary());

    var paid = await payment.ChargeAsync(req.Quantity * 10m);
    var order = new Order(Guid.NewGuid(), req.ProductName, req.Quantity, paid ? "Paid" : "PaymentFailed");
    store.Add(order);
    return Results.Created($"/orders/{order.Id}", order);
});

app.MapGet("/orders/{id:guid}", async (
    Guid id,
    [FromServices] OrderStore store,
    [FromServices] IFusionCache cache) =>
{
    var order = await cache.GetOrSetAsync<Order?>(
        $"order:{id}",
        (ctx, ct) => Task.FromResult(store.Find(id)),
        options: new FusionCacheEntryOptions(TimeSpan.FromSeconds(30)));
    return order is null ? Results.NotFound() : Results.Ok(order);
});

app.Run();

public record CreateOrderRequest(string ProductName, int Quantity);
public record Order(Guid Id, string ProductName, int Quantity, string Status);

public class CreateOrderValidator : AbstractValidator<CreateOrderRequest>
{
    public CreateOrderValidator()
    {
        RuleFor(x => x.ProductName).NotEmpty().MaximumLength(50);
        RuleFor(x => x.Quantity).InclusiveBetween(1, 100);
    }
}

public class OrderStore
{
    private readonly ConcurrentDictionary<Guid, Order> _orders = new();
    public void Add(Order o) => _orders[o.Id] = o;
    public Order? Find(Guid id) => _orders.GetValueOrDefault(id);
}

// Placeholder: in Phase 2 this becomes an HTTP call to FakePaymentService.
public interface IPaymentClient { Task<bool> ChargeAsync(decimal amount); }
public class AlwaysApprovedPaymentClient : IPaymentClient
{
    public Task<bool> ChargeAsync(decimal amount) => Task.FromResult(true);
}