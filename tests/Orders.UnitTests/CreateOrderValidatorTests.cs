using System.Globalization;
using FluentValidation.Results;
using FluentValidation.TestHelper;

// Rules under test (src/Orders.Api/Program.cs):
//   ProductName: NotEmpty, MaximumLength(50)
//   Quantity:    InclusiveBetween(1, 100)
// These messages are returned to API callers (Results.ValidationProblem), so every invalid case
// asserts the exact property, rule (error code) and message.
public class CreateOrderValidatorTests
{
    private const string NotEmpty = "NotEmptyValidator";
    private const string MaximumLength = "MaximumLengthValidator";
    private const string InclusiveBetween = "InclusiveBetweenValidator";

    private readonly CreateOrderValidator _validator = new();

    // The API returns these messages to callers, so they must not depend on the server's culture
    // (FluentValidation's default messages are translated from CurrentUICulture). Every case runs
    // under a non-English culture and still expects the English contract text.
    private TestValidationResult<CreateOrderRequest> Validate(CreateOrderRequest request, string culture = "de-DE")
    {
        var (current, currentUi) = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
            return _validator.TestValidate(request);
        }
        finally
        {
            (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture) = (current, currentUi);
        }
    }

    private static void AssertError(ValidationFailure error, string property, string code, string message)
    {
        Assert.Equal(property, error.PropertyName);
        Assert.Equal(code, error.ErrorCode);
        Assert.Equal(message, error.ErrorMessage);
    }

    // ---------- valid orders ----------

    [Theory]
    [InlineData("Book", 1)]            // quantity lower bound
    [InlineData("Book", 100)]          // quantity upper bound
    [InlineData("B", 50)]              // single-character name
    [InlineData("Čokolada 🍫", 50)]    // non-ASCII name
    public void Valid_order_has_no_errors(string product, int quantity)
    {
        var result = Validate(new CreateOrderRequest(product, quantity));

        result.ShouldNotHaveAnyValidationErrors();
    }

    [Theory]
    [InlineData(49)]
    [InlineData(50)]
    public void Product_name_up_to_50_characters_is_accepted(int length)
    {
        var result = Validate(new CreateOrderRequest(new string('a', length), 1));

        result.ShouldNotHaveAnyValidationErrors();
    }

    // ---------- ProductName: NotEmpty ----------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("   ")]
    [InlineData("\t\r\n")]
    public void Missing_or_whitespace_product_name_is_rejected(string? product)
    {
        var result = Validate(new CreateOrderRequest(product!, 1));

        var error = Assert.Single(result.Errors);
        AssertError(error, nameof(CreateOrderRequest.ProductName), NotEmpty,
            "'Product Name' must not be empty.");
    }

    // ---------- ProductName: MaximumLength(50) ----------

    [Theory]
    [InlineData(51)]
    [InlineData(500)]
    public void Product_name_longer_than_50_characters_is_rejected(int length)
    {
        var result = Validate(new CreateOrderRequest(new string('a', length), 1));

        var error = Assert.Single(result.Errors);
        AssertError(error, nameof(CreateOrderRequest.ProductName), MaximumLength,
            $"The length of 'Product Name' must be 50 characters or fewer. You entered {length} characters.");
    }

    [Fact]
    public void Product_name_is_not_trimmed_so_surrounding_spaces_count_towards_the_limit()
    {
        var result = Validate(new CreateOrderRequest(" " + new string('a', 50), 1));

        var error = Assert.Single(result.Errors);
        AssertError(error, nameof(CreateOrderRequest.ProductName), MaximumLength,
            "The length of 'Product Name' must be 50 characters or fewer. You entered 51 characters.");
    }

    // Documents current behaviour: the limit counts UTF-16 code units, not visible characters.
    // An emoji outside the BMP is 2 units, so 25 emoji fit and 26 do not.
    [Fact]
    public void Product_name_length_counts_utf16_code_units_25_emoji_are_accepted()
    {
        var result = Validate(new CreateOrderRequest(string.Concat(Enumerable.Repeat("🍫", 25)), 1));

        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Product_name_length_counts_utf16_code_units_26_emoji_are_rejected()
    {
        var result = Validate(new CreateOrderRequest(string.Concat(Enumerable.Repeat("🍫", 26)), 1));

        var error = Assert.Single(result.Errors);
        AssertError(error, nameof(CreateOrderRequest.ProductName), MaximumLength,
            "The length of 'Product Name' must be 50 characters or fewer. You entered 52 characters.");
    }

    // Documents current behaviour: rules in one RuleFor chain do not stop at the first failure,
    // so a name of 51 spaces gets both the "empty" and the "too long" error.
    [Fact]
    public void Whitespace_only_name_over_50_characters_reports_both_empty_and_too_long()
    {
        var result = Validate(new CreateOrderRequest(new string(' ', 51), 1));

        Assert.Collection(result.Errors,
            e => AssertError(e, nameof(CreateOrderRequest.ProductName), NotEmpty,
                "'Product Name' must not be empty."),
            e => AssertError(e, nameof(CreateOrderRequest.ProductName), MaximumLength,
                "The length of 'Product Name' must be 50 characters or fewer. You entered 51 characters."));
    }

    // ---------- Quantity: InclusiveBetween(1, 100) ----------

    [Theory]
    [InlineData(0)]                  // lower bound - 1
    [InlineData(101)]                // upper bound + 1
    [InlineData(-5)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void Quantity_outside_1_to_100_is_rejected(int quantity)
    {
        var result = Validate(new CreateOrderRequest("Book", quantity));

        var error = Assert.Single(result.Errors);
        AssertError(error, nameof(CreateOrderRequest.Quantity), InclusiveBetween,
            $"'Quantity' must be between 1 and 100. You entered {quantity.ToString(CultureInfo.InvariantCulture)}.");
    }

    // ---------- several fields at once ----------

    [Fact]
    public void Every_invalid_field_is_reported_not_only_the_first()
    {
        var result = Validate(new CreateOrderRequest("", 0));

        Assert.Collection(result.Errors,
            e => AssertError(e, nameof(CreateOrderRequest.ProductName), NotEmpty,
                "'Product Name' must not be empty."),
            e => AssertError(e, nameof(CreateOrderRequest.Quantity), InclusiveBetween,
                "'Quantity' must be between 1 and 100. You entered 0."));
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    [InlineData("sv-SE")]
    [InlineData("ja-JP")]
    public void Messages_are_the_same_whatever_the_server_culture(string culture)
    {
        var result = Validate(new CreateOrderRequest("", 0), culture);

        Assert.Collection(result.Errors,
            e => AssertError(e, nameof(CreateOrderRequest.ProductName), NotEmpty, "'Product Name' must not be empty."),
            e => AssertError(e, nameof(CreateOrderRequest.Quantity), InclusiveBetween,
                "'Quantity' must be between 1 and 100. You entered 0."));
    }
}
