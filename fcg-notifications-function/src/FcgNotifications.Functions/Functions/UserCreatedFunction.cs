using FcgNotifications.Functions.Contracts;
using FcgNotifications.Functions.Email;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace FcgNotifications.Functions.Functions;

/// <summary>
/// E-mail de boas-vindas do fluxo de cadastro.
///
/// Substitui o consumer que rodava 24/7 no container do NotificationsAPI: o host so
/// materializa esta funcao quando chega mensagem na fila, e volta a escala zero depois.
/// </summary>
public class UserCreatedFunction
{
    private readonly IEmailSender _email;
    private readonly ILogger<UserCreatedFunction> _logger;

    public UserCreatedFunction(IEmailSender email, ILogger<UserCreatedFunction> logger)
    {
        _email = email;
        _logger = logger;
    }

    [Function(nameof(UserCreatedFunction))]
    public async Task Run(
        [RabbitMQTrigger("%RabbitMqUserCreatedQueue%", ConnectionStringSetting = "RabbitMqConnection")]
        string message,
        FunctionContext context)
    {
        var evento = MassTransitEnvelope.Unwrap<UserCreatedEvent>(message);

        _logger.LogInformation(
            "UserCreatedEvent recebido UserId={UserId} Email={Email} InvocationId={InvocationId}",
            evento.UserId, evento.Email, context.InvocationId);

        await _email.SendAsync(
            evento.Email,
            evento.Name,
            "Boas-vindas — FIAP Cloud Games",
            EmailTemplates.BoasVindas(evento.Name, evento.UserId),
            context.CancellationToken);

        _logger.LogInformation("Boas-vindas enviada para {Email}", evento.Email);
    }
}
