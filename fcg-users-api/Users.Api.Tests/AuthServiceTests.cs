using System.Diagnostics.Metrics;
using FCG.Messaging.Contracts;
using MassTransit;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using Users.Api.Contracts;
using Users.Api.Domain.Constants;
using Users.Api.Infrastructure.Persistence;
using Users.Api.Infrastructure.Security;
using Users.Api.Infrastructure.Services;
using Users.Api.Observability;

namespace Users.Api.Tests;

/// <summary>
/// Regras de cadastro e login. O banco e um SQLite em memoria (mesmo provider de producao,
/// sem arquivo em disco) e o broker e substituido por um dublê de <see cref="IPublishEndpoint"/>.
/// </summary>
public sealed class AuthServiceTests : IDisposable
{
    private readonly SqliteConnection _conexao;
    private readonly UsersDbContext _db;
    private readonly ServiceProvider _servicos;
    private readonly IPublishEndpoint _publish = Substitute.For<IPublishEndpoint>();
    private readonly AuthService _auth;

    public AuthServiceTests()
    {
        _conexao = new SqliteConnection("Data Source=:memory:");
        _conexao.Open();

        _db = new UsersDbContext(new DbContextOptionsBuilder<UsersDbContext>().UseSqlite(_conexao).Options);
        _db.Database.EnsureCreated();

        _servicos = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var metrics = new UsersMetrics(_servicos.GetRequiredService<IMeterFactory>());

        var jwt = new JwtTokenGenerator(Options.Create(new JwtOptions
        {
            Key = new string('k', 48),
            Issuer = "FCG",
            Audience = "FCG",
            ExpiresMinutes = 60
        }));

        _auth = new AuthService(new UsuarioRepository(_db), jwt, _publish, metrics);
    }

    [Fact]
    public async Task RegisterAsync_cria_usuario_com_senha_em_hash_e_perfil_usuario()
    {
        var resposta = await _auth.RegisterAsync(new RegisterRequest("Ana Souza", "  ANA@fcg.local ", "Senha@123"));

        var salvo = await _db.Usuarios.SingleAsync();
        Assert.Equal("ana@fcg.local", salvo.Email);
        Assert.Equal(Roles.Usuario, salvo.Perfil);
        Assert.NotEqual("Senha@123", salvo.SenhaHash);
        Assert.True(BCrypt.Net.BCrypt.Verify("Senha@123", salvo.SenhaHash));

        Assert.Equal(salvo.Id, resposta.UserId);
        Assert.False(string.IsNullOrWhiteSpace(resposta.Token));
        Assert.True(resposta.ExpiresAtUtc > DateTime.UtcNow);
    }

    [Fact]
    public async Task RegisterAsync_publica_UserCreatedEvent_para_a_funcao_de_notificacoes()
    {
        var resposta = await _auth.RegisterAsync(new RegisterRequest("Ana Souza", "ana@fcg.local", "Senha@123"));

        await _publish.Received(1).Publish(
            Arg.Is<UserCreatedEvent>(e => e.UserId == resposta.UserId && e.Email == "ana@fcg.local" && e.Name == "Ana Souza"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RegisterAsync_recusa_email_duplicado_sem_publicar_evento()
    {
        await _auth.RegisterAsync(new RegisterRequest("Ana", "ana@fcg.local", "Senha@123"));
        _publish.ClearReceivedCalls();

        var erro = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _auth.RegisterAsync(new RegisterRequest("Outra Ana", "ANA@fcg.local", "Senha@456")));

        Assert.Contains("ja cadastrado", erro.Message);
        await _publish.DidNotReceiveWithAnyArgs().Publish<UserCreatedEvent>(default!, default);
        Assert.Equal(1, await _db.Usuarios.CountAsync());
    }

    [Theory]
    [InlineData("", "ana@fcg.local", "Senha@123", "Nome")]
    [InlineData("Ana", "email-invalido", "Senha@123", "E-mail")]
    [InlineData("Ana", "ana@fcg.local", "fraca", "Senha")]
    public async Task RegisterAsync_valida_os_dados_antes_de_gravar(string nome, string email, string senha, string trecho)
    {
        var erro = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _auth.RegisterAsync(new RegisterRequest(nome, email, senha)));

        Assert.Contains(trecho, erro.Message);
        Assert.Equal(0, await _db.Usuarios.CountAsync());
    }

    [Fact]
    public async Task LoginAsync_com_credenciais_corretas_devolve_token()
    {
        await _auth.RegisterAsync(new RegisterRequest("Ana", "ana@fcg.local", "Senha@123"));

        var resposta = await _auth.LoginAsync(new LoginRequest("Ana@FCG.local", "Senha@123"));

        Assert.Equal("ana@fcg.local", resposta.Email);
        Assert.False(string.IsNullOrWhiteSpace(resposta.Token));
    }

    [Theory]
    [InlineData("ana@fcg.local", "SenhaErrada@1")]
    [InlineData("ninguem@fcg.local", "Senha@123")]
    public async Task LoginAsync_com_credenciais_erradas_responde_nao_autorizado(string email, string senha)
    {
        await _auth.RegisterAsync(new RegisterRequest("Ana", "ana@fcg.local", "Senha@123"));

        var erro = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _auth.LoginAsync(new LoginRequest(email, senha)));

        // Mesma mensagem nos dois casos: nao revela se o e-mail existe.
        Assert.Equal("Credenciais invalidas.", erro.Message);
    }

    public void Dispose()
    {
        _db.Dispose();
        _conexao.Dispose();
        _servicos.Dispose();
    }
}
