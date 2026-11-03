using FcgNotifications.Functions.Contracts;
using FcgNotifications.Functions.Email;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace FcgNotifications.Functions.Functions;

/// <summary>
/// E-mail de confirmacao ou de recusa do fluxo de compra, acionado pelo
/// PaymentProcessedEvent publicado pela PaymentsAPI.
/// </summary>
public class PaymentProcessedFunction
{
    private readonly IEmailSender _email;
    private readonly ILogger<PaymentProcessedFunction> _logger;

    public PaymentProcessedFunction(IEmailSender email, ILogger<PaymentProcessedFunction> logger)
    {
        _email = email;
        _logger = logger;
    }

    [Function(nameof(PaymentProcessedFunction))]
    public async Task Run(
        [RabbitMQTrigger("%RabbitMqPaymentProcessedQueue%", ConnectionStringSetting = "RabbitMqConnection")]
        string message,
        FunctionContext context)
    {
        var evento = MassTransitEnvelope.Unwrap<PaymentProcessedEvent>(message);

        _logger.LogInformation(
            "PaymentProcessedEvent recebido OrderId={OrderId} Status={Status} InvocationId={InvocationId}",
            evento.OrderId, evento.Status, context.InvocationId);

        // Fallback para pedidos antigos, publicados antes do UserEmail entrar no contrato.
        var destinatario = string.IsNullOrWhiteSpace(evento.UserEmail)
            ? $"user-{evento.UserId:N}@fcg.local"
            : evento.UserEmail;

        var aprovado = evento.Status == PaymentStatus.Approved;

        await _email.SendAsync(
            destinatario,
            "Cliente FCG",
            aprovado ? "Confirmacao de compra — FCG" : "Pagamento recusado — FCG",
            aprovado
                ? EmailTemplates.CompraAprovada(evento.OrderId, evento.GameId)
                : EmailTemplates.CompraRecusada(evento.OrderId, evento.GameId),
            context.CancellationToken);

        _logger.LogInformation(
            "Notificacao de pagamento enviada OrderId={OrderId} Status={Status} Destino={Email}",
            evento.OrderId, evento.Status, destinatario);
    }
}
