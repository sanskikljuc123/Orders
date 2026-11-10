using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

/// <summary>
/// Real tokens with exactly one thing wrong each. The API keeps every validation setting of its
/// own (Program.cs); only the trusted issuer and signing key are the test's, handed to JwtBearer as
/// its OpenID configuration (so no Keycloak and no waiting: an expired token is simply minted with
/// exp in the past). No Docker needed.
/// </summary>
public sealed class TokenValidationTests : IClassFixture<TokenValidationTests.ApiTrustingTestIssuer>
{
    private const string Issuer = "https://issuer.test/realms/orders";

    public sealed class ApiTrustingTestIssuer : OrdersApiFactory
    {
        public RsaSecurityKey Key { get; } = new(RSA.Create(2048)) { KeyId = "test-key" };

        protected override IReadOnlyDictionary<string, string> Settings { get; } =
            new Dictionary<string, string> { ["Auth:Authority"] = Issuer };

        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(s => s.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, o =>
            {
                var configuration = new OpenIdConnectConfiguration { Issuer = Issuer };
                configuration.SigningKeys.Add(Key);
                o.Configuration = configuration;   // instead of downloading Keycloak's metadata
                // A fixed configuration does not pass its issuer and keys on (IDX10204, IDX10500),
                // a downloaded one does: trust the same issuer and key directly. Every other
                // setting (lifetime, audience, signed tokens, clock skew) stays Program.cs's.
                o.TokenValidationParameters.ValidIssuer = Issuer;
                o.TokenValidationParameters.IssuerSigningKey = Key;
            }));
        }
    }

    private readonly ApiTrustingTestIssuer _api;

    public TokenValidationTests(ApiTrustingTestIssuer api) => _api = api;

    private string Token(string issuer = Issuer, string audience = "orders-api", SecurityKey? key = null,
        DateTime? expires = null, bool withExpiry = true)
    {
        var now = DateTime.UtcNow;
        return new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false }.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            Subject = new ClaimsIdentity([new Claim("sub", "ana-id"), new Claim("preferred_username", "ana")]),
            IssuedAt = now.AddMinutes(-1),
            NotBefore = now.AddMinutes(-1),
            Expires = withExpiry ? expires ?? now.AddMinutes(5) : null,
            SigningCredentials = new SigningCredentials(key ?? _api.Key, SecurityAlgorithms.RsaSha256),
        });
    }

    private async Task<HttpStatusCode> CallMeWith(string token)
    {
        var client = _api.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return (await client.GetAsync("/me")).StatusCode;
    }

    [Fact]
    public async Task Valid_token_is_accepted()   // proves the setup, so the 401s below mean something
        => Assert.Equal(HttpStatusCode.OK, await CallMeWith(Token()));

    [Fact]
    public async Task Expired_token_returns_401()   // beyond JwtBearer's 5-minute clock skew
        => Assert.Equal(HttpStatusCode.Unauthorized, await CallMeWith(Token(expires: DateTime.UtcNow.AddMinutes(-10))));

    [Fact]
    public async Task Token_without_expiry_returns_401()
        => Assert.Equal(HttpStatusCode.Unauthorized, await CallMeWith(Token(withExpiry: false)));

    [Fact]
    public async Task Token_from_another_issuer_returns_401()
        => Assert.Equal(HttpStatusCode.Unauthorized, await CallMeWith(Token(issuer: "https://evil.test/realms/orders")));

    [Fact]
    public async Task Token_for_another_audience_returns_401()
        => Assert.Equal(HttpStatusCode.Unauthorized, await CallMeWith(Token(audience: "another-api")));

    [Fact]
    public async Task Token_signed_with_another_key_returns_401()
        => Assert.Equal(HttpStatusCode.Unauthorized, await CallMeWith(Token(key: new RsaSecurityKey(RSA.Create(2048)) { KeyId = "test-key" })));
}
