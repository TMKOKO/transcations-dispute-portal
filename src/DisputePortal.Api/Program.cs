using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using DisputePortal.Api.Auth;
using DisputePortal.Api.Data;
using DisputePortal.Api.Events;
using DisputePortal.Api.Middleware;
using DisputePortal.Api.Services;
using DisputePortal.Api.ViewModels;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers(o =>
    {
        // Validation error keys use the JSON (camelCase) names the client sent, not C# property names.
        o.ModelMetadataDetailsProviders.Add(new Microsoft.AspNetCore.Mvc.ModelBinding.Metadata.SystemTextJsonValidationMetadataProvider());
    })
    .AddJsonOptions(o =>
    {
        o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
        // Otherwise a bad enum value echoes the internal CLR type name back to the caller.
        o.AllowInputFormatterExceptionMessages = false;
    })
    .ConfigureApiBehaviorOptions(o =>
    {
        // Model-validation 400s are produced by MVC before any action runs, so they never reach
        // ProblemResults' failure log. Log them here, after trimming to one error per field and keying every field by its JSON name.
        // Field names only: the rejected values may be personal information.
        var defaultResponse = o.InvalidModelStateResponseFactory;
        o.InvalidModelStateResponseFactory = context =>
        {
            // Fail fast: each field reports only the first rule it broke ([Required] runs first),
            // not every rule, so the customer fixes one thing at a time.
            foreach (var entry in context.ModelState.Values)
                while (entry.Errors.Count > 1) entry.Errors.RemoveAt(entry.Errors.Count - 1);

            var response = defaultResponse(context);

            // Attribute errors are keyed by JSON name, but IValidatableObject results arrive under the
            // C# name ("Note"). Re-key those so the client sees the name it sent, "note".
            if (response is ObjectResult { Value: ValidationProblemDetails problem })
            {
                foreach (var key in problem.Errors.Keys.Where(k => k.Length > 0 && char.IsUpper(k[0])).ToList())
                {
                    var messages = problem.Errors[key];
                    problem.Errors.Remove(key);
                    problem.Errors.TryAdd(JsonNamingPolicy.CamelCase.ConvertName(key), messages);
                }
            }

            var http = context.HttpContext;
            var fields = response is ObjectResult { Value: ValidationProblemDetails p }
                ? p.Errors.Keys
                : context.ModelState.Where(e => e.Value?.Errors.Count > 0).Select(e => e.Key);
            http.RequestServices.GetRequiredService<ILoggerFactory>()
                .CreateLogger("DisputePortal.Api.Validation")
                .LogWarning("Request {Method} {Path} failed validation on {Fields} (trace {TraceId})",
                    http.Request.Method, http.Request.Path.Value, string.Join(", ", fields), http.TraceIdentifier);
            return response;
        };
    });

// Outside Development, one JSON object per line so Instana or any log collector gets CustomerId,
// Code, TraceId etc. as fields rather than text. Development keeps the readable console.
// Turn levels up per category with env vars, no rebuild, for example:
//   Logging__LogLevel__DisputePortal=Debug
//   Logging__LogLevel__Microsoft.EntityFrameworkCore.Database.Command=Information  (SQL + timings; values stay hidden)
if (!builder.Environment.IsDevelopment())
    builder.Logging.AddJsonConsole(o => o.IncludeScopes = true);
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "Transactions Dispute Portal API", Version = "v1" });
    // No security scheme: Swagger UI is same-origin, so after calling /api/auth/login the browser
    // sends the session cookie on every "Try it out" by itself.
    c.IncludeXmlComments(Path.Combine(AppContext.BaseDirectory, "DisputePortal.Api.xml"));
});

// --- Database: primary for writes, optional replica for reads ---
// Retries cover transient failures (a failover, a dropped connection) instead of turning them into
// 500s. A retried SaveChanges whose first attempt had in fact committed hits a unique index and is
// handled as a replay. Any explicit transaction must run inside CreateExecutionStrategy() (OutboxRelay).
var primaryConnection = builder.Configuration.GetConnectionString("DefaultConnection");
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(primaryConnection, npgsql => npgsql
        .EnableRetryOnFailure(maxRetryCount: 3, maxRetryDelay: TimeSpan.FromSeconds(2), errorCodesToAdd: null)
        .CommandTimeout(10)));

// The replica fails fast (one retry, 2 s to connect) because ReadStore falls back to the primary.
// Unset, reads use the primary and ReadStore skips the replica logic altogether.
var replicaConnection = builder.Configuration.GetConnectionString(ReadStore.ConnectionName);
builder.Services.AddDbContext<ReadDbContext>(options =>
    options.UseNpgsql(
            string.IsNullOrWhiteSpace(replicaConnection)
                ? primaryConnection
                : new Npgsql.NpgsqlConnectionStringBuilder(replicaConnection) { Timeout = 2 }.ConnectionString,
            npgsql => npgsql
                .EnableRetryOnFailure(maxRetryCount: 1, maxRetryDelay: TimeSpan.FromMilliseconds(500), errorCodesToAdd: null)
                .CommandTimeout(10))
        .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking));
