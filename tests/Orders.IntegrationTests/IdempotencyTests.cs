using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Double submit through the real HTTP pipeline and PostgreSQL: the same Idempotency-Key must give
/// the same order, one row and one charge. Needs Docker.
/// </summary>
[Collection("Stack")]
public class IdempotencyTests
{
    private readonly StackFixture _fixture;

    public IdempotencyTests(StackFixture fixture)
    {
        _fixture = fixture;
        _fixture.ResetPayment(approve: true);
    }

    private static HttpRequestMessage Post(CreateOrderRequest body, string? key)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/orders") { Content = JsonContent.Create(body) };
        if (key is not null) req.Headers.Add("Idempotency-Key", key);
        return req;
    }

    [Fact]
    public async Task Same_key_twice_returns_the_same_order_stores_one_row_and_charges_once()
    {
        var ana = await _fixture.CreateClientAsAsync("ana");
        var key = Guid.NewGuid().ToString();
        var product = $"Book-{Guid.NewGuid():N}";

        var first = await ana.SendAsync(Post(new CreateOrderRequest(product, 2), key));
        var again = await ana.SendAsync(Post(new CreateOrderRequest(product, 2), key));

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, again.StatusCode);
        Assert.False(first.Headers.Contains("Idempotent-Replayed"));
        Assert.Equal("true", again.Headers.GetValues("Idempotent-Replayed").Single());
        var a = (await first.Content.ReadFromJsonAsync<Order>())!;
        var b = (await again.Content.ReadFromJsonAsync<Order>())!;
        Assert.Equal(a.Id, b.Id);
        Assert.Equal(1, await RowsWithProductAsync(product));
        A.CallTo(() => _fixture.Payment.ChargeAsync(20m, A<Guid>._)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task Same_key_for_a_different_order_returns_422_and_is_not_charged_or_stored()
    {
        var ana = await _fixture.CreateClientAsAsync("ana");
        var key = Guid.NewGuid().ToString();
        var product = $"Book-{Guid.NewGuid():N}";
        await ana.SendAsync(Post(new CreateOrderRequest(product, 1), key));

        var reused = await ana.SendAsync(Post(new CreateOrderRequest(product, 5), key));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, reused.StatusCode);
        var errors = (await reused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
        Assert.Equal("'Idempotency-Key' was already used for a different order.",
            errors.GetProperty("Idempotency-Key")[0].GetString());   // under the header's name, so clients can map it
        Assert.False(reused.Headers.Contains("Idempotent-Replayed"));
        A.CallTo(() => _fixture.Payment.ChargeAsync(A<decimal>._, A<Guid>._)).MustHaveHappenedOnceExactly();   // the first order only
        Assert.Equal(1, await RowsWithProductAsync(product));
    }

    [Fact]
    public async Task Concurrent_requests_with_the_same_key_through_the_API_charge_once()
    {
        // The unit test builds its own lock; this one goes through HTTP and the app's DI, so it
        // also fails if the lock is not one shared instance (e.g. registered per request).
        var ana = await _fixture.CreateClientAsAsync("ana");
        var key = Guid.NewGuid().ToString();
        var product = $"Book-{Guid.NewGuid():N}";
        var charging = new TaskCompletionSource();
        A.CallTo(() => _fixture.Payment.ChargeAsync(A<decimal>._, A<Guid>._)).ReturnsLazily(async () => { await charging.Task; return true; });
        var locks = _fixture.Services.GetRequiredService<IdempotencyLocks>();
        var lockKey = $"{StackFixture.SubjectOf(ana)}\n{key}";

        var requests = Enumerable.Range(0, 3).Select(_ => ana.SendAsync(Post(new CreateOrderRequest(product, 1), key))).ToList();
        // All three hold or wait for the same lock before the charge is released (no fixed sleep).
        var allQueued = SpinWait.SpinUntil(() => locks.UsersOf(lockKey) == 3, TimeSpan.FromSeconds(10));
        charging.SetResult();
        var responses = await Task.WhenAll(requests);

        A.CallTo(() => _fixture.Payment.ChargeAsync(A<decimal>._, A<Guid>._)).MustHaveHappenedOnceExactly();
        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        var ids = await Task.WhenAll(responses.Select(async r => (await r.Content.ReadFromJsonAsync<Order>())!.Id));
        Assert.Single(ids.Distinct());
        Assert.Equal(1, await RowsWithProductAsync(product));
        Assert.True(allQueued, "all three requests should have waited on the same shared lock");
    }

    [Fact]
    public async Task Without_a_key_two_posts_are_two_orders()
    {
        var ana = await _fixture.CreateClientAsAsync("ana");
        var product = $"Book-{Guid.NewGuid():N}";

        await ana.SendAsync(Post(new CreateOrderRequest(product, 1), null));
        await ana.SendAsync(Post(new CreateOrderRequest(product, 1), null));

        Assert.Equal(2, await RowsWithProductAsync(product));
    }

    [Fact]
    public async Task Key_is_not_returned_to_the_client()
    {
        var ana = await _fixture.CreateClientAsAsync("ana");

        var key = $"secret-{Guid.NewGuid():N}";

        var res = await ana.SendAsync(Post(new CreateOrderRequest("Book", 1), key));
        var body = await res.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Created, res.StatusCode);   // a real order, not an error page
        Assert.DoesNotContain(key, body);                        // the value, under any field name
        Assert.DoesNotContain("idempotency", body.ToLowerInvariant());
    }

    [Theory]
    [InlineData(" ")]
    [InlineData("\t")]
    public async Task Blank_key_is_rejected(string key)
    {
        var ana = await _fixture.CreateClientAsAsync("ana");

        var res = await ana.SendAsync(Post(new CreateOrderRequest("Book", 1), key));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var problem = await res.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Http.HttpValidationProblemDetails>();
        Assert.Equal(["'Idempotency-Key' must be 1 to 100 characters."], problem!.Errors["Idempotency-Key"]);
        A.CallTo(() => _fixture.Payment.ChargeAsync(A<decimal>._, A<Guid>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task Key_of_exactly_100_characters_is_accepted()
    {
        var ana = await _fixture.CreateClientAsAsync("ana");

        var res = await ana.SendAsync(Post(new CreateOrderRequest("Book", 1), Guid.NewGuid().ToString("N").PadRight(100, 'k')));

        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
    }

    [Fact]
    public async Task Key_longer_than_100_characters_is_rejected_without_charging()
    {
        var ana = await _fixture.CreateClientAsAsync("ana");

        var res = await ana.SendAsync(Post(new CreateOrderRequest("Book", 1), new string('k', 101)));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("'Idempotency-Key' must be 1 to 100 characters.", await res.Content.ReadAsStringAsync());
        A.CallTo(() => _fixture.Payment.ChargeAsync(A<decimal>._, A<Guid>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task Database_refuses_a_second_order_with_the_same_user_and_key()
    {
        // Backstop for API replicas that do not share the in-process lock.
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrdersDbContext>();
        var key = Guid.NewGuid().ToString();
        db.Orders.Add(new Order(Guid.NewGuid(), "Book", 1, "Paid", "ana-id", DateTimeOffset.UtcNow, key));
        await db.SaveChangesAsync();

        db.Orders.Add(new Order(Guid.NewGuid(), "Book", 1, "Paid", "ana-id", DateTimeOffset.UtcNow, key));

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    private async Task<int> RowsWithProductAsync(string product)
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrdersDbContext>();
        return await db.Orders.CountAsync(o => o.ProductName == product);
    }
}
