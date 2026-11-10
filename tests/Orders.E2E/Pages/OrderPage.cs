namespace Orders.E2E.Pages;

/// <summary>The Orders single page (src/Orders.Api/wwwroot/index.html). Only place its selectors live.</summary>
public class OrderPage(IPage page)
{
    public ILocator SignedInUser => page.GetByTestId("user");
    public ILocator LoginLink => page.GetByRole(AriaRole.Link, new() { Name = "Log in" });
    public ILocator Result => page.GetByTestId("result");
    public ILocator MyOrders => page.GetByTestId("orders-list").GetByRole(AriaRole.Listitem);
    public ILocator LogoutLink => page.GetByRole(AriaRole.Link, new() { Name = "Log out" });
    public ILocator PlaceOrderButton => page.GetByRole(AriaRole.Button, new() { Name = "Place order" });

    public Task OpenAsync() => page.GotoAsync("/");   // relative to BaseURL

    /// <summary>Opens the page and waits until the user's order list has been loaded.</summary>
    public Task OpenMyOrdersAsync() =>
        page.RunAndWaitForResponseAsync(OpenAsync, r => r.Url.EndsWith("/orders") && r.Request.Method == "GET");

    public async Task SignInAsync(string user, string password)
    {
        await LoginLink.ClickAsync();
        await new KeycloakLoginPage(page).SubmitAsync(user, password);
    }

    /// <summary>
    /// Fills the form, clicks "Place order" and waits until the server has answered, so a step
    /// that navigates next cannot cancel the request in flight. Returns the HTTP status.
    /// </summary>
    public async Task<int> PlaceOrderAsync(string product, int quantity)
    {
        await page.GetByLabel("Product", new() { Exact = true }).FillAsync(product);
        await page.GetByLabel("Quantity", new() { Exact = true }).FillAsync(quantity.ToString());
        var response = await page.RunAndWaitForResponseAsync(
            () => PlaceOrderButton.ClickAsync(),
            r => r.Url.EndsWith("/orders") && r.Request.Method == "POST");
        return response.Status;
    }

    /// <summary>
    /// Fills the form and double-clicks "Place order" (an impatient customer), then waits until the
    /// server has answered both order requests, so the order list is checked only afterwards.
    /// </summary>
    public async Task PlaceOrderWithDoubleClickAsync(string product, int quantity)
    {
        await page.GetByLabel("Product", new() { Exact = true }).FillAsync(product);
        await page.GetByLabel("Quantity", new() { Exact = true }).FillAsync(quantity.ToString());

        var answered = 0;
        var bothAnswered = new TaskCompletionSource();
        void OnResponse(object? _, IResponse r)
        {
            if (r.Request.Method == "POST" && r.Url.EndsWith("/orders") && Interlocked.Increment(ref answered) == 2)
                bothAnswered.TrySetResult();
        }
        page.Response += OnResponse;
        try
        {
            await PlaceOrderButton.DblClickAsync();
            await bothAnswered.Task.WaitAsync(TimeSpan.FromSeconds(15));
        }
        finally
        {
            page.Response -= OnResponse;
        }
    }

    public async Task LogOutAsync()
    {
        await OpenAsync();
        await LogoutLink.ClickAsync();
    }

    /// <summary>
    /// Sends an order straight to the API with this browser context's cookies, bypassing the UI,
    /// and returns the HTTP status: what the server still allows this session to do.
    /// </summary>
    public async Task<int> SendOrderRequestAsync(string product, int quantity)
    {
        var res = await page.APIRequest.PostAsync("/orders", new() { DataObject = new { productName = product, quantity } });
        return res.Status;
    }

    /// <summary>
    /// Answers this page's next order request in the browser with the given status; the server
    /// never sees it. Later requests go through. For outcomes the real stack cannot produce on
    /// demand (Pending, 503), which integration tests cover on the server side.
    /// </summary>
    public Task AnswerNextOrderAsync(int status)
    {
        var answered = false;
        return page.RouteAsync("**/orders", async route =>
        {
            if (!answered && route.Request.Method == "POST")
            {
                answered = true;
                await route.FulfillAsync(new() { Status = status, ContentType = "application/json", Body = "{}" });
            }
            else
                await route.FallbackAsync();
        });
    }

    /// <summary>Records the Idempotency-Key of every order request this page sends.</summary>
    public void RecordOrderAttemptKeys(List<string> into) => page.Request += (_, request) =>
    {
        if (request.Method == "POST" && request.Url.EndsWith("/orders") && request.Headers.TryGetValue("idempotency-key", out var key))
            into.Add(key);
    };

    /// <summary>Places the order in the form again, unchanged (no reload), and waits for the answer.</summary>
    public async Task<int> PlaceSameOrderAgainAsync()
    {
        var response = await page.RunAndWaitForResponseAsync(
            () => PlaceOrderButton.ClickAsync(),
            r => r.Url.EndsWith("/orders") && r.Request.Method == "POST");
        return response.Status;
    }
}
