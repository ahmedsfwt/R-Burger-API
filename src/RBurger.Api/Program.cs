using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.IdentityModel.Tokens;
using RBurger.Api.Common;
using RBurger.Application.Common.Interfaces;
using RBurger.Application.DependencyInjection;
using RBurger.Infrastructure.Authentication;
using RBurger.Infrastructure.DependencyInjection;
using RBurger.Infrastructure.Realtime;

var builder = WebApplication.CreateBuilder(args);

// §8.1: "Hub route: /hubs/orders". Shared constant so the JWT query-string check below and
// the MapHub call further down can never drift apart.
const string OrdersHubPath = "/hubs/orders";

// ---- Application / Infrastructure ----
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);

// ---- API-layer services ----
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUserService, CurrentUserService>();
builder.Services.AddControllers();

// §5.5 / §7.0: minimal RFC 7807 ProblemDetails error envelope (approved decision #5).
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddProblemDetails();

// ---- JWT Authentication ----
// Day 16 CRITICAL FIX (root cause of the reported 103-failure 401 cascade): a prior version of
// this file read `builder.Configuration.GetSection("Jwt").Get<JwtSettings>()` into a local
// variable HERE, before `builder.Build()`, and captured that value in the AddJwtBearer options
// closure below. For WebApplicationFactory<Program> against a minimal-hosting Program.cs, a
// test factory's ConfigureWebHost -> ConfigureAppConfiguration override is only merged into
// the configuration at the `builder.Build()` call boundary - any code that reads
// `builder.Configuration` BEFORE that line runs against the pre-test-override snapshot (here:
// appsettings.json's blank Jwt:* values). The practical effect: the real app (which sets Jwt
// values via User Secrets before the process even starts) worked fine, but every
// WebApplicationFactory-based integration test built its JwtBearer signing key from an EMPTY
// string while TestJwtFactory correctly signed tokens with the test factory's injected test
// key - a guaranteed signature mismatch, so EVERY authenticated request failed validation and
// returned 401, regardless of role/claims, exactly matching the reported pattern (valid
// Admin/Customer/Driver JWTs all rejected with 401 instead of reaching role authorization).
//
// Fix: bind JwtSettings via the standard IOptions<T> pattern (services.Configure<JwtSettings>)
// and build TokenValidationParameters inside an AddOptions<JwtBearerOptions>().Configure<...>
// post-configure callback. Both are resolved LAZILY by the DI container - the first time
// JwtBearerHandler actually needs them, which is always after `builder.Build()` has returned
// and the host is fully composed, so they always see the final, fully-merged configuration
// (including any test-host override), never a pre-Build() snapshot.
builder.Services.Configure<JwtSettings>(builder.Configuration.GetSection(JwtSettings.SectionName));

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();

builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<Microsoft.Extensions.Options.IOptions<JwtSettings>>((bearerOptions, jwtOptions) =>
    {
        var jwtSettings = jwtOptions.Value;

        // Day 16 (Backend Parity Spec §4 - CRITICAL security fix, still enforced): no fallback
        // signing key. A prior version of this file silently substituted a 32-zero fallback
        // key whenever Jwt:Key was missing/empty - a real vulnerability (a deployment with a
        // misconfigured/missing secret would silently accept/issue tokens signed with a
        // publicly-known key instead of failing). This check now runs at the moment
        // JwtBearerOptions is actually resolved (post-Build(), fully-merged configuration),
        // which both fixes the timing bug above AND keeps the fail-fast guarantee intact.
        if (string.IsNullOrWhiteSpace(jwtSettings.Issuer)
            || string.IsNullOrWhiteSpace(jwtSettings.Audience)
            || string.IsNullOrWhiteSpace(jwtSettings.Key))
        {
            throw new InvalidOperationException(
                "JWT configuration is missing. 'Jwt:Issuer', 'Jwt:Audience', and 'Jwt:Key' must " +
                "all be set via configuration (User Secrets locally, Secrets Manager/Parameter " +
                "Store in production per §10.1) before the application can authenticate requests. " +
                "Refusing to fall back to a default/hardcoded signing key.");
        }

        // Keep claim types literal ("sub", "role") instead of ASP.NET Core's default inbound
        // claim-type remapping, to match §5.4's documented "role=Customer" claim exactly.
        bearerOptions.MapInboundClaims = false;

        // §8.1: "JWT passed as access_token query param on the SignalR handshake (standard
        // SignalR-over-JWT pattern)". Browsers' native WebSocket API cannot set custom
        // headers, so SignalR's JS/Flutter clients pass the token via ?access_token=... on
        // the handshake URL instead - this is the standard, documented workaround. Scoped
        // strictly to the Hub's own path so every other endpoint's normal
        // Authorization: Bearer header flow is completely unaffected. The token is only read
        // into TokenValidationParameters for validation - never logged or echoed back.
        bearerOptions.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"];
                var path = context.HttpContext.Request.Path;

                if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments(OrdersHubPath))
                {
                    context.Token = accessToken;
                }

                return Task.CompletedTask;
            }
        };

        bearerOptions.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtSettings.Issuer,
            ValidateAudience = true,
            ValidAudience = jwtSettings.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSettings.Key)),
            ValidateLifetime = true,
            // Small, intentional allowance for clock drift between the API and the client's
            // notion of "now" - not documented in v1.2 (a purely technical JWT-validation
            // parameter), kept deliberately short rather than .NET's 5-minute default so an
            // expired access token doesn't stay usable much longer than ExpiresInSeconds implies.
            ClockSkew = TimeSpan.FromSeconds(30),
            RoleClaimType = "role"
        };
    });