builder.Services.AddSingleton<ReadYourWrites>();
builder.Services.AddScoped<ReadStore>();

// --- Sessions (Redis) ---
// abortConnect=false: the API starts while Redis is still coming up and reconnects on its own.
// Until then session commands throw, which SessionStoreUnavailableHandler turns into a 503.
var redisOptions = ConfigurationOptions.Parse(CustomerSessionOptions.RequireRedis(builder.Configuration));
redisOptions.AbortOnConnectFail = false;
builder.Services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(redisOptions));
builder.Services.Configure<CustomerSessionOptions>(builder.Configuration.GetSection(CustomerSessionOptions.SectionName));
builder.Services.AddSingleton<ISessionStore, RedisSessionStore>();
builder.Services.AddExceptionHandler<SessionStoreUnavailableHandler>();

var healthChecks = builder.Services.AddHealthChecks()
    .AddDbContextCheck<AppDbContext>("database")
    .AddCheck<RedisHealthCheck>("redis");
// Degraded, not Unhealthy: reads fall back to the primary without it.
if (!string.IsNullOrWhiteSpace(replicaConnection))
    healthChecks.AddDbContextCheck<ReadDbContext>("read-replica", failureStatus: HealthStatus.Degraded);

// --- Dispute events (Kafka) ---
// DisputeService always writes outbox rows. With Kafka on, OutboxPublisherService sends them and
// DecisionConsumer applies the back office's decisions; off (tests, plain dotnet run), they wait.
builder.Services.Configure<KafkaOptions>(builder.Configuration.GetSection(KafkaOptions.SectionName));
builder.Services.AddScoped<DecisionHandler>();
if (builder.Configuration.GetValue<bool>($"{KafkaOptions.SectionName}:{nameof(KafkaOptions.Enabled)}"))
{
    builder.Services.AddSingleton<IEventPublisher, KafkaEventPublisher>();
    builder.Services.AddScoped<OutboxRelay>();
    builder.Services.AddHostedService<OutboxPublisherService>();
    builder.Services.AddHostedService<DecisionConsumer>();
    healthChecks.AddCheck<OutboxHealthCheck>("outbox", failureStatus: HealthStatus.Degraded);
}

// The system clock, injected everywhere "now" matters (dispute window, timestamps, session cap)
// so tests can pin it instead of racing the real one.
builder.Services.AddSingleton(TimeProvider.System);

builder.Services.Configure<DisputeOptions>(builder.Configuration.GetSection(DisputeOptions.SectionName));
builder.Services.AddSingleton<DisputeEligibility>();
builder.Services.AddScoped<IDisputeService, DisputeService>();

// One ViewModel per controller: queries, logging and response mapping, so controllers stay HTTP-only.
builder.Services.AddScoped<AuthViewModel>();
builder.Services.AddScoped<MeViewModel>();
builder.Services.AddScoped<TransactionsViewModel>();
builder.Services.AddScoped<DisputesViewModel>();

// The demo agent (off unless Agent:Enabled): sends decisions to Kafka, never changes a dispute itself.
builder.Services.Configure<AgentOptions>(builder.Configuration.GetSection(AgentOptions.SectionName));
builder.Services.AddScoped<AgentDecisionService>();
builder.Services.AddScoped<AgentViewModel>();

// --- Authentication (server-side session cookie) ---
builder.Services.AddAuthentication(SessionAuthenticationHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, SessionAuthenticationHandler>(SessionAuthenticationHandler.SchemeName, null);
builder.Services.AddAuthorization();

// --- Rate limiting ---
// One global partitioned limiter rather than per-endpoint attributes, so a route added later is
// covered by default instead of being silently unprotected. Limits are configurable so the
// integration tests can exercise the 429 path without sending hundreds of requests.
var strictLimit  = builder.Configuration.GetValue("RateLimiting:AuthPermitsPerMinute", 10);
var generalLimit = builder.Configuration.GetValue("RateLimiting:GeneralPermitsPerMinute", 300);
var trustForwardedFor = builder.Configuration.GetValue("RateLimiting:TrustForwardedFor", false);

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
    {
        var client = ClientKey(context, trustForwardedFor);

        return IsCredentialPath(context.Request)
            ? RateLimitPartition.GetFixedWindowLimiter($"strict:{client}", _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = strictLimit,
                Window = TimeSpan.FromMinutes(1)
            })
            : RateLimitPartition.GetFixedWindowLimiter($"general:{client}", _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = generalLimit,
                Window = TimeSpan.FromMinutes(1)
            });
    });

    options.OnRejected = async (context, token) =>
    {
        // Rejected before any controller runs, so ProblemResults never sees it. Same wording as its
        // failure log so dashboards count 429s alongside every other code. No IP: it is personal data.
        var http = context.HttpContext;
        http.RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger("DisputePortal.Api.RateLimiting")
            .LogWarning("Request {Method} {Path} failed with {StatusCode} {Code}: {Limit} limit reached (trace {TraceId})",
                http.Request.Method, http.Request.Path.Value, StatusCodes.Status429TooManyRequests, "rate_limited",
                IsCredentialPath(http.Request) ? "auth" : "general", http.TraceIdentifier);

        context.HttpContext.Response.Headers.RetryAfter = "60";
        await context.HttpContext.Response.WriteAsJsonAsync(new
        {
            status = StatusCodes.Status429TooManyRequests,
            title  = "Too many requests",
            detail = "Too many requests. Try again in a minute.",
            code   = "rate_limited"
        }, token);
    };
});

