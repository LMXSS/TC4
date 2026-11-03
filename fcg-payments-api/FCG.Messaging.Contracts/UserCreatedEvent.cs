namespace FCG.Messaging.Contracts;

public record UserCreatedEvent(Guid UserId, string Email, string Name);
