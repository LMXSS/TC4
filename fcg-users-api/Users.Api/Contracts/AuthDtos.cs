namespace Users.Api.Contracts;

public record RegisterRequest(string Name, string Email, string Password);

public record LoginRequest(string Email, string Password);

public record AuthResponse(string Token, DateTime ExpiresAtUtc, Guid UserId, string Name, string Email, string Role);
