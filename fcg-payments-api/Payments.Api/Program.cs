using MassTransit;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Payments.Api.Infrastructure.Persistence;
using Payments.Api.Consumers;
using Payments.Api.Infrastructure;
using Payments.Api.Observability;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c => c.SwaggerDoc("v1", new() { Title = "FCG Payments API", Version = "v1" }));

// SQLite no desenvolvimento local; PostgreSQL compartilhado entre as replicas em producao.
builder.Services.AddFcgDatabase<PaymentsDbContext>(builder.Configuration, builder.Environment.ContentRootPath);

builder.Services.Configure<PaymentSimulationOptions>(
    builder.Configuration.GetSection(PaymentSimulationOptions.SectionName));

builder.Services.AddFcgObservability();

var rabbitHost = builder.Configuration["RabbitMq:Host"] ?? "localhost";
// Credenciais do broker chegam por variavel de ambiente (Secret do Kubernetes);
// nenhuma senha fica no codigo. Sem elas, o MassTransit usa o padrao do broker local.
var rabbitUser = builder.Configuration["RabbitMq:Username"];
var rabbitPass = builder.Configuration["RabbitMq:Password"];

builder.Services.AddMassTransit(x =>
{
    x.AddConsumer<OrderPlacedConsumer>();
    x.UsingRabbitMq((context, cfg) =>
    {
        cfg.Host(rabbitHost, "/", h =>
        {
            if (!string.IsNullOrWhiteSpace(rabbitUser))
            {
                h.Username(rabbitUser);
                h.Password(rabbitPass ?? string.Empty);
            }
        });
        cfg.ConfigureEndpoints(context);
    });
});

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
    await DatabaseSetup.InitializeDatabaseAsync(
        db, app.Configuration, app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Database"));
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
