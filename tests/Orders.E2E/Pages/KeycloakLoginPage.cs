namespace Orders.E2E.Pages;

/// <summary>
/// Keycloak's login form: a third-party page we do not own. Its labels are localisable and its
/// markup is Keycloak's, so the stable ids from the Keycloak theme are used here and nowhere else.
/// </summary>
public class KeycloakLoginPage(IPage page)
{
    public async Task SubmitAsync(string user, string password)
    {
        await page.Locator("#username").FillAsync(user);
        await page.Locator("#password").FillAsync(password);
        await page.Locator("#kc-login").ClickAsync();
    }
}
