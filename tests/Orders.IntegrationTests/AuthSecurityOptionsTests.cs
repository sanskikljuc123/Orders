using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;

/// <summary>
/// The relaxed http-only login settings must exist only where appsettings.Development.json turns
/// them on. Any other environment (e.g. the Kubernetes deployment) gets the secure defaults.
/// </summary>
public class AuthSecurityOptionsTests
{
    private sealed class ApiInEnvironment(string environment) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(environment);
            // Options are only read, never used, so the authority is never contacted.
            builder.UseSetting("Auth:Authority", "https://keycloak.invalid/realms/orders");
        }
    }

    private static (OpenIdConnectOptions Oidc, JwtBearerOptions Jwt, CookieAuthenticationOptions Cookie) OptionsIn(string environment)
    {
        using var api = new ApiInEnvironment(environment);
        var services = api.Services;
        return (
            services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>().Get("oidc"),
            services.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>().Get(JwtBearerDefaults.AuthenticationScheme),
            services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(CookieAuthenticationDefaults.AuthenticationScheme));
    }

    [Fact]
    public void Production_requires_https_and_secure_cookies()
    {
        var (oidc, jwt, cookie) = OptionsIn("Production");

        Assert.True(oidc.RequireHttpsMetadata);
        Assert.True(jwt.RequireHttpsMetadata);
        Assert.Equal("form_post", oidc.ResponseMode);
        Assert.Equal(CookieSecurePolicy.Always, cookie.Cookie.SecurePolicy);
    }

    [Fact]
    public void Development_allows_plain_http_for_local_keycloak()
    {
        var (oidc, jwt, cookie) = OptionsIn("Development");

        Assert.False(oidc.RequireHttpsMetadata);
        Assert.False(jwt.RequireHttpsMetadata);
        Assert.Equal("query", oidc.ResponseMode);
        Assert.Equal(SameSiteMode.Lax, oidc.CorrelationCookie.SameSite);
        Assert.Equal(CookieSecurePolicy.SameAsRequest, cookie.Cookie.SecurePolicy);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Development")]
    public void Expired_unsigned_or_foreign_tokens_are_rejected_in_every_environment(string environment)
    {
        // Testing a real expired token would mean waiting out Keycloak's lifetime plus the clock
        // skew (JwtBearer's lifetime check does not use TimeProvider), so this guards the settings
        // the library's own checks depend on instead.
        var jwt = OptionsIn(environment).Jwt.TokenValidationParameters;

        Assert.True(jwt.ValidateLifetime);
        Assert.True(jwt.RequireExpirationTime);
        Assert.True(jwt.RequireSignedTokens);
        Assert.True(jwt.ValidateIssuer);
        Assert.True(jwt.ValidateAudience);
        Assert.Equal("orders-api", OptionsIn(environment).Jwt.Audience);
        Assert.True(jwt.ClockSkew <= TimeSpan.FromMinutes(5), $"clock skew {jwt.ClockSkew} lets expired tokens in for too long");
    }

    [Fact]
    public async Task Login_cookie_sessions_live_on_the_server_so_logout_can_revoke_them()
    {
        using var api = new ApiInEnvironment("Production");
        var cookie = api.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(CookieAuthenticationDefaults.AuthenticationScheme);

        // The app's own configured store (and its cache), not a test double.
        var store = Assert.IsType<SessionStore>(cookie.SessionStore);
        var ticket = new AuthenticationTicket(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim("preferred_username", "ana")], "test")),
            new AuthenticationProperties(), CookieAuthenticationDefaults.AuthenticationScheme);   // no expiry: default lifetime

        var key = await store.StoreAsync(ticket);
        Assert.Equal("ana", (await store.RetrieveAsync(key))!.Principal.FindFirst("preferred_username")!.Value);

        await store.RemoveAsync(key);   // what sign-out does
        Assert.Null(await store.RetrieveAsync(key));
    }
}
