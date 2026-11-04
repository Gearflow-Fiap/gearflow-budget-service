using System.Globalization;
using System.Net;
using GearFlow.Billing.Adapters.Security;
using GearFlow.Billing.Application.Notifications;
using GearFlow.Billing.Application.Ports.Out;
using GearFlow.Billing.Domain.Budgets;
using GearFlow.Billing.Domain.Notifications;
using GearFlow.Billing.Domain.Payments;
using Microsoft.Extensions.Options;

namespace GearFlow.Billing.Adapters.Email;

public sealed class NotificationComposer(IApprovalTokenService tokens, IOptions<ApprovalOptions> options) : INotificationComposer
{
    public EmailMessage Compose(Notification notification, Budget budget, Payment? payment)
    {
        static string Encode(string value) => WebUtility.HtmlEncode(value);
        static string Price(decimal value) => value.ToString("C", CultureInfo.GetCultureInfo("pt-BR"));
        var greeting = $"<p>Olá, {Encode(budget.Customer.Name)}.</p>";
        string subject, content;
        switch (notification.Template)
        {
            case "budget-proposal-v1":
                var token = Uri.EscapeDataString(tokens.Create(budget.Id, budget.ExpiresOn));
                var url = options.Value.PublicBaseUrl.TrimEnd('/') + $"/api/budgets/{budget.Id:D}";
                subject = "Seu orçamento GearFlow";
                content = $"<p>Orçamento: {budget.Id:D}</p><ul>"
                    + string.Join("", budget.Items.Select(i => $"<li>{Encode(i.Description)} — {i.Quantity.ToString(CultureInfo.InvariantCulture)} × {Price(i.UnitPrice.Amount)} = {Price(i.Subtotal.Amount)}</li>"))
                    + $"</ul><p>Total: <strong>{Price(budget.Total.Amount)}</strong></p><p>Válido até {budget.ExpiresOn:dd/MM/yyyy HH:mm} UTC.</p>"
                    + $"<p><a href=\"{Encode(url + "/approve?token=" + token)}\">Revisar e aprovar</a> · <a href=\"{Encode(url + "/reject?token=" + token)}\">Revisar e rejeitar</a></p>";
                break;
            case "payment-pix-v1":
                subject = "Pix do seu orçamento GearFlow";
                content = $"<p>Valor: <strong>{Price(payment!.Amount.Amount)}</strong></p><p>Pix Copia e Cola:</p><pre>{Encode(payment.QrCode!)}</pre>"
                    + (payment.TicketUrl is null ? "" : $"<p><a href=\"{Encode(payment.TicketUrl)}\">Abrir Pix</a></p>")
                    + $"<p>Validade: {payment.ExpiresOn:dd/MM/yyyy HH:mm} UTC.</p>";
                break;
            case "payment-refunded-v1":
                subject = "Reembolso confirmado — GearFlow";
                content = $"<p>O reembolso integral de <strong>{Price(payment!.Amount.Amount)}</strong> foi confirmado.</p><p>Referência: {Encode(payment.RefundId!)}.</p>";
                break;
            default: throw new InvalidOperationException("Unknown notification template.");
        }
        var html = $"<!doctype html><html lang=\"pt-BR\"><head><meta charset=\"utf-8\"></head><body><h1>{Encode(subject)}</h1>{greeting}{content}<p>Equipe GearFlow</p></body></html>";
        return new(notification.Recipient.Value, subject, html, $"<{notification.Id:N}@billing.gearflow>");
    }
}
