using System.Net;
using System.Net.Mail;
using GearFlow.Billing.Application.Ports.Out;
using Microsoft.Extensions.Options;

namespace GearFlow.Billing.Adapters.Email;

public sealed class EmailOptions
{
    public bool Enabled { get; set; }
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 1025;
    public string FromAddress { get; set; } = "billing@gearflow.local";
    public string FromName { get; set; } = "GearFlow";
    public string? Username { get; set; }
    public string? Password { get; set; }
    public bool EnableSsl { get; set; }
}

public sealed class SmtpEmailSender(IOptions<EmailOptions> options) : IEmailSender
{
    public async Task SendAsync(EmailMessage email, CancellationToken ct)
    {
        var settings = options.Value;
        using var client = new SmtpClient(settings.Host, settings.Port)
            { EnableSsl = settings.EnableSsl, UseDefaultCredentials = false, Timeout = 15000 };
        if (!string.IsNullOrEmpty(settings.Username)) client.Credentials = new NetworkCredential(settings.Username, settings.Password);
        using var message = new MailMessage { From = new MailAddress(settings.FromAddress, settings.FromName),
            Subject = email.Subject, Body = email.Html, IsBodyHtml = true };
        message.To.Add(new MailAddress(email.Recipient));
        if (email.MessageId is not null) message.Headers["Message-ID"] = email.MessageId;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(15));
        await client.SendMailAsync(message, deadline.Token);
    }
}
