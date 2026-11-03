using Microsoft.EntityFrameworkCore;
using Users.Api.Domain.Entities;

namespace Users.Api.Infrastructure.Persistence;

public class UsuarioRepository
{
    private readonly UsersDbContext _db;

    public UsuarioRepository(UsersDbContext db) => _db = db;

    public async Task<Usuario?> GetByEmailAsync(string email, CancellationToken cancellationToken = default) =>
        await _db.Usuarios.AsNoTracking().FirstOrDefaultAsync(u => u.Email == email, cancellationToken);

    public Task<bool> EmailExistsAsync(string normalizedEmail, CancellationToken cancellationToken = default) =>
        _db.Usuarios.AnyAsync(u => u.Email == normalizedEmail, cancellationToken);

    public async Task AddAsync(Usuario usuario, CancellationToken cancellationToken = default)
    {
        await _db.Usuarios.AddAsync(usuario, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
    }
}
