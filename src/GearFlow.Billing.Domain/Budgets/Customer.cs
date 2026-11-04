using System.Net.Mail;
using GearFlow.Billing.Domain.Core;

namespace GearFlow.Billing.Domain.Budgets;

public sealed record EmailAddress
{
    public string Value { get; }
    public EmailAddress(string value)
    {
        value = Ensure.Text(value, "email", 254);
        Ensure.That(MailAddress.TryCreate(value, out var parsed) && parsed.Address == value,
            "email.invalid", "Invalid email address.");
        var separator = value.LastIndexOf('@');
        // Preserve local-part case; DNS domain names are case insensitive.
        Value = value[..separator] + "@" + value[(separator + 1)..].ToLowerInvariant();
    }
}

public sealed record Customer
{
    public string Name { get; }
    public EmailAddress Email { get; }
    public Customer(string name, EmailAddress email)
    {
        Name = Ensure.Text(name, "customer name", 200);
        Email = email ?? throw new ArgumentNullException(nameof(email));
    }
}
