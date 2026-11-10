using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

/// <summary>
/// The API in a given environment (e.g. "Production") with every setting it requires there:
/// login authority, payment provider and database. Authority and provider are never contacted by
/// tests that only read options or call anonymous endpoints; the database is the test PostgreSQL
/// (migrations run at startup). Pass <paramref name="without"/> to leave settings out on purpose.
/// </summary>
public sealed class ApiInEnvironment(string environment, string dbConnectionString, params string[] without)
    : WebApplicationFactory<Program>
{
    public static readonly IReadOnlyDictionary<string, string> RequiredOutsideDevelopment = new Dictionary<string, string>
    {
        ["Auth:Authority"] = "https://keycloak.invalid/realms/orders",
        ["Payment:BaseUrl"] = "https://payment.invalid",
        ["ConnectionStrings:ordersdb"] = "",   // filled with the test database below
    };

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(environment);
        foreach (var (key, value) in RequiredOutsideDevelopment)
            if (!without.Contains(key))
                builder.UseSetting(key, key == "ConnectionStrings:ordersdb" ? dbConnectionString : value);
    }
}
