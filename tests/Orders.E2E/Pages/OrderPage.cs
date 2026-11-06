using Microsoft.Playwright;

public class OrderPage(IPage page)
{
    public ILocator Result => page.GetByTestId("result");

    public async Task OpenAsync() => await page.GotoAsync("/");

    public async Task PlaceOrderAsync(string product, int quantity)
    {
        await page.GetByTestId("product").FillAsync(product);
        await page.GetByTestId("quantity").FillAsync(quantity.ToString());
        await page.GetByTestId("submit").ClickAsync();
    }
}