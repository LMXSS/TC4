using System.Text.Json;
using System.Text.Json.Serialization;

namespace FcgNotifications.Functions.Contracts;

/// <summary>
/// Envelope que o MassTransit coloca em volta de cada mensagem publicada.
///
/// Na Fase 2 o NotificationsAPI usava o proprio MassTransit para consumir, e o
/// desempacotamento era transparente. A funcao serverless e acionada pelo trigger nativo
/// do RabbitMQ e recebe o corpo bruto da mensagem, entao o envelope precisa ser lido aqui.
/// </summary>
public class MassTransitEnvelope<T>
{
    [JsonPropertyName("messageId")]
    public string? MessageId { get; set; }

    [JsonPropertyName("conversationId")]
    public string? ConversationId { get; set; }

    [JsonPropertyName("correlationId")]
    public string? CorrelationId { get; set; }

    [JsonPropertyName("sourceAddress")]
    public string? SourceAddress { get; set; }

    [JsonPropertyName("messageType")]
    public string[]? MessageType { get; set; }

    [JsonPropertyName("message")]
    public T? Message { get; set; }

    [JsonPropertyName("sentTime")]
    public DateTimeOffset? SentTime { get; set; }
}

public static class MassTransitEnvelope
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        // O MassTransit serializa enums como texto; o converter tambem aceita valor numerico.
        Converters = { new JsonStringEnumConverter(allowIntegerValues: true) }
    };

    /// <summary>
    /// Extrai o payload do envelope. Mensagens publicadas fora do MassTransit (um teste
    /// manual pelo painel do RabbitMQ, por exemplo) chegam sem envelope; nesse caso o
    /// proprio corpo e desserializado como o evento.
    /// </summary>
    public static T Unwrap<T>(string body) where T : class
    {
        if (string.IsNullOrWhiteSpace(body))
            throw new InvalidOperationException("Mensagem vazia.");

        var envelope = JsonSerializer.Deserialize<MassTransitEnvelope<T>>(body, Options);
        if (envelope?.Message is not null)
            return envelope.Message;

        return JsonSerializer.Deserialize<T>(body, Options)
            ?? throw new InvalidOperationException($"Nao foi possivel desserializar a mensagem como {typeof(T).Name}.");
    }
}
