namespace FCG.Messaging.Contracts;

/// <summary>Pedido de compra publicado pelo Catalog; Payments consome e responde com PaymentProcessedEvent.</summary>
public record OrderPlacedEvent(Guid OrderId, Guid UserId, Guid GameId, decimal Price, string UserEmail);
