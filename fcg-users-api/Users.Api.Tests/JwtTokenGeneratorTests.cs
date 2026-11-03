using System.IdentityModel.Tokens.Jwt;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Users.Api.Domain.Constants;
using Users.Api.Domain.Entities;
using Users.Api.Infrastructure.Security;

namespace Users.Api.Tests;

public class JwtTokenGeneratorTests
{
    // Chave gerada so para o teste — nao e usada em nenhum ambiente.
    private static readonly string ChaveDeTeste = Convert.ToBase64String(Guid.NewGuid().ToByteArray()) +
                                                  Convert.ToBase64String(Guid.NewGuid().ToByteArray());

    private static JwtTokenGenerator CriarGerador(int expiresMinutes = 60) =>
        new(Options.Create(new JwtOptions
        {
            Key = ChaveDeTeste,
            Issuer = "FCG",
            Audience = "FCG",
            ExpiresMinutes = expiresMinutes
        }));

    [Fact]
    public void CreateToken_emite_token_assinado_e_valido_para_issuer_e_audience()
    {
        var usuario = new Usuario("Ana Souza", "ana@fcg.local", "hash", Roles.Usuario);
        var agora = DateTime.UtcNow;

        var token = CriarGerador().CreateToken(usuario, agora);

        var principal = new JwtSecurityTokenHandler { MapInboundClaims = false }.ValidateToken(token,
            new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(ChaveDeTeste)),
                ValidIssuer = "FCG",
                ValidAudience = "FCG",
                ValidateLifetime = true
            },
            out var validado);

        Assert.Equal(SecurityAlgorithms.HmacSha256, ((JwtSecurityToken)validado).Header.Alg);
        Assert.Equal(usuario.Id.ToString(), principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value);
        Assert.Equal("ana@fcg.local", principal.FindFirst(JwtRegisteredClaimNames.Email)?.Value);
        Assert.Equal("Ana Souza", principal.FindFirst("name")?.Value);
        Assert.Equal(Roles.Usuario, principal.FindFirst("role")?.Value);
        Assert.Equal("1", principal.FindFirst(JwtCustomClaims.CredencialVersao)?.Value);
    }

    [Fact]
    public void CreateToken_nao_valida_com_outra_chave()
    {
        var token = CriarGerador().CreateToken(new Usuario("Ana", "ana@fcg.local", "hash"), DateTime.UtcNow);
        var outraChave = new string('x', 48);

        Assert.ThrowsAny<SecurityTokenException>(() =>
            new JwtSecurityTokenHandler().ValidateToken(token,
                new TokenValidationParameters
                {
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(outraChave)),
                    ValidIssuer = "FCG",
                    ValidAudience = "FCG"
                },
                out _));
    }

    [Theory]
    [InlineData(120, 120)]
    [InlineData(5, 5)]
    [InlineData(1, 5)]
    [InlineData(0, 5)]
    public void GetExpiryUtc_respeita_o_minimo_de_5_minutos(int configurado, int esperado)
    {
        var agora = new DateTime(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc);

        var expira = CriarGerador(configurado).GetExpiryUtc(agora);

        Assert.Equal(agora.AddMinutes(esperado), expira);
    }
}
