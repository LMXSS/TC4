using FCG.Messaging.Contracts;

namespace Payments.Api.Domain;

public class ProcessedOrder
{
    public Guid OrderId { get; set; }
    public Guid UserId { get; set; }
    public Guid GameId { get; set; }
    public decimal Price { get; set; }
    public PaymentStatus Status { get; set; }
    public DateTime ProcessedAtUtc { get; set; }
}
