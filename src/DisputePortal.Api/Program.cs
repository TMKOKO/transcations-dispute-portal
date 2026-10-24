using System.Text;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using DisputePortal.Api.Data;
using DisputePortal.Api.Middleware;
using DisputePortal.Api.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

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
    });
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "Transactions Dispute Portal API", Version = "v1" });
    c.IncludeXmlComments(Path.Combine(AppContext.BaseDirectory, "DisputePortal.Api.xml"));
    c.AddSecurityDefinition("Bearer", new Microsoft.OpenApi.Models.OpenApiSecurityScheme
    {
        Name         = "Authorization",
        Type         = Microsoft.OpenApi.Models.SecuritySchemeType.Http,
        Scheme       = "bearer",
        BearerFormat = "JWT",
        In           = Microsoft.OpenApi.Models.ParameterLocation.Header,
        Description  = "Paste the token returned by /api/auth/login or /api/auth/agent-login."
    });
    c.AddSecurityRequirement(new Microsoft.OpenApi.Models.OpenApiSecurityRequirement
    {
        {
            new Microsoft.OpenApi.Models.OpenApiSecurityScheme
            {
                Reference = new Microsoft.OpenApi.Models.OpenApiReference
                {
                    Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme,
                    Id   = "Bearer"
                }
            },
            Array.Empty<string>()
        }
    });
});

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

builder.Services.AddHealthChecks().AddDbContextCheck<AppDbContext>("database");

// The system clock, injected everywhere "now" matters (dispute window, timestamps, token expiry)
// so tests can pin it instead of racing the real one.
builder.Services.AddSingleton(TimeProvider.System);

builder.Services.Configure<DisputeOptions>(builder.Configuration.GetSection(DisputeOptions.SectionName));
builder.Services.AddSingleton<DisputeEligibility>();
builder.Services.AddScoped<IDisputeService, DisputeService>();
builder.Services.AddSingleton<ITokenService, JwtTokenService>();

// --- Authentication (JWT bearer) ---
var jwtKey = JwtSettings.RequireKey(builder.Configuration);

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer           = true,
            ValidIssuer              = JwtSettings.Issuer(builder.Configuration),
            ValidateAudience         = true,
            ValidAudience            = JwtSettings.Audience(builder.Configuration),
            ValidateIssuerSigningKey = true,
            IssuerSigningKey         = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
            ValidateLifetime         = true,
            ClockSkew                = TimeSpan.FromMinutes(1),
            RoleClaimType            = "role",
            NameClaimType            = "sub"
        };
    });
builder.Services.AddAuthorization();

// --- Rate limiting ---
// One global partitioned limiter rather than per-endpoint attributes, so a route added later is
// covered by default instead of being silently unprotected. Limits are configurable so the
// integration tests can exercise the 429 path without sending hundreds of requests.
var strictLimit  = builder.Configuration.GetValue("RateLimiting:AuthPermitsPerMinute", 10);
var generalLimit = builder.Configuration.GetValue("RateLimiting:GeneralPermitsPerMinute", 300);

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
    {
        var client = ClientKey(context);

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

// Behind a reverse proxy (nginx in compose, a load balancer in production) the socket address is
// the proxy, which would put every caller in one partition. The first X-Forwarded-For hop is the
// real client. Only trustworthy because the API is not reachable except through that proxy.
static string ClientKey(HttpContext context)
{
    var forwarded = context.Request.Headers["X-Forwarded-For"].ToString();
    if (!string.IsNullOrWhiteSpace(forwarded))
        return forwarded.Split(',')[0].Trim();

    return context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}

// Credential-guessing surface: both logins. CORS preflights are excluded — an OPTIONS would
// otherwise cost a permit and halve the budget for every browser caller.
static bool IsCredentialPath(HttpRequest request) =>
    request.Path.StartsWithSegments("/api/auth", StringComparison.OrdinalIgnoreCase)
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

// In compose the portal and API share an origin through nginx, so no CORS is needed. Origins are
// configurable for the case where the portal is hosted separately (e.g. S3 + CloudFront).
var corsOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(options =>
{
    options.AddPolicy("Portal", policy =>
        policy.WithOrigins(corsOrigins)
              .WithMethods("GET", "POST", "PATCH", "OPTIONS")
              .WithHeaders("Content-Type", "Accept", "Authorization", "Idempotency-Key")
              .WithExposedHeaders("Idempotent-Replayed"));
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.MigrateAsync();

    // Every seeded account shares one published demo password, so seeding must never run against
    // a real environment — even one whose tables happen to be empty.
    if (app.Environment.IsDevelopment())
        await DbSeeder.SeedAsync(db, scope.ServiceProvider.GetRequiredService<TimeProvider>());
}

// Outermost, so the headers land on error responses too.
app.UseMiddleware<SecurityHeadersMiddleware>();

// A malformed body that reaches an unexpected code path would otherwise surface as a bare 500
// with no correlation id — or a stack trace, had a container inherited Development.
app.UseExceptionHandler();
app.UseStatusCodePages();

app.UseCors("Portal");
app.UseRateLimiter();

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
