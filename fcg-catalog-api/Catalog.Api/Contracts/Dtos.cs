namespace Catalog.Api.Contracts;

public record ApiMessageResponse(string Message);

public record CreateGameRequest(string Titulo, string Genero, decimal Preco);

public record UpdateGameRequest(string Titulo, string Genero, decimal Preco);

public record GameResponse(Guid Id, string Titulo, string Genero, decimal Preco);

public record StartPurchaseResponse(Guid OrderId, string Message);
