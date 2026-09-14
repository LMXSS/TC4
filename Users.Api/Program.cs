using System.Text;
using MassTransit;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Data.Sqlite;
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

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("DefaultConnection ausente.");
connectionString = ResolveSqlitePath(connectionString, builder.Environment.ContentRootPath);
builder.Services.AddDbContext<UsersDbContext>(o => o.UseSqlite(connectionString));

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
var rabbitUser = builder.Configuration["RabbitMq:Username"] ?? "guest";
var rabbitPass = builder.Configuration["RabbitMq:Password"] ?? "guest";

builder.Services.AddMassTransit(x =>
{
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
    var db = scope.ServiceProvider.GetRequiredService<UsersDbContext>();
    await db.Database.MigrateAsync();
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

static string ResolveSqlitePath(string connectionString, string contentRoot)
{
    var b = new SqliteConnectionStringBuilder(connectionString);
    var ds = b.DataSource;
    if (string.IsNullOrEmpty(ds) || ds.Equals(":memory:", StringComparison.OrdinalIgnoreCase) || Path.IsPathRooted(ds))
        return connectionString;
    b.DataSource = Path.GetFullPath(Path.Combine(contentRoot, ds));
    return b.ConnectionString;
}
