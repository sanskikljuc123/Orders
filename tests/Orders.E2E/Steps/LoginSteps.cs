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
