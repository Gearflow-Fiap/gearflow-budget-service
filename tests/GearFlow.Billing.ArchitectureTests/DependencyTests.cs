using System.Reflection;
using GearFlow.Billing.Application.Budgets;
using GearFlow.Billing.Domain.Budgets;
using GearFlow.Billing.Domain.Core;
using GearFlow.Billing.Domain.Notifications;
using GearFlow.Billing.Domain.Payments;

namespace GearFlow.Billing.ArchitectureTests;

public class DependencyTests
{
    [Fact]
    public void Domain_only_depends_on_runtime()
    {
        var references = typeof(Budget).Assembly.GetReferencedAssemblies().Select(a => a.Name!).ToArray();
        Assert.All(references, name => Assert.True(name == "netstandard" || name.StartsWith("System.", StringComparison.Ordinal), name));
    }

    [Fact]
    public void Application_only_depends_on_domain_and_runtime()
    {
        var references = typeof(GenerateBudgetCommand).Assembly.GetReferencedAssemblies().Select(a => a.Name!).ToArray();
        Assert.All(references, name => Assert.True(name is "GearFlow.Billing.Domain" or "netstandard" || name.StartsWith("System.", StringComparison.Ordinal), name));
    }

    [Theory]
    [InlineData(typeof(Budget))]
    [InlineData(typeof(Payment))]
    [InlineData(typeof(Notification))]
    [InlineData(typeof(BudgetItem))]
    [InlineData(typeof(Customer))]
    [InlineData(typeof(Money))]
    [InlineData(typeof(EmailAddress))]
    [InlineData(typeof(PaymentAttempt))]
    public void Model_exposes_no_public_setters_or_mutable_list(Type type)
    {
        Assert.DoesNotContain(type.GetProperties(), p => p.SetMethod?.IsPublic == true);
        Assert.DoesNotContain(type.GetFields(BindingFlags.Public | BindingFlags.Instance), f => !f.IsInitOnly);
        Assert.DoesNotContain(type.GetProperties(), p => p.PropertyType.IsGenericType &&
            (p.PropertyType.GetGenericTypeDefinition() == typeof(List<>) || p.PropertyType.GetGenericTypeDefinition() == typeof(IList<>)));
    }
}