// Behind a load balancer or CDN the socket address is the proxy, which would put every caller in one
// partition, so the first X-Forwarded-For hop is the real client. Opt-in: when the API is reachable
// directly (as in local compose), a caller could set the header to anything and get a fresh budget
// on every request. Enable only where a trusted proxy overwrites it.
static string ClientKey(HttpContext context, bool trustForwardedFor)
{
    var forwarded = context.Request.Headers["X-Forwarded-For"].ToString();
    if (trustForwardedFor && !string.IsNullOrWhiteSpace(forwarded))
        return forwarded.Split(',')[0].Trim();

    return context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}

// Credential-guessing surface: the login route only, so signing out never eats the login budget.
// CORS preflights are excluded — an OPTIONS would otherwise cost a permit and halve the budget for
// every browser caller.
static bool IsCredentialPath(HttpRequest request) =>
    request.Path.StartsWithSegments("/api/auth/login", StringComparison.OrdinalIgnoreCase)
    && !HttpMethods.IsOptions(request.Method);

// Returns RFC 7807 bodies from UseExceptionHandler below instead of a bare 500.
builder.Services.AddProblemDetails();

builder.Services.Configure<Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions>(o =>
{
    // The largest real body is a dispute with a 1000-character description.
    o.Limits.MaxRequestBodySize = 64 * 1024;

    // Kestrel writes "Server: Kestrel" below the middleware pipeline, so it cannot be stripped
    // from a response header collection — it has to be switched off here.
    o.AddServerHeader = false;
});

// The portal is a separate project served from its own origin, so its origin(s) must be listed.
// Config-driven: localhost in compose/Development, the real portal domain in production.
// AllowCredentials so the browser sends the session cookie. The cookie is SameSite=Strict, so the
// portal and API must also be same-site (share a registrable domain), not just CORS-allowed.
var corsOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(options =>
{
    options.AddPolicy("Portal", policy =>
        policy.WithOrigins(corsOrigins)
              .WithMethods("GET", "POST", "PATCH", "OPTIONS")
              .WithHeaders("Content-Type", "Accept", "Idempotency-Key")
              .WithExposedHeaders("Idempotent-Replayed")
              .AllowCredentials());
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

    var pending = (await db.Database.GetPendingMigrationsAsync()).ToList();
    if (pending.Count > 0)
        app.Logger.LogInformation("Applying {Count} database migration(s): {Migrations}", pending.Count, string.Join(", ", pending));
    await db.Database.MigrateAsync();
    app.Logger.LogInformation("Database schema is up to date");

    // Every seeded account shares one published demo password, so seeding must never run against
    // a real environment — even one whose tables happen to be empty.
    if (app.Environment.IsDevelopment())
    {
        var clock = scope.ServiceProvider.GetRequiredService<TimeProvider>();
        await DbSeeder.SeedAsync(db, clock, app.Logger);

        if (app.Configuration.GetValue<bool>($"{AgentOptions.SectionName}:{nameof(AgentOptions.Enabled)}"))
            await DbSeeder.SeedAgentAsync(db, clock, app.Logger);
    }
}

// Outermost, so the headers land on error responses too. LIFO
app.UseMiddleware<SecurityHeadersMiddleware>();

// A malformed body that reaches an unexpected code path would otherwise surface as a bare 500
// with no correlation id — or a stack trace, had a container inherited Development.
app.UseExceptionHandler();
app.UseStatusCodePages();

app.UseCors("Portal");
app.UseRateLimiter();
app.UseMiddleware<OriginCheckMiddleware>();

// Development only: it publishes every route and DTO schema.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "Dispute Portal API v1"));
}

app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapHealthChecks("/health");

app.Run();

// Exposed for WebApplicationFactory in the integration tests.
public partial class Program;
