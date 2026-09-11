using Npgsql;
using Wms.Api;
using Wms.Api.Administration;
using Wms.Api.Authentication;
using Wms.Api.Receiving;
using Wms.Api.Reporting;
using Wms.Api.Sync;
using Wms.Api.Work;
using Wms.Modules.Identity.Contracts;
using Wms.Modules.Identity.Infrastructure;
using Wms.Modules.Inbound.Application;
using Wms.Modules.Inbound.Contracts;
using Wms.Modules.Inbound.Infrastructure;
using Wms.Modules.Inventory.Contracts;
using Wms.Modules.Inventory.Infrastructure;
using Wms.Modules.Platform.Contracts;
using Wms.Modules.Platform.Infrastructure;
using Wms.Modules.Tasks.Application;
using Wms.Modules.Tasks.Contracts;
using Wms.Modules.Tasks.Infrastructure;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

string connectionString = builder.Configuration.GetConnectionString("Wms")
    ?? throw new InvalidOperationException(
        "ConnectionStrings:Wms is not configured. The API must not start without a "
        + "database — a process that starts and then fails every request is harder to "
        + "diagnose than one that refuses to start.");

builder.Services.AddSingleton(_ => new NpgsqlDataSourceBuilder(connectionString).Build());

builder.Services.AddSingleton<IBusinessCalendar, BusinessCalendar>();
builder.Services.AddSingleton<IStockLedger, StockLedger>();
builder.Services.AddSingleton<IIdempotencyStore, IdempotencyStore>();
builder.Services.AddSingleton<IOutbox, Outbox>();
builder.Services.AddSingleton<ConfirmReceiptFactHandler>();
builder.Services.AddSingleton<ConfirmPutawayFactHandler>();
builder.Services.AddSingleton<FactHandlers>();
builder.Services.AddSingleton<ReceiptService>();
builder.Services.AddSingleton<LeaseService>();

// Read models — one per module, composed by the dashboard rather than joined
// across module tables in a single query (§1.3, the boundary rule).
builder.Services.AddSingleton<IInventoryReadModel, InventoryReadModel>();
builder.Services.AddSingleton<IInboundReadModel, InboundReadModel>();
builder.Services.AddSingleton<ITaskReadModel, TaskReadModel>();
builder.Services.AddSingleton<IUserDirectory, UserDirectory>();

// Authentication is not built yet (§6.1, Identity module). Development gets a
// header-driven stand-in so the fact path and admin screens can be exercised
// against the REAL permission model; every other environment gets a resolver
// that authenticates nobody, so the API fails closed rather than open.
// Tracked in docs/shortcuts.md.
//
// TWO independent conditions, so one stray environment variable copied out of
// a development compose file cannot turn header-trust on by itself. The
// resolver enforces both again in its own constructor — this is the outer of
// two gates, not the only one.
// Real authentication is now the default. The development header remains
// available for a moment longer so a developer can drive the API without
// seeding credentials, but it is no longer what happens when nothing is
// configured — that is the difference between a shortcut and a hole.
string signingKey = builder.Configuration["Wms:Auth:SigningKey"]
    ?? throw new InvalidOperationException(
        "Wms:Auth:SigningKey is not configured. The API must not start without one: "
        + "a generated-at-startup key would silently invalidate every token on restart "
        + "and cannot be shared across replicas.");

// appsettings.Development.json carries a known literal so a developer can run
// the API locally without provisioning a secret first. That value is
// committed, so it must be structurally inert anywhere it is not supposed to
// be — the same two-independent-conditions shape as DevelopmentPrincipalResolver
// below, applied to a value instead of a resolver. Without this, the only
// thing keeping a forgeable signing key out of a real deployment would be
// "nobody copies appsettings.Development.json into a Dockerfile," which is
// not a control.
const string developmentOnlySigningKey =
    "development-only-signing-key-do-not-use-in-prod";

if (signingKey == developmentOnlySigningKey && !builder.Environment.IsDevelopment())
{
    throw new InvalidOperationException(
        "Wms:Auth:SigningKey is set to the known Development-only placeholder outside "
        + "the Development environment. Every token issued with it is forgeable by "
        + "anyone who has read this repository — configure a real, deploy-time secret.");
}

builder.Services.AddSingleton<ITokenIssuer>(
    _ => new TokenIssuer(signingKey, builder.Configuration["Wms:Auth:Issuer"] ?? "wms"));
builder.Services.AddSingleton<IPasswordHasher, Argon2PasswordHasher>();
builder.Services.AddSingleton<IAuthenticationService, AuthenticationService>();

bool allowDevelopmentPrincipal =
    builder.Environment.IsDevelopment()
    && builder.Configuration.GetValue("Wms:Auth:AllowDevelopmentHeaderPrincipal", false);

if (allowDevelopmentPrincipal)
{
    builder.Services.AddSingleton<IOperatorPrincipalResolver>(services =>
        new DevelopmentPrincipalResolver(
            services.GetRequiredService<NpgsqlDataSource>(),
            builder.Environment.EnvironmentName,
            explicitlyEnabled: true));
}
else
{
    builder.Services.AddSingleton<IOperatorPrincipalResolver, TokenPrincipalResolver>();
}

builder.Services.AddProblemDetails();

// OpenAPI document and Swagger UI (§3.6). The gate is configuration rather
// than the environment name, because §3.6 requires enabling the console on a
// specific deployment to be a config change — not a rebuild, and not a side
// effect of how ASPNETCORE_ENVIRONMENT happens to be set.
bool swaggerEnabled = builder.Configuration.GetValue("Wms:Swagger:Enabled", false);
if (swaggerEnabled)
{
    builder.Services.AddOpenApi();
}

WebApplication app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

if (swaggerEnabled)
{
    app.MapOpenApi();
    app.MapGet("/", () => Results.Redirect("/openapi/v1.json"))
        .ExcludeFromDescription();
}

// Said once, loudly, at startup. Which resolver is live decides whether the
// API trusts a header, and that must be visible in the first lines of a
// container's log rather than inferred from behaviour.
app.Logger.LogWarning(
    "Authentication: {Resolver}. OpenAPI console: {Swagger}.",
    allowDevelopmentPrincipal
        ? "DEVELOPMENT HEADER PRINCIPAL — the API trusts a request header and verifies no credential"
        : "bearer tokens, validated against the current security stamp on every request",
    swaggerEnabled ? "enabled" : "disabled");

// Liveness only. The Compose healthcheck (proposal §11) gates container
// start ordering on this; it deliberately does not touch the database —
// a health endpoint that fails when Postgres is briefly unreachable turns
// a recoverable blip into a restart loop.
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapSyncFacts();
app.MapReceipts();
app.MapWorkLeases();
app.MapReadModels();
app.MapUserAdministration();
app.MapAuthentication();

app.Run();

/// <summary>
/// Named so <c>WebApplicationFactory&lt;Program&gt;</c> can find the entry
/// point of a top-level-statements program from the integration tests.
/// </summary>
public partial class Program;
