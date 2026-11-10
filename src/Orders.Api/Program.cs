using System.Security.Claims;
using Autofac;
using Autofac.Extensions.DependencyInjection;
using FluentValidation;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Serilog;
using ZiggyCreatures.Caching.Fusion;

var builder = WebApplication.CreateBuilder(args);

// ReadFrom.Services adds sinks registered in DI (tests capture every event to check that no
// secret is ever logged, see LogSecretsTests).
builder.Host.UseSerilog((ctx, services, lc) => lc.ReadFrom.Services(services).WriteTo.Console());
builder.Host.UseServiceProviderFactory(new AutofacServiceProviderFactory());
builder.Host.ConfigureContainer<ContainerBuilder>(c =>
{
    c.RegisterType<OrderService>();
    c.RegisterType<IdempotencyLocks>().SingleInstance();
    c.RegisterType<CreateOrderValidator>().As<IValidator<CreateOrderRequest>>().SingleInstance();
});
builder.Services.AddFusionCache();
builder.Services.AddOpenApi();   // the API contract; snapshot in docs/api/openapi.v1.json
builder.Services.AddSingleton(TimeProvider.System);   // replaced with a fixed clock in tests
builder.Services.AddHealthChecks();

// Fail fast outside Development. Without these settings the API would quietly fall back to its
// local/test defaults: no login at all, every order "Paid" without a charge, orders kept only in
// memory (lost on restart). Development keeps the defaults for local runs and tests.
if (!builder.Environment.IsDevelopment())
{
    var missing = new[] { "Auth:Authority", "Payment:BaseUrl", "ConnectionStrings:ordersdb" }
        .Where(key => string.IsNullOrWhiteSpace(builder.Configuration[key]))
        .ToArray();
    if (missing.Length > 0)
        throw new InvalidOperationException(
            $"Missing required settings in environment '{builder.Environment.EnvironmentName}': {string.Join(", ", missing)}. "
            + "Outside Development the API refuses to start without login, payment provider and database.");
}

var paymentUrl = builder.Configuration["Payment:BaseUrl"];
if (!string.IsNullOrEmpty(paymentUrl))
    builder.Services.AddHttpClient<IPaymentClient, HttpPaymentClient>(c =>
    {
        c.BaseAddress = new Uri(paymentUrl);
        // The default (100 s) would hold the order request open for a slow provider.
        c.Timeout = TimeSpan.FromSeconds(builder.Configuration.GetValue("Payment:TimeoutSeconds", 10));
    });
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
// Local runs and tests use Keycloak over plain http. Only appsettings.Development.json turns this on;
// everywhere else the secure defaults apply (HTTPS metadata, form_post, Secure cookies).
var allowInsecureHttp = builder.Configuration.GetValue("Auth:AllowInsecureHttp", false);
if (authEnabled)
{
    builder.Services
        .AddAuthentication(o =>
        {
            o.DefaultScheme = "Smart";
            o.DefaultChallengeScheme = "Smart";
        })
        // Requests with an Authorization header use the JWT; browser requests use the cookie.
        .AddPolicyScheme("Smart", null, o =>
        {
            o.ForwardDefaultSelector = ctx =>
                ctx.Request.Headers.ContainsKey("Authorization")
                    ? JwtBearerDefaults.AuthenticationScheme
                    : CookieAuthenticationDefaults.AuthenticationScheme;
        })
        .AddJwtBearer(o =>
        {
            o.Authority = authority;
            o.RequireHttpsMetadata = !allowInsecureHttp;
            // Only tokens issued for this API (aud "orders-api", added by the realm's audience mapper
            // on orders-web). Without it a token for any other client of the realm was accepted.
            o.Audience = builder.Configuration["Auth:Audience"] ?? "orders-api";
        })
        .AddCookie(CookieAuthenticationDefaults.AuthenticationScheme, o =>
        {
            o.Cookie.Name = "orders.auth";
            o.Cookie.SameSite = SameSiteMode.Lax;
            o.Cookie.SecurePolicy = allowInsecureHttp ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
            // API-style behaviour: answer 401 instead of redirecting. Login starts only at /login.
            o.Events.OnRedirectToLogin = ctx =>
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            };
            o.Events.OnRedirectToAccessDenied = ctx =>
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                return Task.CompletedTask;
            };
        })
        .AddOpenIdConnect("oidc", o =>
        {
            o.Authority = authority;
            o.ClientId = "orders-web";
            o.RequireHttpsMetadata = !allowInsecureHttp;
            o.ResponseType = "code";
            o.UsePkce = true;
            if (allowInsecureHttp)
            {
                // Plain http: query response mode and Lax cookies (form_post needs SameSite=None + Secure).
                o.ResponseMode = "query";
                o.CorrelationCookie.SameSite = SameSiteMode.Lax;
                o.NonceCookie.SameSite = SameSiteMode.Lax;
            }
            // Otherwise the handler defaults apply: form_post, SameSite=None + Secure correlation/nonce cookies.
            o.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            o.Scope.Add("profile");
            o.SaveTokens = true;   // id_token_hint for Keycloak logout
            o.TokenValidationParameters.NameClaimType = "preferred_username";
        });
    builder.Services.AddAuthorization();

    // Server-side sessions: the cookie holds only a key, and logout deletes the session, so a copied
    // cookie cannot be replayed after logout.
    builder.Services.AddSingleton<SessionStore>();
    builder.Services.AddOptions<CookieAuthenticationOptions>(CookieAuthenticationDefaults.AuthenticationScheme)
        .Configure<SessionStore>((o, store) => o.SessionStore = store);
}

