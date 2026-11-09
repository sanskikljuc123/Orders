using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using ZiggyCreatures.Caching.Fusion;

/// <summary>
/// Keeps login sessions on the server: the auth cookie only carries a random key. Logging out
/// removes the session, so a copy of the cookie taken before logout stops working (a self-contained
/// cookie would stay valid until it expires).
/// Limitation: FusionCache is in-memory here, so sessions do not survive an API restart and are
/// not shared between replicas. A distributed L2 cache (with a ticket serializer) fixes both.
/// </summary>
public sealed class SessionStore(IFusionCache cache) : ITicketStore
{
    private static readonly TimeSpan DefaultLifetime = TimeSpan.FromDays(14);   // cookie default ExpireTimeSpan

    private static string CacheKey(string key) => $"auth-session:{key}";

    public async Task<string> StoreAsync(AuthenticationTicket ticket)
    {
        var key = Guid.NewGuid().ToString("N");
        await RenewAsync(key, ticket);
        return key;
    }

    public async Task RenewAsync(string key, AuthenticationTicket ticket) =>
        await cache.SetAsync(CacheKey(key), ticket, new FusionCacheEntryOptions(Lifetime(ticket)));

    public async Task<AuthenticationTicket?> RetrieveAsync(string key) =>
        await cache.GetOrDefaultAsync<AuthenticationTicket>(CacheKey(key));

    public async Task RemoveAsync(string key) => await cache.RemoveAsync(CacheKey(key));

    private static TimeSpan Lifetime(AuthenticationTicket ticket)
    {
        var left = ticket.Properties.ExpiresUtc - DateTimeOffset.UtcNow;
        return left is { } l && l > TimeSpan.Zero ? l : DefaultLifetime;
    }
}
