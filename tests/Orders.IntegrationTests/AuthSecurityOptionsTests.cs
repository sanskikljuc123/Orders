using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

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
}
