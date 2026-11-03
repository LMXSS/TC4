using FCG.Messaging.Contracts;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Payments.Api.Domain;
using Payments.Api.Infrastructure;
using Payments.Api.Observability;

namespace Payments.Api.Consumers;

public class OrderPlacedConsumer : IConsumer<OrderPlacedEvent>
{
    private readonly PaymentsDbContext _db;
    private readonly ILogger<OrderPlacedConsumer> _logger;
    private readonly PaymentSimulationOptions _options;
    private readonly PaymentsMetrics _metrics;

    public OrderPlacedConsumer(
        PaymentsDbContext db,
        ILogger<OrderPlacedConsumer> logger,
        IOptions<PaymentSimulationOptions> options,
        PaymentsMetrics metrics)
    {
        _db = db;
        _logger = logger;
        _options = options.Value;
        _metrics = metrics;
    }

    public async Task Consume(ConsumeContext<OrderPlacedEvent> context)
    {
        var m = context.Message;

        if (await _db.ProcessedOrders.AnyAsync(o => o.OrderId == m.OrderId, context.CancellationToken))
            return;

        var status = PaymentSimulation.Resolve(_options, m.Price);

        _logger.LogInformation(
            "Pagamento simulado OrderId={OrderId} UserId={UserId} GameId={GameId} Price={Price} Mode={Mode} => {Status}",
            m.OrderId, m.UserId, m.GameId, m.Price, _options.SimulationMode, status);

        await context.Publish(
            new PaymentProcessedEvent(m.OrderId, m.UserId, m.GameId, status, m.UserEmail),
            context.CancellationToken);

        _metrics.PagamentoProcessado(status.ToString(), m.Price);

        _db.ProcessedOrders.Add(new ProcessedOrder
        {
            OrderId = m.OrderId,
            UserId = m.UserId,
            GameId = m.GameId,
            Price = m.Price,
            Status = status,
            ProcessedAtUtc = DateTime.UtcNow
        });

        try
        {
            await _db.SaveChangesAsync(context.CancellationToken);
        }
        catch (DbUpdateException)
        {
            // Idempotencia: reentrega do mesmo OrderId.
        }
    }
}
