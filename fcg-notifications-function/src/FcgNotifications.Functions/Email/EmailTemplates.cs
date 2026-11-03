namespace FcgNotifications.Functions.Email;

/// <summary>Corpos HTML das notificacoes transacionais da FCG.</summary>
public static class EmailTemplates
{
    public static string BoasVindas(string nome, Guid userId) => Layout(
        "Bem-vindo(a) ao FIAP Cloud Games!",
        $"""
         <p>Ola, <strong>{Escape(nome)}</strong>!</p>
         <p>Sua conta foi criada com sucesso e ja da acesso a todo o catalogo.</p>
         <p class="meta">UserId: {userId}</p>
         """);

    public static string CompraAprovada(Guid orderId, Guid gameId) => Layout(
        "Compra confirmada!",
        $"""
         <p>Seu pagamento foi <strong>aprovado</strong> e o jogo ja esta na sua biblioteca.</p>
         <ul>
           <li>Pedido: {orderId}</li>
           <li>Jogo: {gameId}</li>
         </ul>
         """);

    public static string CompraRecusada(Guid orderId, Guid gameId) => Layout(
        "Pagamento nao aprovado",
        $"""
         <p>Infelizmente seu pagamento foi <strong>recusado</strong> e a compra nao foi concluida.</p>
         <ul>
           <li>Pedido: {orderId}</li>
           <li>Jogo: {gameId}</li>
         </ul>
         <p>Nenhum valor foi cobrado. Voce pode tentar novamente quando quiser.</p>
         """);

    private static string Layout(string titulo, string conteudo) =>
        $"""
         <div style="font-family:Segoe UI,Arial,sans-serif;max-width:560px;color:#1f2933">
           <h2 style="color:#0b5fff;margin-bottom:4px">{Escape(titulo)}</h2>
           {conteudo}
           <hr style="border:none;border-top:1px solid #e4e7eb;margin:24px 0">
           <p style="font-size:12px;color:#7b8794">FIAP Cloud Games — mensagem automatica, nao responda.</p>
         </div>
         """;

    private static string Escape(string valor) => valor
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;");
}
