using System.Text;
using Catalog.Api.Consumers;
using Catalog.Api.Infrastructure.Caching;
using Catalog.Api.Infrastructure.Mongo;
using Catalog.Api.Infrastructure.Persistence;
using Catalog.Api.Infrastructure.Security;
using Catalog.Api.Infrastructure.Services;
using Catalog.Api.Middleware;
using Catalog.Api.Observability;
using MongoDB.Driver;
using MassTransit;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo { Title = "FCG Catalog API", Version = "v1" });
    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Description = "JWT emitido pelo Users API (Authorization: Bearer {token})",
        Name = "Authorization",
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT"
    });
    options.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" }
            },
            Array.Empty<string>()
        }
    });
});

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("DefaultConnection ausente.");
connectionString = ResolveSqlitePath(connectionString, builder.Environment.ContentRootPath);
builder.Services.AddDbContext<CatalogDbContext>(o => o.UseSqlite(connectionString));

builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection(JwtOptions.SectionName));

// ---- Persistencia poliglota: MongoDB para o catalogo expandido e as avaliacoes ----
builder.Services.Configure<MongoOptions>(builder.Configuration.GetSection(MongoOptions.SectionName));
builder.Services.AddSingleton<IMongoClient>(sp =>
{
    var mongo = sp.GetRequiredService<IOptions<MongoOptions>>().Value;
    return new MongoClient(mongo.ConnectionString);
});
builder.Services.AddScoped<CatalogMongoContext>();
builder.Services.AddHostedService<MongoIndexInitializer>();

// ---- Cache distribuido (Redis) ----
builder.Services.Configure<CacheOptions>(builder.Configuration.GetSection(CacheOptions.SectionName));
var cacheOptions = builder.Configuration.GetSection(CacheOptions.SectionName).Get<CacheOptions>() ?? new CacheOptions();
if (cacheOptions.RedisEnabled)
{
    builder.Services.AddStackExchangeRedisCache(options =>
    {
        options.Configuration = cacheOptions.RedisConnectionString;
        options.InstanceName = cacheOptions.InstanceName;
    });
}
else
{
    // Sem Redis a API continua funcionando com cache no proprio processo.
    builder.Services.AddDistributedMemoryCache();
}
builder.Services.AddScoped<CatalogCache>();

builder.Services.AddScoped<GameService>();
builder.Services.AddScoped<LibraryService>();
builder.Services.AddScoped<GameDetailsService>();
builder.Services.AddScoped<ReviewService>();

var jwt = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>()
    ?? throw new InvalidOperationException("Jwt ausente.");
if (string.IsNullOrWhiteSpace(jwt.Key) || jwt.Key.Length < 32)
    throw new InvalidOperationException("Jwt:Key com pelo menos 32 caracteres.");

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.MapInboundClaims = false;
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Key)),
            ValidateIssuer = true,
            ValidIssuer = jwt.Issuer,
            ValidateAudience = true,
            ValidAudience = jwt.Audience,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(1),
            NameClaimType = "name",
            RoleClaimType = "role"
        };
    });
builder.Services.AddAuthorization();

builder.Services.AddFcgObservability();

var rabbitHost = builder.Configuration["RabbitMq:Host"] ?? "localhost";
var rabbitUser = builder.Configuration["RabbitMq:Username"] ?? "guest";
var rabbitPass = builder.Configuration["RabbitMq:Password"] ?? "guest";

builder.Services.AddMassTransit(x =>
{
    x.AddConsumer<PaymentProcessedConsumer>();
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
    var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
    await db.Database.MigrateAsync();
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseApiExceptionHandling();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

// Endpoint de scrape do Prometheus (nao exige autenticacao; exposto so na rede interna).
app.MapPrometheusScrapingEndpoint();

app.MapGet("/health/live", () => Results.Ok(new { status = "ok", service = "catalog-api" }))
    .WithTags("Health").AllowAnonymous();

app.MapGet("/health/ready", async (CatalogDbContext db, CatalogMongoContext mongo, CancellationToken ct) =>
{
    var relacional = await db.Database.CanConnectAsync(ct);
    var documental = await PingMongoAsync(mongo, ct);
    return relacional && documental
        ? Results.Ok(new { status = "ready", service = "catalog-api", sqlite = true, mongo = true })
        : Results.Json(
            new { status = "degraded", service = "catalog-api", sqlite = relacional, mongo = documental },
            statusCode: StatusCodes.Status503ServiceUnavailable);
}).WithTags("Health").AllowAnonymous();

app.Run();

static async Task<bool> PingMongoAsync(CatalogMongoContext mongo, CancellationToken cancellationToken)
{
    try
    {
        await mongo.Database.RunCommandAsync<MongoDB.Bson.BsonDocument>(
            new MongoDB.Bson.BsonDocument("ping", 1), cancellationToken: cancellationToken);
        return true;
    }
    catch
    {
        return false;
    }
}

static string ResolveSqlitePath(string connectionString, string contentRoot)
{
    var b = new SqliteConnectionStringBuilder(connectionString);
    var ds = b.DataSource;
    if (string.IsNullOrEmpty(ds) || ds.Equals(":memory:", StringComparison.OrdinalIgnoreCase) || Path.IsPathRooted(ds))
        return connectionString;
    b.DataSource = Path.GetFullPath(Path.Combine(contentRoot, ds));
    return b.ConnectionString;
}
