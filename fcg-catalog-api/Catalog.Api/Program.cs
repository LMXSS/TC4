using System.Text;
using Catalog.Api.Consumers;
using Catalog.Api.Infrastructure.Caching;
using Catalog.Api.Infrastructure.Mongo;
using Catalog.Api.Infrastructure.Persistence;
using Catalog.Api.Infrastructure.Search;
using Catalog.Api.Infrastructure.Security;
using Catalog.Api.Infrastructure.Services;
using Catalog.Api.Middleware;
using Catalog.Api.Observability;
using MongoDB.Driver;
using MassTransit;
using Microsoft.AspNetCore.Authentication.JwtBearer;
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

// SQLite no desenvolvimento local; PostgreSQL compartilhado entre as replicas em producao.
builder.Services.AddFcgDatabase<CatalogDbContext>(builder.Configuration, builder.Environment.ContentRootPath);

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

// ---- Busca avancada (Elasticsearch / OpenSearch) ----
builder.Services.Configure<ElasticsearchOptions>(builder.Configuration.GetSection(ElasticsearchOptions.SectionName));
builder.Services.AddHttpClient<IGameSearchIndex, ElasticsearchGameIndex>((sp, http) =>
    ElasticsearchGameIndex.Configure(http, sp.GetRequiredService<IOptions<ElasticsearchOptions>>().Value));
builder.Services.AddScoped<GameSearchIndexer>();
builder.Services.AddScoped<GameSearchService>();
builder.Services.AddHostedService<SearchIndexInitializer>();

builder.Services.AddScoped<GameService>();
builder.Services.AddScoped<LibraryService>();
builder.Services.AddScoped<GameDetailsService>();
builder.Services.AddScoped<ICatalogTextSearch>(sp => sp.GetRequiredService<GameDetailsService>());
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
// Credenciais do broker chegam por variavel de ambiente (Secret do Kubernetes);
// nenhuma senha fica no codigo. Sem elas, o MassTransit usa o padrao do broker local.
var rabbitUser = builder.Configuration["RabbitMq:Username"];
var rabbitPass = builder.Configuration["RabbitMq:Password"];

builder.Services.AddMassTransit(x =>
{
    x.AddConsumer<PaymentProcessedConsumer>();
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
    var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
    await DatabaseSetup.InitializeDatabaseAsync(
        db, app.Configuration, app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Database"));
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
        ? Results.Ok(new { status = "ready", service = "catalog-api", relacional = true, mongo = true })
        : Results.Json(
            new { status = "degraded", service = "catalog-api", relacional, mongo = documental },
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
