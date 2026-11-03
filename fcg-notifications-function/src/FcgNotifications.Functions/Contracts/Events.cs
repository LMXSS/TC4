namespace FcgNotifications.Functions.Contracts;

/// <summary>
/// Copia dos contratos publicados pelos microsservicos (FCG.Messaging.Contracts).
/// A funcao le direto da fila, entao depende apenas do formato do evento — nao do
/// pacote .NET dos servicos. Manter estes tipos alinhados com o repositorio de contratos.
/// </summary>
public record UserCreatedEvent(Guid UserId, string Email, string Name);

public record PaymentProcessedEvent(
    Guid OrderId,
    Guid UserId,
    Guid GameId,
    PaymentStatus Status,
    string UserEmail);

public enum PaymentStatus
{
    Approved,
    Rejected
}
