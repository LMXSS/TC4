namespace FCG.Messaging.Contracts;

public enum PaymentStatus
{
    Approved,
    Rejected
}

public record PaymentProcessedEvent(Guid OrderId, Guid UserId, Guid GameId, PaymentStatus Status, string UserEmail);
