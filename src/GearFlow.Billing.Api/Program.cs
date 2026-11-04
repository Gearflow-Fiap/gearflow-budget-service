using GearFlow.Billing.Adapters;
using GearFlow.Billing.Api;
using GearFlow.Billing.Adapters.Messaging;
using GearFlow.Billing.Adapters.Operations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using Microsoft.OpenApi;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(options => options.IncludeScopes = true);
builder.Services.AddBillingCore(builder.Configuration);
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme { Type = SecuritySchemeType.Http, Scheme = "bearer", BearerFormat = "JWT" });
    options.AddSecurityRequirement(document => new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("Bearer", document)] = [] });
    options.OperationFilter<AnonymousSwaggerOperationFilter>();
});
builder.Services.AddBillingObservability(builder.Configuration, builder.Logging);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 64 * 1024);
var paymentIntegration = builder.Configuration.GetValue<bool>("MercadoPago:Enabled");
if (paymentIntegration) builder.Services.AddMercadoPagoIntegration(builder.Configuration, builder.Environment);
var businessHttp = builder.Configuration.GetValue<bool>("BillingHttp:Enabled");
if (businessHttp)
{
    if (!paymentIntegration) throw new InvalidOperationException("BillingHttp requires persistent integration configuration.");
    var jwt = builder.Configuration.GetSection("Jwt").Get<JwtOptions>() ?? new();
    var localIssuer = builder.Environment.IsDevelopment() && Uri.TryCreate(jwt.Authority, UriKind.Absolute, out var local) && local.IsLoopback;
    builder.Services.AddOptions<JwtOptions>().Bind(builder.Configuration.GetSection("Jwt"))
        .Validate(x => !string.IsNullOrWhiteSpace(x.Audience) && x.ReadRoles.Length > 0
            && (x.Mode == "Hmac" ? Encoding.UTF8.GetByteCount(x.Secret) >= 32 && !string.IsNullOrWhiteSpace(x.Issuer)
                : x.Mode == "Authority" && Uri.TryCreate(x.Authority, UriKind.Absolute, out var uri)
                    && string.IsNullOrEmpty(uri.UserInfo) && (uri.Scheme == "https" || (localIssuer && uri.Scheme == "http"))),
            "JWT requires valid Authority mode or Hmac mode with external secret, issuer and audience.").ValidateOnStart();
    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
    {
        if (jwt.Mode == "Authority") options.Authority = jwt.Authority;
        options.Audience = jwt.Audience; options.RequireHttpsMetadata = !localIssuer;
        options.MapInboundClaims = false; options.IncludeErrorDetails = false;
        options.TokenValidationParameters = new TokenValidationParameters { ValidateIssuer = true, ValidateAudience = true,
            ValidateIssuerSigningKey = true, ValidateLifetime = true, RequireSignedTokens = true, RequireExpirationTime = true,
            ClockSkew = TimeSpan.FromSeconds(30), RoleClaimType = "role" };
        if (jwt.Mode == "Hmac")
        {
            options.TokenValidationParameters.ValidIssuer = jwt.Issuer;
            options.TokenValidationParameters.IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Secret));
            options.TokenValidationParameters.ValidAlgorithms = [SecurityAlgorithms.HmacSha256];
        }
    });
    builder.Services.AddAuthorization(options => options.AddPolicy("BillingRead", policy => policy.RequireAuthenticatedUser()
        .RequireAssertion(ctx => jwt.ReadRoles.Any(ctx.User.IsInRole) || ctx.User.HasClaim(c => c.Type == "service_order_id"))));
    var keyPath = builder.Configuration["DataProtection:KeyPath"];
    if (string.IsNullOrWhiteSpace(keyPath))
    {
        if (!builder.Environment.IsDevelopment()) throw new InvalidOperationException("Shared DataProtection:KeyPath is required outside Development.");
        keyPath = Path.Combine(builder.Environment.ContentRootPath, ".data", "keys");
    }
    Directory.CreateDirectory(keyPath);
    builder.Services.AddDataProtection().SetApplicationName("GearFlow.Billing").PersistKeysToFileSystem(new DirectoryInfo(keyPath));
    builder.Services.AddAntiforgery(options => { options.Cookie.HttpOnly = true; options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always; });
}
var app = builder.Build();
if (builder.Configuration.GetValue<bool>("Operations:ReplayDlq"))
{
    using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(1));
    await app.Services.GetRequiredService<DlqReplay>().ReplayOneAsync(builder.Configuration["Operations:Actor"] ?? "", deadline.Token);
    return;
}
app.UseExceptionHandler();
if (businessHttp) { app.UseAuthentication(); app.UseAuthorization(); app.UseAntiforgery(); app.MapBillingEndpoints(); }
if (app.Environment.IsDevelopment()) { app.UseSwagger(); app.UseSwaggerUI(); }
app.MapGet("/health/live", () => Results.Ok(new { status = "alive" }));
if (paymentIntegration && !builder.Configuration.GetValue<bool>("MercadoPago:UseMock") && builder.Configuration.GetValue("MercadoPago:WebhookEnabled", true))
    app.MapPost("/api/payments/webhook", PaymentWebhookEndpoint.Receive);
app.MapGet("/health/ready", async (IServiceProvider services, CancellationToken ct) =>
    paymentIntegration && await services.GetRequiredService<Readiness>().CheckAsync(ct)
        ? Results.Ok(new { status = "ready" }) : Results.Problem(statusCode: 503, title: "Dependencies, topology or workers unavailable."));
app.Run();

public partial class Program;

