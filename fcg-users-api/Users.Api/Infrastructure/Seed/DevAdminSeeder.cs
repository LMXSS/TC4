using static BCrypt.Net.BCrypt;
using Users.Api.Domain.Constants;
using Users.Api.Domain.Entities;
using Users.Api.Domain.Services;
using Users.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Users.Api.Infrastructure.Seed;

public class DevAdminSeeder
{
    private readonly UsuarioRepository _usuarios;
    private readonly DevAdminSeedOptions _options;

    public DevAdminSeeder(UsuarioRepository usuarios, IOptions<DevAdminSeedOptions> options)
    {
        _usuarios = usuarios;
        _options = options.Value;
    }

    public async Task SeedAsync(CancellationToken cancellationToken = default)
    {
        var email = _options.AdminEmail?.Trim().ToLowerInvariant();
        var password = _options.AdminPassword;
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            return;

        if (await _usuarios.EmailExistsAsync(email, cancellationToken))
            return;

        if (!CredentialValidation.IsStrongPassword(password, out _))
            return;

        var hash = HashPassword(password);
        var admin = new Usuario(_options.AdminName.Trim(), email, hash, Roles.Administrador);
        try
        {
            await _usuarios.AddAsync(admin, cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Duas replicas iniciando juntas no mesmo banco: a outra ja criou o admin.
        }
    }
}
