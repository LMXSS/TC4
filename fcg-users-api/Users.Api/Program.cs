using System.Text;
using MassTransit;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Users.Api.Infrastructure.Persistence;
using Users.Api.Infrastructure.Security;
using Users.Api.Infrastructure.Seed;
using Users.Api.Infrastructure.Services;
using Users.Api.Middleware;
using Users.Api.Observability;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c => c.SwaggerDoc("v1", new() { Title = "FCG Users API", Version = "v1" }));

// SQLite no desenvolvimento local; PostgreSQL compartilhado entre as replicas em producao.
builder.Services.AddFcgDatabase<UsersDbContext>(builder.Configuration, builder.Environment.ContentRootPath);

builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection(JwtOptions.SectionName));
builder.Services.Configure<DevAdminSeedOptions>(builder.Configuration.GetSection(DevAdminSeedOptions.SectionName));
builder.Services.AddSingleton<JwtTokenGenerator>();
builder.Services.AddScoped<UsuarioRepository>();
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<DevAdminSeeder>();

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
    var db = scope.ServiceProvider.GetRequiredService<UsersDbContext>();
    await DatabaseSetup.InitializeDatabaseAsync(
        db, app.Configuration, app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Database"));
    var seeder = scope.ServiceProvider.GetRequiredService<DevAdminSeeder>();
    await seeder.SeedAsync();
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

app.MapGet("/health/live", () => Results.Ok(new { status = "ok", service = "users-api" }))
    .WithTags("Health").AllowAnonymous();

app.MapGet("/health/ready", async (UsersDbContext db, CancellationToken ct) =>
    await db.Database.CanConnectAsync(ct)
        ? Results.Ok(new { status = "ready", service = "users-api" })
        : Results.StatusCode(StatusCodes.Status503ServiceUnavailable))
    .WithTags("Health").AllowAnonymous();

app.Run();
