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

builder.Host.UseSerilog((ctx, lc) => lc.WriteTo.Console());
builder.Host.UseServiceProviderFactory(new AutofacServiceProviderFactory());
builder.Host.ConfigureContainer<ContainerBuilder>(c =>
{
    c.RegisterType<OrderService>();
    c.RegisterType<CreateOrderValidator>().As<IValidator<CreateOrderRequest>>().SingleInstance();
});
builder.Services.AddFusionCache();
builder.Services.AddHealthChecks();

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
            o.TokenValidationParameters.ValidateAudience = false;
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
app.UseDefaultFiles();
app.UseStaticFiles();

if (authEnabled)
{
    app.UseAuthentication();
    app.UseAuthorization();

    app.MapGet("/login", () =>
        Results.Challenge(new AuthenticationProperties { RedirectUri = "/" }, new[] { "oidc" }));

    // Ends both sessions: the app cookie and Keycloak's own (RP-initiated logout via the end-session
    // endpoint, with the saved id_token as hint). Clearing only the cookie left Keycloak signed in,
    // so "Log in" signed the user straight back in without a password.
    app.MapGet("/logout", () =>
        Results.SignOut(new AuthenticationProperties { RedirectUri = "/" },
            new[] { CookieAuthenticationDefaults.AuthenticationScheme, "oidc" }));
}

var me = app.MapGet("/me", (ClaimsPrincipal user) => Results.Ok(new { name = UserIds.Name(user) }));
if (authEnabled)
    me.RequireAuthorization();

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
        // Explicit messages (same text as FluentValidation's English defaults): the defaults are
        // translated from the server's UI culture, so a non-English server would change the API
        // contract. These are returned to callers in the 400 response.
        RuleFor(x => x.ProductName)
            .NotEmpty().WithMessage("'{PropertyName}' must not be empty.")
            .MaximumLength(50).WithMessage("The length of '{PropertyName}' must be {MaxLength} characters or fewer. You entered {TotalLength} characters.");
        RuleFor(x => x.Quantity)
            .InclusiveBetween(1, 100).WithMessage("'{PropertyName}' must be between {From} and {To}. You entered {PropertyValue}.");
    }
}

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

// Lets WebApplicationFactory<Program> in tests/Orders.IntegrationTests reach the top-level Program.
public partial class Program { }
