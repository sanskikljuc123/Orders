using FluentValidation;

public class CreateOrderValidator : AbstractValidator<CreateOrderRequest>
{
    public CreateOrderValidator()
    {
        // Explicit messages (same text as FluentValidation's English defaults): the defaults are
        // translated from the server's UI culture, so a non-English server would change the API
        // contract. These are returned to callers in the 400 response.
        RuleFor(x => x.ProductName)
            .NotEmpty().WithMessage("'{PropertyName}' must not be empty.")
            .MaximumLength(50).WithMessage("The length of '{PropertyName}' must be {MaxLength} characters or fewer. You entered {TotalLength} characters.");
        RuleFor(x => x.Quantity)
            .InclusiveBetween(1, 100).WithMessage("'{PropertyName}' must be between {From} and {To}. You entered {PropertyValue}.");
    }
}
