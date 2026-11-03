using FCG.Messaging.Contracts;
using MassTransit;
using Catalog.Api.Infrastructure.Services;

namespace Catalog.Api.Consumers;

public class PaymentProcessedConsumer : IConsumer<PaymentProcessedEvent>
{
    private readonly LibraryService _library;
    private readonly ILogger<PaymentProcessedConsumer> _logger;

    public PaymentProcessedConsumer(LibraryService library, ILogger<PaymentProcessedConsumer> logger)
    {
        _library = library;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<PaymentProcessedEvent> context)
    {
        var m = context.Message;
        if (m.Status != PaymentStatus.Approved)
            return;

        await _library.ConfirmPurchaseAsync(m.UserId, m.GameId, context.CancellationToken);
        _logger.LogInformation("Biblioteca atualizada UserId={UserId} GameId={GameId} OrderId={OrderId}", m.UserId, m.GameId, m.OrderId);
    }
}
