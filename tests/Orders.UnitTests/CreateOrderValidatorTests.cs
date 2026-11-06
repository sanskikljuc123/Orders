using Xunit;

public class CreateOrderValidatorTests
{
    private readonly CreateOrderValidator _validator = new();

    [Theory]
    [InlineData("Book", 1)]
    [InlineData("Book", 100)]
    public void Valid_requests_pass(string name, int quantity)
        => Assert.True(_validator.Validate(new CreateOrderRequest(name, quantity)).IsValid);

    [Theory]
    [InlineData("", 1)]
    [InlineData("   ", 1)]
    [InlineData("Book", 0)]
    [InlineData("Book", 101)]
    [InlineData("Book", -5)]
    public void Invalid_requests_fail(string name, int quantity)
        => Assert.False(_validator.Validate(new CreateOrderRequest(name, quantity)).IsValid);

    [Fact]
    public void Product_name_longer_than_50_characters_fails()
        => Assert.False(_validator.Validate(new CreateOrderRequest(new string('x', 51), 1)).IsValid);
}