namespace Orders.E2E.Pages;

/// <summary>The Orders single page (src/Orders.Api/wwwroot/index.html). Only place its selectors live.</summary>
public class OrderPage(IPage page)
{
    public ILocator SignedInUser => page.GetByTestId("user");
    public ILocator LoginLink => page.GetByRole(AriaRole.Link, new() { Name = "Log in" });
    public ILocator Result => page.GetByTestId("result");
    public ILocator MyOrders => page.GetByTestId("orders-list").GetByRole(AriaRole.Listitem);

    public Task OpenAsync() => page.GotoAsync("/");   // relative to BaseURL

    /// <summary>Opens the page and waits until the user's order list has been loaded.</summary>
    public Task OpenMyOrdersAsync() =>
        page.RunAndWaitForResponseAsync(OpenAsync, r => r.Url.EndsWith("/orders") && r.Request.Method == "GET");

    public async Task SignInAsync(string user, string password)
    {
        await LoginLink.ClickAsync();
        await new KeycloakLoginPage(page).SubmitAsync(user, password);
    }

    public async Task PlaceOrderAsync(string product, int quantity)
    {
        await page.GetByLabel("Product", new() { Exact = true }).FillAsync(product);
        await page.GetByLabel("Quantity", new() { Exact = true }).FillAsync(quantity.ToString());
        await page.GetByRole(AriaRole.Button, new() { Name = "Place order" }).ClickAsync();
    }
}
