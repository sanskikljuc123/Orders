using System.Net;

/// <summary>
/// Outside Development the API must refuse to start without login, payment provider or database,
/// instead of falling back to its local defaults (no login, every order "Paid" without a charge,
/// orders only in memory). Development keeps those defaults for local runs and tests. Needs Docker
/// for the database of the starting cases (StackFixture).
/// </summary>
[Collection("Stack")]
public class StartupConfigurationTests(StackFixture fixture)
{
    [Theory]
    [InlineData("Auth:Authority")]
    [InlineData("Payment:BaseUrl")]
    [InlineData("ConnectionStrings:ordersdb")]
    public void Production_refuses_to_start_without_a_required_setting(string missing)
    {
        using var api = new ApiInEnvironment("Production", fixture.DbConnectionString, without: missing);

        var error = Assert.Throws<InvalidOperationException>(() => api.CreateClient());

        Assert.Contains(missing, error.Message);
        Assert.Contains("'Production'", error.Message);
    }

    [Fact]
    public void All_missing_settings_are_named_at_once()
    {
        using var api = new ApiInEnvironment("Staging", fixture.DbConnectionString,
            without: [.. ApiInEnvironment.RequiredOutsideDevelopment.Keys]);

        var error = Assert.Throws<InvalidOperationException>(() => api.CreateClient());

        Assert.Contains("Auth:Authority, Payment:BaseUrl, ConnectionStrings:ordersdb", error.Message);
    }

    [Fact]
    public async Task Production_with_every_setting_starts()   // the control: the settings above are the only reason
    {
        using var api = new ApiInEnvironment("Production", fixture.DbConnectionString);

        Assert.Equal(HttpStatusCode.OK, (await api.CreateClient().GetAsync("/health")).StatusCode);
    }

    [Fact]
    public async Task Development_still_starts_without_them()   // local runs and the in-memory test factory
    {
        using var api = new ApiInEnvironment("Development", fixture.DbConnectionString,
            without: [.. ApiInEnvironment.RequiredOutsideDevelopment.Keys]);

        Assert.Equal(HttpStatusCode.OK, (await api.CreateClient().GetAsync("/health")).StatusCode);
    }
}
