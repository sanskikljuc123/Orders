using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using ZiggyCreatures.Caching.Fusion;

/// <summary>Server-side login sessions: what logout relies on to revoke a cookie.</summary>
public class SessionStoreTests
{
    private readonly SessionStore _store = new(new FusionCache(new FusionCacheOptions()));   // real in-memory cache

    private static AuthenticationTicket Ticket(string user) => new(
        new ClaimsPrincipal(new ClaimsIdentity([new Claim("preferred_username", user)], "test")),
        new AuthenticationProperties { ExpiresUtc = DateTimeOffset.UtcNow.AddHours(1) },
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
    public async Task Unknown_key_finds_nothing()
        => Assert.Null(await _store.RetrieveAsync("not-a-session"));
}
