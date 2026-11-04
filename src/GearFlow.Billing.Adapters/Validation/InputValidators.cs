using FluentValidation;
using GearFlow.Billing.Application.Budgets;
using GearFlow.Billing.Application.Common;
using GearFlow.Billing.Application.Payments;
using GearFlow.Billing.Application.Ports.In;
using Microsoft.Extensions.DependencyInjection;

namespace GearFlow.Billing.Adapters.Validation;

public sealed class FluentCommandValidator<T>(IValidator<T> validator) : ICommandValidator<T>
{
    public async Task ValidateAsync(T command, CancellationToken ct)
    {
        var result = await validator.ValidateAsync(command, ct);
        if (!result.IsValid) throw new InputValidationException(result.Errors.Select(e => new Error("input.invalid", e.ErrorMessage)).ToArray());
    }
}

public static class InputValidators
{
    private static void Add<T>(IServiceCollection services, Action<InlineValidator<T>> configure)
    {
        var validator = new InlineValidator<T>(); configure(validator);
        services.AddSingleton<IValidator<T>>(validator); services.AddScoped<ICommandValidator<T>, FluentCommandValidator<T>>();
    }
    public static IServiceCollection AddInputValidators(this IServiceCollection services)
    {
        Add<GenerateBudgetCommand>(services, v =>
        {
            v.RuleFor(c => c.ServiceOrderId).NotEmpty(); v.RuleFor(c => c.Customer).NotNull();
            v.RuleFor(c => c.Items).NotEmpty().Must(i => i is not null && i.Count <= 1000); v.RuleFor(c => c.ExpiresOn.Offset).Equal(TimeSpan.Zero);
        });
        Add<RequestPaymentCommand>(services, v => { v.RuleFor(c => c.ServiceOrderId).NotEmpty(); v.RuleFor(c => c.BudgetId).NotEmpty(); v.RuleFor(c => c.Amount).NotNull(); });
        Add<ApproveBudgetCommand>(services, v => { v.RuleFor(c => c.BudgetId).NotEmpty(); v.RuleFor(c => c.ApprovalToken).NotEmpty().MaximumLength(100); });
        Add<RejectBudgetCommand>(services, v => { v.RuleFor(c => c.BudgetId).NotEmpty(); v.RuleFor(c => c.ApprovalToken).NotEmpty().MaximumLength(100); v.RuleFor(c => c.Reason).MaximumLength(500); });
        Add<CancelBudgetCommand>(services, v => { v.RuleFor(c => c.ServiceOrderId).NotEmpty(); v.RuleFor(c => c.BudgetId).NotEmpty(); v.RuleFor(c => c.Reason).NotEmpty().MaximumLength(500); });
        Add<RefundPaymentCommand>(services, v => { v.RuleFor(c => c.ServiceOrderId).NotEmpty(); v.RuleFor(c => c.BudgetId).NotEmpty(); v.RuleFor(c => c.PaymentId).NotEmpty(); v.RuleFor(c => c.Reason).NotEmpty().MaximumLength(500); });
        return services;
    }
}