var app = builder.Build();

if (dbEnabled)
{
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<OrdersDbContext>().Database.MigrateAsync();
}

app.UseSerilogRequestLogging();
// Anonymous readiness probe (Aspire WithHttpHealthCheck, E2E startup). Reports only "Healthy"/"Unhealthy".
app.MapHealthChecks("/health");

// The contract document is for development and tests (snapshot test, fuzzing); production does not
// publish it. Public on purpose in Development (EndpointAuthTests' public list).
if (app.Environment.IsDevelopment())
    app.MapOpenApi();
app.UseDefaultFiles();
app.UseStaticFiles();

if (authEnabled)
{
    app.UseAuthentication();
    app.UseAuthorization();

    app.MapGet("/login", () =>
        Results.Challenge(new AuthenticationProperties { RedirectUri = "/" }, new[] { "oidc" }))
        .ExcludeFromDescription();   // browser redirect to Keycloak, not part of the API contract

    // Ends both sessions: the app cookie and Keycloak's own (RP-initiated logout via the end-session
    // endpoint, with the saved id_token as hint). Clearing only the cookie left Keycloak signed in,
    // so "Log in" signed the user straight back in without a password.
    app.MapGet("/logout", () =>
        Results.SignOut(new AuthenticationProperties { RedirectUri = "/" },
            new[] { CookieAuthenticationDefaults.AuthenticationScheme, "oidc" }))
        .ExcludeFromDescription();
}

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

app.Run();

public record CreateOrderRequest(string ProductName, int Quantity);

public record Me(string Name);

public record Order(
    Guid Id,
    string ProductName,
    int Quantity,
    [property: System.ComponentModel.Description("Pending (charge in progress or its result not saved yet; retry with the same Idempotency-Key), Paid or PaymentFailed.")]
    string Status,
    [property: System.Text.Json.Serialization.JsonIgnore] string UserId,   // internal (Keycloak sub): stored, never sent
    DateTimeOffset CreatedAt,
    [property: System.Text.Json.Serialization.JsonIgnore] string? IdempotencyKey);   // no defaults: the OpenAPI schema
                                                                                      // generator fails on `= default`

public static class UserIds
{
    public static string Get(ClaimsPrincipal user) =>
        user.FindFirst("sub")?.Value
        ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value
        ?? "anonymous";

    public static string Name(ClaimsPrincipal user) =>
        user.Identity?.Name
        ?? user.FindFirst("preferred_username")?.Value
        ?? "anonymous";
}

public class AlwaysApprovedPaymentClient : IPaymentClient
{
    public Task<bool> ChargeAsync(decimal amount, Guid reference) => Task.FromResult(true);
}

// Lets WebApplicationFactory<Program> in tests/Orders.IntegrationTests reach the top-level Program.
public partial class Program { }
