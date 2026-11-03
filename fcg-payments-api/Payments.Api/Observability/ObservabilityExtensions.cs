using System.Diagnostics.Metrics;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;

namespace Payments.Api.Observability;

/// <summary>
/// Instrumentacao de metricas no formato Prometheus (Fase 3 - Opcao A: Prometheus + Grafana).
/// Expoe /metrics para o scrape do Prometheus.
/// </summary>
public static class ObservabilityExtensions
{
    public const string ServiceName = "payments-api";

    public static IServiceCollection AddFcgObservability(this IServiceCollection services)
    {
        services.AddSingleton<PaymentsMetrics>();

        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(
                serviceName: ServiceName,
                serviceVersion: typeof(ObservabilityExtensions).Assembly.GetName().Version?.ToString() ?? "1.0.0",
                serviceInstanceId: Environment.MachineName))
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                .AddMeter("MassTransit")
                .AddMeter(PaymentsMetrics.MeterName)
                .AddPrometheusExporter());

        return services;
    }
}

/// <summary>Metricas de negocio da Payments API.</summary>
public class PaymentsMetrics
{
    public const string MeterName = "FCG.Payments";

    private readonly Counter<long> _pagamentosProcessados;
    private readonly Histogram<double> _valorProcessado;

    public PaymentsMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);
        _pagamentosProcessados = meter.CreateCounter<long>(
            "fcg_payments_processed", unit: null, description: "Total de pagamentos processados por status.");
        _valorProcessado = meter.CreateHistogram<double>(
            "fcg_payments_amount", unit: null, description: "Distribuicao do valor dos pedidos processados.");
    }

    public void PagamentoProcessado(string status, decimal valor)
    {
        var tag = new KeyValuePair<string, object?>("status", status.ToLowerInvariant());
        _pagamentosProcessados.Add(1, tag);
        _valorProcessado.Record((double)valor, tag);
    }
}
