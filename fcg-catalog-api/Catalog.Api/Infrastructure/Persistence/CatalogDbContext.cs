using Catalog.Api.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Catalog.Api.Infrastructure.Persistence;

public class CatalogDbContext : DbContext
{
    public CatalogDbContext(DbContextOptions<CatalogDbContext> options) : base(options) { }

    public DbSet<Jogo> Jogos => Set<Jogo>();
    public DbSet<UsuarioJogo> UsuarioJogos => Set<UsuarioJogo>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Jogo>(entity =>
        {
            entity.ToTable("Jogos");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Titulo).HasMaxLength(300);
            entity.Property(e => e.Genero).HasMaxLength(100);
            entity.Property(e => e.Preco).HasPrecision(18, 2);
            entity.HasQueryFilter(e => e.Ativo);
        });

        modelBuilder.Entity<UsuarioJogo>(entity =>
        {
            entity.ToTable("UsuarioJogos");
            entity.HasKey(e => new { e.UsuarioId, e.JogoId });
            entity.HasOne<Jogo>().WithMany().HasForeignKey(e => e.JogoId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
