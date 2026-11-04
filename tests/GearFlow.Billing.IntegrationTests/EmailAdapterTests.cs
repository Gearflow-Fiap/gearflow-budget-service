using System.Net;
using System.Net.Sockets;
using GearFlow.Billing.Adapters.Email;
using GearFlow.Billing.Adapters.Security;
using GearFlow.Billing.Application.Ports.Out;
using GearFlow.Billing.Domain.Budgets;
using GearFlow.Billing.Domain.Core;
using GearFlow.Billing.Domain.Notifications;
using Microsoft.Extensions.Options;

namespace GearFlow.Billing.IntegrationTests;

public class EmailAdapterTests
{
    private sealed class Tokens : IApprovalTokenService
    {
        public string Create(Guid budgetId, DateTimeOffset expiresOn) => "a-test-token";
        public bool IsValid(string token, Guid budgetId, DateTimeOffset expiresOn) => false;
    }
    [Fact]
    public void Html_template_encodes_customer_and_items_and_points_to_confirmation_page()
    {
        var now = DateTimeOffset.Parse("2026-11-03T12:00:00Z");
        var budget = new Budget(Guid.NewGuid(), Guid.NewGuid(), new Customer("<script>customer</script>", new EmailAddress("ana@example.com")),
            [new BudgetItem(Guid.NewGuid(), BudgetItemType.Job, "<img src=x onerror=alert(1)>", new Money(350), 1)], now, now.AddHours(1));
        var notification = new Notification(Guid.NewGuid(), budget.ServiceOrderId, budget.Id, "proposal", budget.Customer.Email, "budget-proposal-v1", now);
        var composer = new NotificationComposer(new Tokens(), Options.Create(new ApprovalOptions { PublicBaseUrl = "https://billing.example" }));
        var email = composer.Compose(notification, budget, null);
        Assert.DoesNotContain("<script>", email.Html); Assert.DoesNotContain("<img", email.Html);
        Assert.Contains("&lt;script&gt;", email.Html); Assert.Contains($"/api/budgets/{budget.Id:D}/approve?token=a-test-token", email.Html);
        Assert.Equal($"<{notification.Id:N}@billing.gearflow>", email.MessageId);
    }
    [Fact]
    public async Task Smtp_adapter_sends_HTML_and_stable_message_ID_to_loopback_server()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var received = Capture();
            var sender = new SmtpEmailSender(Options.Create(new EmailOptions { Host = "127.0.0.1", Port = port, FromAddress = "billing@example.com" }));
            await sender.SendAsync(new EmailMessage("ana@example.com", "Test message", "<p>Test message</p>", "<stable-test@example.com>"), deadline.Token);
            var body = await received;
            Assert.Contains("stable-test@example.com", body); Assert.Contains("text/html", body, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("ana@example.com", body);
        }
        finally { listener.Stop(); }

        async Task<string> Capture()
        {
            using var connection = await listener.AcceptTcpClientAsync(deadline.Token);
            await using var stream = connection.GetStream(); using var reader = new StreamReader(stream);
            await using var writer = new StreamWriter(stream) { AutoFlush = true, NewLine = "\r\n" };
            await writer.WriteLineAsync("220 localhost SMTP");
            while (await reader.ReadLineAsync(deadline.Token) is { } line)
            {
                if (line.StartsWith("EHLO", StringComparison.Ordinal)) await writer.WriteLineAsync("250 localhost");
                else if (line.StartsWith("MAIL", StringComparison.Ordinal) || line.StartsWith("RCPT", StringComparison.Ordinal)) await writer.WriteLineAsync("250 OK");
                else if (line == "DATA")
                {
                    await writer.WriteLineAsync("354 End with dot"); var content = new List<string>();
                    while (await reader.ReadLineAsync(deadline.Token) is { } data && data != ".") content.Add(data);
                    await writer.WriteLineAsync("250 Accepted"); return string.Join("\n", content);
                }
                else if (line == "QUIT") { await writer.WriteLineAsync("221 Bye"); break; }
                else await writer.WriteLineAsync("250 OK");
            }
            throw new IOException("SMTP body was not received.");
        }
    }
}