// §3.4/§4.5/§5.5: "Accept-Language middleware reads the header sent by React/Flutter and sets
// CultureInfo so validation/localized entity fields (Name_Ar/Name_En) resolve correctly."
// (Backend Parity Spec §14). Centralized, ASP.NET Core built-in RequestLocalization - does not
// touch existing DTOs/error codes: §7.0's ProblemDetails envelope already localizes purely by
// errorCode (client-side), which this does not change.
var supportedCultures = new[] { "ar", "en" };
builder.Services.Configure<Microsoft.AspNetCore.Builder.RequestLocalizationOptions>(options =>
{
    options.SetDefaultCulture("ar") // §2.5: "ar (default, RTL)"
        .AddSupportedCultures(supportedCultures)
        .AddSupportedUICultures(supportedCultures);
    // AcceptLanguageHeaderRequestCultureProvider is included by default - no custom provider
    // needed for the documented Accept-Language: ar|en contract.
});

// §5.4: role-based authorization ([Authorize(Roles = "Customer")] on CustomersController).
builder.Services.AddAuthorization();

// §5.5 (Backend Parity Spec §13): "Rate limiting (fixed window) on Auth and Payment
// endpoints." Two named fixed-window policies, applied via [EnableRateLimiting("...")] on
// AuthController/PaymentsController - no global policy, so every other documented endpoint is
// completely unaffected. Partitioned by client IP: both policies primarily protect
// pre-authentication or payment-initiation flows where a caller may not have a JWT yet.
var rateLimitSettings = builder.Configuration.GetSection(RateLimitSettings.SectionName).Get<RateLimitSettings>()
    ?? new RateLimitSettings();

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    options.AddPolicy("auth", context => RateLimitPartition.GetFixedWindowLimiter(
        partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        factory: _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = rateLimitSettings.AuthPermitLimit,
            Window = TimeSpan.FromSeconds(rateLimitSettings.AuthWindowSeconds),
            QueueLimit = 0
        }));

    options.AddPolicy("payment", context => RateLimitPartition.GetFixedWindowLimiter(
        partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        factory: _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = rateLimitSettings.PaymentPermitLimit,
            Window = TimeSpan.FromSeconds(rateLimitSettings.PaymentWindowSeconds),
            QueueLimit = 0
        }));
});

builder.Services.AddEndpointsApiExplorer();
object value = builder.Services.AddSwaggerGen();

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowFrontend", policy =>
        policy.WithOrigins(
            "https://rb-resturant.vercel.app",
            "https://rb-resturant-admin.vercel.app",
            "http://localhost:5173",
            "http://localhost:5174"
        )
        .AllowAnyMethod()
        .AllowAnyHeader());
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseExceptionHandler();

app.UseCors("AllowFrontend");

app.UseRequestLocalization();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();



app.MapControllers();

// §17 (Backend Parity Spec) - GET /health for ECS/ALB health checks and deployment smoke
// tests. Deliberately minimal (no DB round-trip): a health check that depends on a live DB
// connection can report the whole task unhealthy during a transient DB blip that the API
// process itself is fine to keep serving through, and ECS would kill/replace healthy tasks
// unnecessarily. This confirms the process is up and the DI container/middleware pipeline
// built successfully - no sensitive infrastructure details in the response.
app.MapGet("/health", () => Results.Ok(new { status = "healthy" }))
    .AllowAnonymous();

// §8.1: "Hub route: /hubs/orders". OrdersHub itself lives in Infrastructure (SignalR is an
// Infrastructure concern); this is purely endpoint-routing configuration, an API-Alayer concern.
app.MapHub<OrdersHub>(OrdersHubPath);

app.Run();

// Exposes the Program class for WebApplicationFactory<Program> in integration tests.
public partial class Program
{
}
