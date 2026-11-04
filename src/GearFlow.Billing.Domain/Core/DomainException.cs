namespace GearFlow.Billing.Domain.Core;

public sealed class DomainException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public static class Ensure
{
    public static void That(bool condition, string code, string message)
    {
        if (!condition) throw new DomainException(code, message);
    }

    public static Guid Identity(Guid id)
    {
        That(id != Guid.Empty, "identity.empty", "Identity must not be empty.");
        return id;
    }

    public static DateTimeOffset Utc(DateTimeOffset value)
    {
        That(value.Offset == TimeSpan.Zero, "date.utc", "Dates must use UTC.");
        return value;
    }

    public static string Text(string? value, string name, int maxLength = 500)
    {
        That(!string.IsNullOrWhiteSpace(value) && value.Length <= maxLength,
            "value.invalid", $"Invalid {name}.");
        return value!.Trim();
    }
}
