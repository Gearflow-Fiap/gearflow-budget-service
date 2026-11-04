using System.Security.Claims;
using System.Text.Encodings.Web;
using GearFlow.Billing.Application.Budgets;
using GearFlow.Billing.Application.Common;
using GearFlow.Billing.Application.Payments;
using GearFlow.Billing.Application.Ports.In;
using GearFlow.Billing.Application.Ports.Out;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.Extensions.Options;

namespace GearFlow.Billing.Api;

public sealed class JwtOptions
{
    public string Mode { get; set; } = "Authority";
    public string Authority { get; set; } = "";
    public string Issuer { get; set; } = "GearFlow.Api";
    public string Secret { get; set; } = "";
    public string Audience { get; set; } = "GearFlow.Client";
    public string[] ReadRoles { get; set; } = ["BillingAdmin", "BillingOperator"];
}
public sealed record ReviewRequest(string ApprovalToken, string? Reason = null);

public static class BillingEndpoints
{
    public static void MapBillingEndpoints(this WebApplication app)
    {
        app.MapGet("/api/budgets/{id:guid}", async (Guid id, IDispatcher dispatcher, ClaimsPrincipal user, IOptions<JwtOptions> jwt, CancellationToken ct) =>
        {
            var view = await dispatcher.Query(new GetBudgetByIdQuery(id), ct);
            return view is not null && CanRead(user, view.ServiceOrderId, jwt.Value) ? Results.Ok(view) : Results.NotFound();
        }).RequireAuthorization("BillingRead").WithName("GetBudgetById").Produces<BudgetView>().Produces(401).Produces(404);
        app.MapGet("/api/budgets/service-order/{serviceOrderId:guid}", async (Guid serviceOrderId, IDispatcher dispatcher, ClaimsPrincipal user, IOptions<JwtOptions> jwt, CancellationToken ct) =>
        {
            if (!CanRead(user, serviceOrderId, jwt.Value)) return Results.NotFound();
            var view = await dispatcher.Query(new GetBudgetByServiceOrderQuery(serviceOrderId), ct);
            return view is null ? Results.NotFound() : Results.Ok(view);
        }).RequireAuthorization("BillingRead").WithName("GetBudgetByServiceOrder").Produces<BudgetView>().Produces(401).Produces(404);
        app.MapGet("/api/payments/{id:guid}", async (Guid id, IDispatcher dispatcher, ClaimsPrincipal user, IOptions<JwtOptions> jwt, CancellationToken ct) =>
        {
            var view = await dispatcher.Query(new GetPaymentByIdQuery(id), ct);
            return view is not null && CanRead(user, view.ServiceOrderId, jwt.Value) ? Results.Ok(view) : Results.NotFound();
        }).RequireAuthorization("BillingRead").WithName("GetPaymentById").Produces<PaymentView>().Produces(401).Produces(404);
        app.MapGet("/api/payments/budget/{budgetId:guid}", async (Guid budgetId, IDispatcher dispatcher, ClaimsPrincipal user, IOptions<JwtOptions> jwt, CancellationToken ct) =>
        {
            var view = await dispatcher.Query(new GetPaymentByBudgetQuery(budgetId), ct);
            return view is not null && CanRead(user, view.ServiceOrderId, jwt.Value) ? Results.Ok(view) : Results.NotFound();
        }).RequireAuthorization("BillingRead").WithName("GetPaymentByBudget").Produces<PaymentView>().Produces(401).Produces(404);

        foreach (var decision in new[] { "approve", "reject" })
        {
            var approve = decision == "approve";
            app.MapGet($"/api/budgets/{{id:guid}}/{decision}", (Guid id, string token, HttpContext http, IDispatcher dispatcher,
                IApprovalTokenService tokens, IAntiforgery antiforgery, CancellationToken ct) => Confirmation(id, token, approve, http, dispatcher, tokens, antiforgery, ct))
                .AllowAnonymous().ExcludeFromDescription();
            app.MapPost($"/api/budgets/{{id:guid}}/{decision}", (Guid id, HttpContext http, IDispatcher dispatcher,
                IAntiforgery antiforgery, CancellationToken ct) => DecideForm(id, approve, http, dispatcher, antiforgery, ct))
                .AllowAnonymous().ExcludeFromDescription();
            app.MapPut($"/api/budgets/{{id:guid}}/{decision}", async (Guid id, ReviewRequest body, IDispatcher dispatcher, CancellationToken ct) =>
            {
                var result = approve ? await dispatcher.Send(new ApproveBudgetCommand(id, body.ApprovalToken), ct)
                    : await dispatcher.Send(new RejectBudgetCommand(id, body.ApprovalToken, body.Reason), ct);
                return Response(result);
            }).AllowAnonymous().WithName(approve ? "ApproveBudget" : "RejectBudget").Produces(204).ProducesProblem(403).ProducesProblem(409);
        }
    }
    public static bool CanRead(ClaimsPrincipal user, Guid serviceOrderId, JwtOptions options) =>
        user.Identity?.IsAuthenticated == true && (options.ReadRoles.Any(user.IsInRole)
            || user.Claims.Any(c => c.Type == "service_order_id" && Guid.TryParse(c.Value, out var id) && id == serviceOrderId));

