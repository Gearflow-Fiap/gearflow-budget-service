using GearFlow.Billing.Domain.Core;

namespace GearFlow.Billing.Domain.Budgets;

public enum BudgetItemType { Job, Part, Consumable }

public sealed record BudgetItem
{
    public Guid ReferenceId { get; }
    public BudgetItemType Type { get; }
    public string Description { get; }
    public Money UnitPrice { get; }
    public decimal Quantity { get; }
    public Money Subtotal => UnitPrice.Multiply(Quantity);

    public BudgetItem(Guid referenceId, BudgetItemType type, string description, Money unitPrice, decimal quantity)
    {
        ReferenceId = Ensure.Identity(referenceId);
        Ensure.That(Enum.IsDefined(type), "item.type", "Unknown item type.");
        Ensure.That(quantity > 0 && decimal.Round(quantity, 3) == quantity,
            "item.quantity", "Quantity must be positive with at most three decimals.");
        Type = type;
        Description = Ensure.Text(description, "description");
        UnitPrice = unitPrice ?? throw new ArgumentNullException(nameof(unitPrice));
        Quantity = quantity;
        _ = Subtotal;
    }
}
