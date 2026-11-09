using Microsoft.Playwright;

public class OrderPage(IPage page)
{
    public ILocator Result => page.GetByTestId("result");
    public ILocator LoginButton => page.GetByTestId("login");
    public ILocator SignedInUser => page.GetByTestId("user");
    public ILocator MyOrders => page.GetByTestId("orders-list");
    public ILocator LoginError => page.GetByText("Invalid username or password");

    public async Task OpenAsync() => await page.GotoAsync("/");

    public async Task SignInAsync(string username, string password)
    {
        await LoginButton.ClickAsync();
        // The next three elements belong to the Keycloak login page, not to our app.
        await page.Locator("#username").FillAsync(username);
        await page.Locator("#password").FillAsync(password);
        await page.Locator("#kc-login").ClickAsync();
    }

    public async Task PlaceOrderAsync(string product, int quantity)
    {
        await page.GetByTestId("product").FillAsync(product);
        await page.GetByTestId("quantity").FillAsync(quantity.ToString());
        await page.GetByTestId("submit").ClickAsync();
    }
}