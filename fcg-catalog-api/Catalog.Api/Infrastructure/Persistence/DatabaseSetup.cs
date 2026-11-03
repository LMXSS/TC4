using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Catalog.Api.Infrastructure.Persistence;

/// <summary>
/// Escolha do banco relacional por configuracao (<c>Database:Provider</c>).
///
/// * <b>Sqlite</b> (padrao): desenvolvimento local e testes. Schema por migrations do EF Core.
/// * <b>Postgres</b>: producao no Kubernetes (Fase 4). Todas as replicas compartilham o mesmo
///   banco, o que o SQLite dentro do pod nao permite, e os dados sobrevivem ao rolling update.
///   O schema e criado a partir do modelo com <c>EnsureCreated</c>, porque as migrations
///   versionadas foram geradas para o SQLite.
/// </summary>
public static class DatabaseSetup
{
    public const string ProviderPostgres = "Postgres";

    public static bool UsaPostgres(IConfiguration configuration) =>
        string.Equals(configuration["Database:Provider"], ProviderPostgres, StringComparison.OrdinalIgnoreCase);

    public static IServiceCollection AddFcgDatabase<TContext>(
        this IServiceCollection services,
        IConfiguration configuration,
        string contentRoot)
        where TContext : DbContext
    {
        var connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("ConnectionStrings:DefaultConnection ausente.");

        if (UsaPostgres(configuration))
            services.AddDbContext<TContext>(o => o.UseNpgsql(connectionString));
        else
            services.AddDbContext<TContext>(o => o.UseSqlite(ResolveSqlitePath(connectionString, contentRoot)));

        return services;
    }

    /// <summary>
    /// Prepara o schema no start. No PostgreSQL tenta de novo com espera crescente: logo apos
    /// um deploy o banco pode ainda estar subindo, e duas replicas iniciando juntas podem
    /// disputar a criacao das tabelas — a segunda tentativa ja encontra tudo criado.
    /// </summary>
    public static async Task InitializeDatabaseAsync(
        DbContext db,
        IConfiguration configuration,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        if (!UsaPostgres(configuration))
        {
            await db.Database.MigrateAsync(cancellationToken);
            return;
        }

        const int maxTentativas = 12;
        for (var tentativa = 1; ; tentativa++)
        {
            try
            {
                var criado = await db.Database.EnsureCreatedAsync(cancellationToken);
                logger.LogInformation(criado
                    ? "Schema criado no PostgreSQL."
                    : "Schema do PostgreSQL ja existia.");
                return;
            }
            catch (Exception ex) when (tentativa < maxTentativas && !cancellationToken.IsCancellationRequested)
            {
                var espera = TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, tentativa)));
                logger.LogWarning(ex, "PostgreSQL indisponivel (tentativa {Tentativa}/{Max}). Nova tentativa em {Espera}s.",
                    tentativa, maxTentativas, espera.TotalSeconds);
                await Task.Delay(espera, cancellationToken);
            }
        }
    }

    private static string ResolveSqlitePath(string connectionString, string contentRoot)
    {
        var b = new SqliteConnectionStringBuilder(connectionString);
        var ds = b.DataSource;
        if (string.IsNullOrEmpty(ds) || ds.Equals(":memory:", StringComparison.OrdinalIgnoreCase) || Path.IsPathRooted(ds))
            return connectionString;
        b.DataSource = Path.GetFullPath(Path.Combine(contentRoot, ds));
        return b.ConnectionString;
    }
}