    public static IResult Response(Result<Unit> result)
    {
        if (result.IsSuccess) return Results.NoContent();
        var error = result.Error!;
        var status = error.Code switch
        {
            "approval.forbidden" => 403, "budget.not_found" => 404,
            "budget.state" or "budget.expired" or "budget.conflict" => 409,
            _ => 400
        };
        return Results.Problem(statusCode: status, title: error.Message, extensions: new Dictionary<string, object?> { ["code"] = error.Code });
    }
    private static void PrivatePage(HttpContext http)
    {
        http.Response.Headers.CacheControl = "no-store"; http.Response.Headers["Referrer-Policy"] = "no-referrer";
        http.Response.Headers["Content-Security-Policy"] = "default-src 'none'; form-action 'self'; frame-ancestors 'none'; base-uri 'none'";
        http.Response.Headers["X-Content-Type-Options"] = "nosniff";
    }
    public static async Task<IResult> Confirmation(Guid id, string token, bool approve, HttpContext http, IDispatcher dispatcher,
        IApprovalTokenService tokens, IAntiforgery antiforgery, CancellationToken ct)
    {
        PrivatePage(http);
        var budget = await dispatcher.Query(new GetBudgetByIdQuery(id), ct);
        if (budget is null) return Results.NotFound();
        if (!tokens.IsValid(token, id, budget.ExpiresOn)) return Results.Problem(statusCode: 403, title: "Link inválido ou expirado.");
        var csrf = antiforgery.GetAndStoreTokens(http);
        var encoder = HtmlEncoder.Default;
        var action = approve ? "approve" : "reject"; var text = approve ? "aprovação" : "rejeição";
        var html = $"<!doctype html><html lang=\"pt-BR\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width\"><title>Confirmar {text}</title></head><body>"
            + $"<main><h1>Confirmar {text} do orçamento</h1><p>Orçamento: {id:D}</p><p>Total: R$ {budget.TotalAmount:F2}</p>"
            + $"<form method=\"post\" action=\"/api/budgets/{id:D}/{action}\"><input type=\"hidden\" name=\"token\" value=\"{encoder.Encode(token)}\">"
            + $"<input type=\"hidden\" name=\"{encoder.Encode(csrf.FormFieldName)}\" value=\"{encoder.Encode(csrf.RequestToken!)}\">"
            + (approve ? "" : "<label>Motivo (opcional): <textarea name=\"reason\" maxlength=\"500\"></textarea></label>")
            + $"<button type=\"submit\">Confirmar {text}</button></form></main></body></html>";
        return Results.Content(html, "text/html; charset=utf-8");
    }
    private static async Task<IResult> DecideForm(Guid id, bool approve, HttpContext http, IDispatcher dispatcher, IAntiforgery antiforgery, CancellationToken ct)
    {
        PrivatePage(http);
        try { await antiforgery.ValidateRequestAsync(http); }
        catch (AntiforgeryValidationException) { return Results.Problem(statusCode: 400, title: "Confirmação inválida. Abra o link novamente."); }
        var form = await http.Request.ReadFormAsync(ct);
        var result = approve ? await dispatcher.Send(new ApproveBudgetCommand(id, form["token"].ToString()), ct)
            : await dispatcher.Send(new RejectBudgetCommand(id, form["token"].ToString(), string.IsNullOrWhiteSpace(form["reason"]) ? null : form["reason"].ToString()), ct);
        if (!result.IsSuccess) return Response(result);
        return Results.Content("<!doctype html><html lang=\"pt-BR\"><meta charset=\"utf-8\"><title>Decisão registrada</title><h1>Decisão registrada</h1><p>Você pode fechar esta página.</p></html>", "text/html; charset=utf-8");
    }
}
