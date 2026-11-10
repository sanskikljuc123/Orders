using System.Security.Claims;

public class UserIdsTests
{
    [Fact]
    public void Sub_claim_is_the_user_id()
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("sub", "kc-123"), new Claim(ClaimTypes.NameIdentifier, "other")], "test"));

        Assert.Equal("kc-123", UserIds.Get(user));
    }

    [Fact]
    public void NameIdentifier_is_used_when_sub_is_missing()
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "nid-1")], "test"));

        Assert.Equal("nid-1", UserIds.Get(user));
    }

    [Fact]
    public void Unauthenticated_user_is_anonymous()
    {
        Assert.Equal("anonymous", UserIds.Get(new ClaimsPrincipal(new ClaimsIdentity())));
    }

    // Name is what the UI shows as "Signed in as ..." (/me).

    [Fact]
    public void Name_comes_from_the_identity_name()
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("preferred_username", "ana"), new Claim(ClaimTypes.Name, "Ana Name")], "test"));

        Assert.Equal("Ana Name", UserIds.Name(user));
    }

    [Fact]
    public void Name_falls_back_to_preferred_username()
    {
        // NameClaimType set to something the token does not carry, so Identity.Name is null.
        var user = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("preferred_username", "ana")], "test", "missing-name-claim", ClaimTypes.Role));

        Assert.Equal("ana", UserIds.Name(user));
    }

    [Fact]
    public void Name_of_an_unauthenticated_user_is_anonymous()
        => Assert.Equal("anonymous", UserIds.Name(new ClaimsPrincipal(new ClaimsIdentity())));
}
