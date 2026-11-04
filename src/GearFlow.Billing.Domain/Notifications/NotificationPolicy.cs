using GearFlow.Billing.Domain.Budgets;
using GearFlow.Billing.Domain.Core;
using GearFlow.Billing.Domain.Payments;

namespace GearFlow.Billing.Domain.Notifications;

// Relevance depends on independent aggregates; this is a cross-aggregate domain policy.
public static class NotificationPolicy
{
    public static bool IsRelevant(Notification notification, Budget budget, Payment? payment, DateTimeOffset now)
    {
        Ensure.Utc(now);
        Ensure.That(notification.BudgetId == budget.Id && notification.ServiceOrderId == budget.ServiceOrderId
            && (payment is null || payment.BudgetId == budget.Id), "notification.reference", "Notification references do not match.");
        return notification.Template switch
        {
            "budget-proposal-v1" => budget.Status == BudgetStatus.Pending && now < budget.ExpiresOn,
            "payment-pix-v1" => payment?.Status == PaymentStatus.Pending && payment.QrCode is not null && payment.ExpiresOn > now,
            "payment-refunded-v1" => payment?.Status == PaymentStatus.Refunded,
            _ => throw new DomainException("notification.template", "Unknown notification template.")
        };
    }
}
