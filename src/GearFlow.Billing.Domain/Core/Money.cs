namespace GearFlow.Billing.Domain.Core;

public sealed record Money
{
    public decimal Amount { get; }
    public string Currency { get; }

    public Money(decimal amount, string currency = "BRL")
    {
        Ensure.That(currency == "BRL", "money.currency", "Only BRL is supported.");
        Ensure.That(amount >= 0, "money.negative", "Amount must be nonnegative.");
        Amount = decimal.Round(amount, 2, MidpointRounding.AwayFromZero);
        Currency = currency;
    }

    public Money Multiply(decimal quantity)
    {
        Ensure.That(quantity > 0, "item.quantity", "Quantity must be positive.");
        return new Money(checked(Amount * quantity), Currency);
    }
}
