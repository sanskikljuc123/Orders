using Orders.E2E.Hooks;

namespace Orders.E2E.Steps;

/// <summary>
/// Replays a copy of the app's session cookie in a separate browser context, the way a stolen
/// cookie would be used. Only the server can reject it, so this checks server-side logout.
/// </summary>
[Binding]
public class SessionCookieSteps(ScenarioBrowser browser)
{
    private const string AuthCookie = "orders.auth";   // set in src/Orders.Api/Program.cs (may be chunked: orders.authC1, ...)
    private IReadOnlyList<Cookie> _copy = [];

    [Given("I keep a copy of my session cookie")]
    public async Task KeepCopy()
    {
        _copy = (await browser.Page.Context.CookiesAsync())
            .Where(c => c.Name.StartsWith(AuthCookie, StringComparison.Ordinal))
            .Select(c => new Cookie { Name = c.Name, Value = c.Value, Domain = c.Domain, Path = c.Path })
            .ToList();
        Assert.NotEmpty(_copy);
    }

    [Then("the copied session cookie is accepted")]
    public async Task CopyAccepted() => Assert.Equal(200, await StatusOfMeWithCopyAsync());

    [Then("the copied session cookie is rejected")]
    public async Task CopyRejected() => Assert.Equal(401, await StatusOfMeWithCopyAsync());

    private async Task<int> StatusOfMeWithCopyAsync()
    {
        await using var other = await TestEnvironment.NewContextAsync();
        await other.AddCookiesAsync(_copy);
        var res = await other.APIRequest.GetAsync("/me");
        return res.Status;
    }
}
