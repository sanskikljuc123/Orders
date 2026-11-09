using Orders.E2E.Hooks;
using Orders.E2E.Pages;

namespace Orders.E2E.Steps;

[Binding]
public class LoginSteps(ScenarioBrowser browser)
{
    private OrderPage Shop => new(browser.Page);

    /// <summary>Reuses the cached Keycloak session; only Login.feature uses the login form directly.</summary>
    [Given("I am signed in as {string}")]
    public async Task SignedInAs(string user) =>
        await browser.OpenAsync(await TestEnvironment.GetStorageStateAsync(user));

    /// <summary>
    /// A real login in this scenario's own browser context, never the shared cached session:
    /// logout scenarios end the session, which must not break later scenarios that reuse the cache.
    /// </summary>
    [Given("I have just signed in as {string}")]
    public async Task JustSignedInAs(string user)
    {
        await Shop.OpenAsync();
        await Shop.SignInAsync(user, TestEnvironment.PasswordFor(user));
        await Assertions.Expect(Shop.SignedInUser).ToHaveTextAsync(user);
    }

    [When("I log out")]
    public Task LogOut() => Shop.LogOutAsync();

    [When("I start logging in again")]
    public Task StartLoggingInAgain() => Shop.LoginLink.ClickAsync();

    /// <summary>Keycloak's session is gone too, so it shows its login form instead of signing straight back in.</summary>
    [Then("I am asked for my password")]
    public Task AskedForPassword() => Assertions.Expect(new KeycloakLoginPage(browser.Page).PasswordField).ToBeVisibleAsync();

    [When("I sign in as {string} with password {string}")]
    public async Task SignInWith(string user, string password)
    {
        await Shop.OpenAsync();
        await Shop.SignInAsync(user, password);
    }

    [When("I open the shop")]
    public Task OpenShop() => Shop.OpenAsync();

    [Then("I am signed in as {string}")]
    public Task IAmSignedInAs(string user) => Assertions.Expect(Shop.SignedInUser).ToHaveTextAsync(user);

    [Then("I am asked to log in")]
    public Task AskedToLogIn() => Assertions.Expect(Shop.LoginLink).ToBeVisibleAsync();
}
