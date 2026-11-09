using FluentValidation.TestHelper;

public class CreateOrderValidatorTests
{
    private readonly CreateOrderValidator _validator = new();

    [Theory]
    [InlineData("Book", 1)]
    [InlineData("Book", 100)]
    [InlineData("Čokolada 🍫", 50)]
    public void Valid_order_has_no_errors(string product, int quantity)
    {
        var result = _validator.TestValidate(new CreateOrderRequest(product, quantity));

        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Product_name_of_exactly_50_characters_is_accepted()
    {
        var result = _validator.TestValidate(new CreateOrderRequest(new string('a', 50), 1));

        result.ShouldNotHaveAnyValidationErrors();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Missing_product_name_is_rejected(string? product)
    {
        var result = _validator.TestValidate(new CreateOrderRequest(product!, 1));

        var error = Assert.Single(result.Errors);
        Assert.Equal(nameof(CreateOrderRequest.ProductName), error.PropertyName);
        Assert.Equal("NotEmptyValidator", error.ErrorCode);
        Assert.Equal("'Product Name' must not be empty.", error.ErrorMessage);
    }

    [Fact]
    public void Product_name_longer_than_50_characters_is_rejected()
    {
        var result = _validator.TestValidate(new CreateOrderRequest(new string('a', 51), 1));

        var error = Assert.Single(result.Errors);
        Assert.Equal(nameof(CreateOrderRequest.ProductName), error.PropertyName);
        Assert.Equal("MaximumLengthValidator", error.ErrorCode);
        Assert.Equal("The length of 'Product Name' must be 50 characters or fewer. You entered 51 characters.", error.ErrorMessage);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    [InlineData(-5)]
    public void Quantity_outside_1_to_100_is_rejected(int quantity)
    {
        var result = _validator.TestValidate(new CreateOrderRequest("Book", quantity));

        var error = Assert.Single(result.Errors);
        Assert.Equal(nameof(CreateOrderRequest.Quantity), error.PropertyName);
        Assert.Equal("InclusiveBetweenValidator", error.ErrorCode);
        Assert.Equal($"'Quantity' must be between 1 and 100. You entered {quantity}.", error.ErrorMessage);
    }
}
