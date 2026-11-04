using GearFlow.Billing.Application.Common;
using GearFlow.Billing.Domain.Core;
using Microsoft.AspNetCore.Diagnostics;

namespace GearFlow.Billing.Api;

public sealed class ApiExceptionHandler(ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken ct)
    {
        var (status, code, title) = exception switch
        {
            ConcurrencyException => (409, "state.concurrent", "State changed. Retry the request."),
            DuplicateException => (409, "state.duplicate", "The operation conflicts with an existing record."),
            DomainException domain => (409, domain.Code, domain.Message),
            InputValidationException => (400, "input.invalid", "Invalid request input."),
            BadHttpRequestException => (400, "input.invalid", "Invalid request input."),
            _ => (500, "server.error", "The request could not be completed.")
        };
        logger.LogWarning("Request failed: {Code}", code);
        await Results.Problem(statusCode: status, title: title,
            extensions: new Dictionary<string, object?> { ["code"] = code, ["traceId"] = context.TraceIdentifier }).ExecuteAsync(context);
        return true;
    }
}
