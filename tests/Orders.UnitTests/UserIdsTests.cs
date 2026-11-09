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
}
