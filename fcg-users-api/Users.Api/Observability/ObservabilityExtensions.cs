using System.Diagnostics.Metrics;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;

namespace Users.Api.Observability;

/// <summary>
/// Instrumentacao de metricas no formato Prometheus (Fase 3 - Opcao A: Prometheus + Grafana).
/// Expoe /metrics para o scrape do Prometheus.
/// </summary>
public static class ObservabilityExtensions
{
    public const string ServiceName = "users-api";

    public static IServiceCollection AddFcgObservability(this IServiceCollection services)
    {
        services.AddSingleton<UsersMetrics>();

        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(
                serviceName: ServiceName,
                serviceVersion: typeof(ObservabilityExtensions).Assembly.GetName().Version?.ToString() ?? "1.0.0",
                serviceInstanceId: Environment.MachineName))
            .WithMetrics(metrics => metrics
                // Latencia, throughput e status code das requisicoes HTTP (Microsoft.AspNetCore.Hosting).
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                // CPU, memoria, GC e thread pool do processo .NET.
                .AddRuntimeInstrumentation()
                // Publicacao/consumo de mensagens.
                .AddMeter("MassTransit")
                .AddMeter(UsersMetrics.MeterName)
                .AddPrometheusExporter());

        return services;
    }
}

/// <summary>Metricas de negocio da Users API.</summary>
public class UsersMetrics
{
    public const string MeterName = "FCG.Users";

    private readonly Counter<long> _usuariosRegistrados;
    private readonly Counter<long> _loginsRealizados;

    public UsersMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);
        _usuariosRegistrados = meter.CreateCounter<long>(
            "fcg_users_registered", unit: null, description: "Total de usuarios registrados com sucesso.");
        _loginsRealizados = meter.CreateCounter<long>(
            "fcg_users_logins", unit: null, description: "Total de tentativas de login por resultado.");
    }

    public void UsuarioRegistrado() => _usuariosRegistrados.Add(1);

    public void LoginRealizado(bool sucesso) =>
        _loginsRealizados.Add(1, new KeyValuePair<string, object?>("result", sucesso ? "success" : "failure"));
}
