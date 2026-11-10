using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using ZiggyCreatures.Caching.Fusion;

/// <summary>Server-side login sessions: what logout relies on to revoke a cookie.</summary>
public class SessionStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 8, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private readonly SessionStore _store = new(new FusionCache(new FusionCacheOptions()), new FixedClock(Now));   // real in-memory cache

    private static AuthenticationTicket Ticket(string user, DateTimeOffset? expires = null, bool noExpiry = false) => new(
        new ClaimsPrincipal(new ClaimsIdentity([new Claim("preferred_username", user)], "test")),
        new AuthenticationProperties { ExpiresUtc = noExpiry ? null : expires ?? Now.AddHours(1) },
        "Cookies");

    [Fact]
    public async Task Stored_session_is_found_by_its_key()
    {
        var key = await _store.StoreAsync(Ticket("ana"));

        var found = await _store.RetrieveAsync(key);

        Assert.Equal("ana", found!.Principal.FindFirst("preferred_username")!.Value);
    }

    [Fact]
    public async Task Removed_session_is_gone_so_the_cookie_no_longer_authenticates()
    {
        var key = await _store.StoreAsync(Ticket("ana"));

        await _store.RemoveAsync(key);

        Assert.Null(await _store.RetrieveAsync(key));
    }

    [Fact]
    public async Task Every_login_gets_its_own_unguessable_key()
    {
        var first = await _store.StoreAsync(Ticket("ana"));
        var second = await _store.StoreAsync(Ticket("ana"));

        Assert.NotEqual(first, second);
        Assert.Equal(32, first.Length);   // 128-bit random Guid, "N" format
    }

    [Fact]
    public async Task Session_without_an_expiry_is_kept()
    {
        var key = await _store.StoreAsync(Ticket("ana", noExpiry: true));

        Assert.NotNull(await _store.RetrieveAsync(key));
    }

    [Fact]
    public async Task Already_expired_session_is_never_kept()
    {
        var key = await _store.StoreAsync(Ticket("ana", expires: Now.AddSeconds(-1)));

        Assert.Null(await _store.RetrieveAsync(key));
    }

    [Fact]
    public async Task Renewing_with_an_expired_ticket_removes_the_session()
    {
        var key = await _store.StoreAsync(Ticket("ana"));

        await _store.RenewAsync(key, Ticket("ana", expires: Now));

        Assert.Null(await _store.RetrieveAsync(key));
    }

    [Fact]
    public async Task Unknown_key_finds_nothing()
        => Assert.Null(await _store.RetrieveAsync("not-a-session"));
}
