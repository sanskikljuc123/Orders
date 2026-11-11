using System.Security.Claims;
using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using ZiggyCreatures.Caching.Fusion;

/// <summary>
/// /me and /orders. In their own file (not Program.cs) so Stryker can mutate exactly the code
/// that decides who sees which order (stryker/api-integration.json).
/// </summary>
public static class OrderEndpoints
{
    public static void MapOrderEndpoints(this WebApplication app, bool authEnabled)
    {
        var me = app.MapGet("/me", (ClaimsPrincipal user) => Results.Ok(new Me(UserIds.Name(user))))
            .Produces<Me>()
            .Produces(StatusCodes.Status401Unauthorized);
        if (authEnabled)
            me.RequireAuthorization();

        var orders = app.MapGroup("/orders");
        if (authEnabled)
            orders.RequireAuthorization();

        orders.MapPost("", async (
            CreateOrderRequest req,
            ClaimsPrincipal user,
            HttpContext http,
            [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
            [FromServices] IValidator<CreateOrderRequest> validator,
            [FromServices] OrderService orderService,
            [FromServices] ILogger<OrderService> log) =>
        {
            var result = await validator.ValidateAsync(req);
            if (!result.IsValid)
                return Results.ValidationProblem(result.ToDictionary());

            if (idempotencyKey is not null && (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 100))
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["Idempotency-Key"] = ["'Idempotency-Key' must be 1 to 100 characters."]
                });

            PlacedOrder placed;
            try
            {
                placed = await orderService.PlaceOrderAsync(req, UserIds.Get(user), idempotencyKey);
            }
            catch (Exception e)
            {
                // Only possible before the charge (reserving the order failed): every failure after the
                // charge is handled in OrderService and leaves the order Pending.
                log.LogError(e, "Order not placed: could not reserve it; nothing charged");
                return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Order not placed",
                    detail: "Nothing was charged. Try again with the same Idempotency-Key.");
            }
            if (placed.Outcome == PlaceOutcome.KeyUsedForDifferentOrder)   // 422 as in the IETF Idempotency-Key draft
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["Idempotency-Key"] = ["'Idempotency-Key' was already used for a different order."]
                }, statusCode: StatusCodes.Status422UnprocessableEntity);
            if (placed.Replayed)
                http.Response.Headers["Idempotent-Replayed"] = "true";   // same order, not charged again
            if (placed.Outcome == PlaceOutcome.Pending)   // charged (or attempted), result not saved yet
                return Results.Accepted($"/orders/{placed.Order.Id}", placed.Order);
            return Results.Created($"/orders/{placed.Order.Id}", placed.Order);
        })
            .Produces<Order>(StatusCodes.Status201Created)
            .Produces<Order>(StatusCodes.Status202Accepted)                         // Pending: retry with the same key settles it
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)              // not placed, nothing charged
            .ProducesValidationProblem()                                            // field errors, bad Idempotency-Key
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)    // key used for another order
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status415UnsupportedMediaType);

        orders.MapGet("", async (
            ClaimsPrincipal user,
            [FromServices] IOrderRepository repo) =>
            Results.Ok(await repo.ListAsync(UserIds.Get(user))))
            .Produces<List<Order>>()
            .Produces(StatusCodes.Status401Unauthorized);

        orders.MapGet("/{id:guid}", async (
            Guid id,
            ClaimsPrincipal user,
            [FromServices] IOrderRepository repo,
            [FromServices] IFusionCache cache) =>
        {
            var userId = UserIds.Get(user);
            var key = $"order:{userId}:{id}";
            var order = await cache.GetOrSetAsync<Order?>(
                key,
                (ctx, ct) => repo.FindAsync(id, userId),
                options: new FusionCacheEntryOptions(TimeSpan.FromSeconds(30)));
            if (order?.Status == OrderStatus.Pending)
                await cache.RemoveAsync(key);   // only final states are cached: the next read sees Paid/PaymentFailed
            return order is null ? Results.NotFound() : Results.Ok(order);
        })
            .Produces<Order>()
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status401Unauthorized);
    }
}
