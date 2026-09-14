using MassTransit;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Payments.Api.Consumers;
using Payments.Api.Infrastructure;
using Payments.Api.Observability;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c => c.SwaggerDoc("v1", new() { Title = "FCG Payments API", Version = "v1" }));

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("DefaultConnection ausente.");
connectionString = ResolveSqlitePath(connectionString, builder.Environment.ContentRootPath);
builder.Services.AddDbContext<PaymentsDbContext>(o => o.UseSqlite(connectionString));

builder.Services.Configure<PaymentSimulationOptions>(
    builder.Configuration.GetSection(PaymentSimulationOptions.SectionName));

builder.Services.AddFcgObservability();

var rabbitHost = builder.Configuration["RabbitMq:Host"] ?? "localhost";
var rabbitUser = builder.Configuration["RabbitMq:Username"] ?? "guest";
var rabbitPass = builder.Configuration["RabbitMq:Password"] ?? "guest";

builder.Services.AddMassTransit(x =>
{
    x.AddConsumer<OrderPlacedConsumer>();
    x.UsingRabbitMq((context, cfg) =>
    {
        cfg.Host(rabbitHost, "/", h =>
        {
            h.Username(rabbitUser);
            h.Password(rabbitPass);
        });
        cfg.ConfigureEndpoints(context);
    });
});

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
    await db.Database.MigrateAsync();
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapControllers();

// Endpoint de scrape do Prometheus (nao exige autenticacao; exposto so na rede interna).
app.MapPrometheusScrapingEndpoint();

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "payments-api" }));
app.MapGet("/health/live", () => Results.Ok(new { status = "ok", service = "payments-api" }));
app.MapGet("/health/ready", async (PaymentsDbContext db, CancellationToken ct) =>
    await db.Database.CanConnectAsync(ct)
        ? Results.Ok(new { status = "ready", service = "payments-api" })
        : Results.StatusCode(StatusCodes.Status503ServiceUnavailable));

app.Run();

static string ResolveSqlitePath(string connectionString, string contentRoot)
{
    var b = new SqliteConnectionStringBuilder(connectionString);
    var ds = b.DataSource;
    if (string.IsNullOrEmpty(ds) || ds.Equals(":memory:", StringComparison.OrdinalIgnoreCase) || Path.IsPathRooted(ds))
        return connectionString;
    b.DataSource = Path.GetFullPath(Path.Combine(contentRoot, ds));
    return b.ConnectionString;
}
