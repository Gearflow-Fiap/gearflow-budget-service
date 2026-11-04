namespace GearFlow.Billing.Application.Common;

public sealed class InputValidationException(IReadOnlyList<Error> errors) : Exception("Input validation failed.")
{
    public IReadOnlyList<Error> Errors { get; } = errors;
}
