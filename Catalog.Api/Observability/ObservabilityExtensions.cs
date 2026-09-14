using System.Diagnostics.Metrics;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;

namespace Catalog.Api.Observability;

/// <summary>
/// Instrumentacao de metricas no formato Prometheus (Fase 3 - Opcao A: Prometheus + Grafana).
/// Expoe /metrics para o scrape do Prometheus.
/// </summary>
public static class ObservabilityExtensions
{
    public const string ServiceName = "catalog-api";

    public static IServiceCollection AddFcgObservability(this IServiceCollection services)
    {
        services.AddSingleton<CatalogMetrics>();

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
                .AddMeter(CatalogMetrics.MeterName)
                .AddPrometheusExporter());

        return services;
    }
}

/// <summary>Metricas de negocio e de cache da Catalog API.</summary>
public class CatalogMetrics
{
    public const string MeterName = "FCG.Catalog";

    private readonly Counter<long> _cacheRequests;
    private readonly Counter<long> _comprasIniciadas;
    private readonly Counter<long> _comprasConfirmadas;
    private readonly Counter<long> _avaliacoesRegistradas;
    private readonly Histogram<double> _mongoDuration;

    public CatalogMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);

        _cacheRequests = meter.CreateCounter<long>(
            "fcg_catalog_cache_requests", unit: null,
            description: "Consultas ao cache distribuido (Redis) por resultado (hit/miss) e por chave logica.");
        _comprasIniciadas = meter.CreateCounter<long>(
            "fcg_catalog_purchases_started", unit: null, description: "Pedidos de compra publicados (OrderPlacedEvent).");
        _comprasConfirmadas = meter.CreateCounter<long>(
            "fcg_catalog_purchases_confirmed", unit: null, description: "Compras confirmadas na biblioteca apos pagamento aprovado.");
        _avaliacoesRegistradas = meter.CreateCounter<long>(
            "fcg_catalog_reviews", unit: null, description: "Avaliacoes de jogos gravadas no MongoDB.");
        _mongoDuration = meter.CreateHistogram<double>(
            "fcg_catalog_mongo_operation_duration", unit: "s", description: "Duracao das operacoes no MongoDB por tipo.");
    }

    public void CacheHit(string cacheKind) => _cacheRequests.Add(1,
        new KeyValuePair<string, object?>("result", "hit"),
        new KeyValuePair<string, object?>("cache", cacheKind));

    public void CacheMiss(string cacheKind) => _cacheRequests.Add(1,
        new KeyValuePair<string, object?>("result", "miss"),
        new KeyValuePair<string, object?>("cache", cacheKind));

    public void CompraIniciada() => _comprasIniciadas.Add(1);

    public void CompraConfirmada() => _comprasConfirmadas.Add(1);

    public void AvaliacaoRegistrada() => _avaliacoesRegistradas.Add(1);

    public void MongoOperacao(string operation, TimeSpan elapsed) =>
        _mongoDuration.Record(elapsed.TotalSeconds, new KeyValuePair<string, object?>("operation", operation));
}
